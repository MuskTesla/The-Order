using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class LiveSessionLibraryTests
{
    private static WorkspaceViewModel Workspace(IOfflineLibraryClient client) => new(client, new Preferences(), new("synthetic"), new LegacyClientTextRules());
    private static LibraryQueryResult Row(string name) => new([new("g", name, "planned", null, false, "", 1, 0)], 1, [], []);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task DurableStartAndExitUpdateListWithoutReloadOrDiscardingDirtyDetails()
    {
        await using var rig = new SessionTestRig(); var factory = new Factory(rig);
        await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = Workspace(client);
        await workspace.InitializeAsync();
        await workspace.SetQueryAsync(new(Text: "Synthetic", Sort: "recent"));
        await workspace.NavigateAsync(WorkspacePage.Details, "g", () => Task.FromResult(UnsavedChoice.Stay));
        var draft = workspace.Draft!; draft.Notes = "unsaved personal edit";
        await workspace.SynchronizeSessionLibraryAsync(rig.Library.Snapshot);
        var selection = workspace.SelectionVersion;
        Assert.Null(Assert.Single(workspace.Items).LastPlayedAt);
        await rig.Start();
        await workspace.SynchronizeSessionLibraryAsync(rig.Library.Snapshot);
        Assert.Equal(rig.Library.Snapshot.Entries[0].LastPlayedAt, Assert.Single(workspace.Items).LastPlayedAt);
        Assert.NotNull(workspace.Items[0].LastPlayedAt);
        await rig.Engine.SampleAsync(); await rig.Advance(11);
        await rig.Engine.ObserveFactAsync(rig.Fact(ProcessEventKind.ProcessExited, exit: rig.Clock.Utc));
        var writes = rig.Disk.Writes;
        await workspace.SynchronizeSessionLibraryAsync(rig.Library.Snapshot);
        Assert.Equal(SessionLifecycle.Completed, rig.Native.Lifecycle);
        Assert.Equal(1011, rig.Library.Snapshot.Entries[0].TotalPlaySeconds);
        Assert.Equal(rig.Library.Snapshot.Entries[0].TotalPlaySeconds, workspace.Items[0].PlaySeconds);
        Assert.Equal(writes, rig.Disk.Writes); Assert.Equal(1, factory.Opens);
        Assert.Same(draft, workspace.Draft); Assert.True(workspace.IsDirty);
        Assert.Equal("unsaved personal edit", workspace.Draft!.Notes);
        Assert.Equal(WorkspacePage.Details, workspace.Page); Assert.Equal("g", workspace.SelectedId);
        Assert.Equal("Synthetic", workspace.Query.Text); Assert.Equal("recent", workspace.Query.Sort);
        Assert.Equal(selection, workspace.SelectionVersion);
    }

    [Fact]
    public async Task FailedLaunchDoesNotInventRecentDateOrTriggerAProjectionRefresh()
    {
        await using var rig = new SessionTestRig(); var factory = new Factory(rig);
        await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = Workspace(client); await workspace.InitializeAsync();
        var original = rig.Library.Snapshot;
        await workspace.SynchronizeSessionLibraryAsync(original);
        var items = workspace.Items;
        rig.Platform.Deny = true; Assert.False((await rig.Start()).ProcessCreated);
        Assert.Same(original, rig.Library.Snapshot);
        await workspace.SynchronizeSessionLibraryAsync(rig.Library.Snapshot);
        Assert.Same(items, workspace.Items); Assert.Null(workspace.Items[0].LastPlayedAt);
        Assert.Equal(5, rig.Library.Snapshot.Entries[0].PlayCount);
    }

    [Fact]
    public async Task UnchangedSnapshotDoesNotQueryOnEveryTickAndConcurrentChangesAreBounded()
    {
        var client = new Client(); await using var workspace = Workspace(client); await workspace.InitializeAsync();
        var first = new LibrarySnapshot([], LibraryRecovery.Main);
        await workspace.SynchronizeSessionLibraryAsync(first);
        var queries = client.Queries;
        for (var i = 0; i < 100; i++) await workspace.SynchronizeSessionLibraryAsync(first);
        Assert.Equal(queries, client.Queries);
        var entered = Gate(); var release = Gate();
        client.Query = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Row("updated"); };
        var second = first with { Recovery = LibraryRecovery.Backup };
        var refresh = workspace.SynchronizeSessionLibraryAsync(second); await entered.Task;
        var third = first with { Entries = [SessionTestRig.Entry()] };
        for (var i = 0; i < 100; i++) await workspace.SynchronizeSessionLibraryAsync(third);
        Assert.Equal(queries + 1, client.Queries);
        release.TrySetResult(); await refresh;
        await workspace.SynchronizeSessionLibraryAsync(third);
        Assert.Equal(queries + 2, client.Queries); Assert.Equal("updated", workspace.Items[0].Name);
    }

    [Fact]
    public async Task ANewUserQuerySupersedesLateSessionProjectionAndRetriesUseLatestFilter()
    {
        var client = new Client(); await using var workspace = Workspace(client); await workspace.InitializeAsync();
        var snapshot = new LibrarySnapshot([], LibraryRecovery.Main);
        var entered = Gate(); var release = Gate();
        client.Query = async (query, _) => { if (query.Text == "") { entered.TrySetResult(); await release.Task; return Row("stale"); } return Row(query.Text); };
        var live = workspace.SynchronizeSessionLibraryAsync(snapshot); await entered.Task;
        await workspace.SetQueryAsync(new(Text: "new filter"));
        release.TrySetResult(); await live;
        Assert.Equal("new filter", workspace.Items[0].Name);
        await workspace.SynchronizeSessionLibraryAsync(snapshot);
        Assert.Equal("new filter", workspace.Items[0].Name); Assert.Equal("new filter", workspace.Query.Text);
    }

    [Fact]
    public async Task FailedProjectionCanRetryWithoutClearingExistingItems()
    {
        var client = new Client(); await using var workspace = Workspace(client); await workspace.InitializeAsync();
        var original = workspace.Items; var snapshot = new LibrarySnapshot([], LibraryRecovery.Main);
        client.Query = (_, _) => throw new IOException("synthetic query failure");
        await workspace.SynchronizeSessionLibraryAsync(snapshot); Assert.Same(original, workspace.Items);
        client.Query = (_, _) => Task.FromResult(Row("recovered"));
        await workspace.SynchronizeSessionLibraryAsync(snapshot); Assert.Equal("recovered", workspace.Items[0].Name);
    }

    [Fact]
    public async Task DisposalCancelsAndDrainsLiveQueryWithoutApplyingLateResults()
    {
        var client = new Client(); var workspace = Workspace(client); await workspace.InitializeAsync();
        var entered = Gate(); var release = Gate(); var original = workspace.Items;
        client.Query = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Row("late"); };
        var refresh = workspace.SynchronizeSessionLibraryAsync(new([], LibraryRecovery.Main)); await entered.Task;
        await workspace.DisposeAsync(); await refresh;
        Assert.Same(original, workspace.Items);
        var queries = client.Queries;
        await workspace.SynchronizeSessionLibraryAsync(new([], LibraryRecovery.Main)); Assert.Equal(queries, client.Queries);
    }

    private sealed class Factory(SessionTestRig rig) : ITransactionalLibraryFactory
    {
        public int Opens;
        public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) { Opens++; return Task.FromResult(rig.Library); }
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences value, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Client : IOfflineLibraryClient
    {
        public LibraryCapability Capability => new(true, true, "");
        public int Queries;
        public Func<LibraryQuery, CancellationToken, Task<LibraryQueryResult>> Query = (_, _) => Task.FromResult(Row("original"));
        public Task<ClientResult> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientResult(ClientOutcome.Success, ""));
        public Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation) { Queries++; return Query(query, cancellation); }
        public Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
