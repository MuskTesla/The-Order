using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class PresentationRemediationTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<UnsavedChoice> Stay() => Task.FromResult(UnsavedChoice.Stay);
    private static GameDetails Detail(string id, string notes = "") => new(id, new(id, "planned", null, false, notes, [], []), "", "", "", "", "", "", new(Guid.Empty, id, Guid.Empty, new(id, "planned", null, false, notes, [], [])));
    private static LibraryQueryResult Rows(string id = "a") => new([new(id, id, "planned", null, false, "", 0, 0)], 1, [], []);
    private static WorkspaceViewModel Vm(Client client, Preferences? preferences = null, IClientTextRules? text = null) => new(client, preferences ?? new(), new("synthetic"), text ?? new LegacyClientTextRules());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelDelayedDetailsHonorsNewDraftRevision(bool typeAgain)
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay);
        vm.Draft!.Notes = "discard";
        var completion = Gate<GameDetails>(); client.Detail = (_, _) => completion.Task;
        var cancel = vm.CancelAndReloadDetailsAsync(); Assert.True(vm.IsDetailsLoading);
        if (typeAgain) vm.Draft!.Notes = "new input";
        completion.SetResult(Detail("a", "authoritative")); await cancel;
        Assert.Equal(typeAgain ? "new input" : "authoritative", vm.Draft!.Notes);
        Assert.Equal(typeAgain, vm.IsDirty); Assert.False(vm.IsDetailsLoading); Assert.True(vm.CanEdit);
    }
    [Fact]
    public async Task CancelReloadFailurePreservesCoherentEditableDraft()
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay);
        vm.Draft!.Notes = "discard"; client.Detail = (_, _) => Task.FromException<GameDetails>(new IOException());
        await vm.CancelAndReloadDetailsAsync(); Assert.False(vm.IsDirty); Assert.True(vm.CanEdit); Assert.False(vm.IsDetailsLoading); Assert.Equal(ClientOutcome.LoadFailure, vm.Notice.Outcome);
    }
    [Fact]
    public async Task CancelReloadCannotReplaceAnotherGameOrLifecycle()
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay);
        var delayed = Gate<GameDetails>(); client.Detail = (id, _) => id == "a" ? delayed.Task : Task.FromResult(Detail(id));
        var cancel = vm.CancelAndReloadDetailsAsync(); await vm.NavigateAsync(WorkspacePage.Details, "b", Stay);
        delayed.SetResult(Detail("a")); await cancel; Assert.Equal("b", vm.Draft!.Source.Id);
        client.Detail = (_, _) => Task.FromResult(Detail("b") with { Token = Detail("b").Token with { Generation = Guid.NewGuid() } });
        await vm.CancelAndReloadDetailsAsync(); Assert.Equal(Guid.Empty, vm.Draft.Source.Token.Generation); Assert.Equal(ClientOutcome.Conflict, vm.Notice.Outcome);
    }
    [Fact]
    public async Task SaveInvalidatesPendingCancelRefresh()
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay);
        var delayed = Gate<GameDetails>(); client.Detail = (_, _) => delayed.Task;
        var cancel = vm.CancelAndReloadDetailsAsync(); vm.Draft!.Notes = "new input";
        client.Detail = (id, _) => Task.FromResult(Detail(id, "new input")); await vm.SaveAsync();
        delayed.SetResult(Detail("a", "stale")); await cancel;
        Assert.Equal("new input", vm.Draft!.Notes); Assert.False(vm.IsDirty);
    }
    [Theory]
    [InlineData("theme")]
    [InlineData("layout")]
    [InlineData("sort")]
    [InlineData("rapid")]
    public async Task EarlyPreferencesMergeOnlyUserOverrides(string operation)
    {
        var entered = Gate<bool>(); var loaded = Gate<ClientPreferences>();
        var old = new ClientPreferences(NativeTheme.Dark, "grid", "rating", [new("keep", "existing", new("old"))]);
        var store = new Preferences { Load = _ => { entered.SetResult(true); return loaded.Task; } };
        await using var vm = Vm(new(), store); var initialization = vm.InitializeAsync(); await entered.Task;
        Assert.Equal(PreferenceInitialization.Loading, vm.PreferenceState);
        if (operation is "theme" or "rapid") await vm.ChangeAppearanceAsync(NativeTheme.Light, vm.Layout);
        if (operation == "layout") await vm.ChangeAppearanceAsync(vm.Theme, "list");
        if (operation == "sort") await vm.SetQueryAsync(new(Sort: "name"));
        if (operation == "rapid") { await vm.ChangeAppearanceAsync(NativeTheme.Dark, "list"); await vm.ChangeAppearanceAsync(NativeTheme.Light, "grid"); }
        Assert.Empty(store.Writes); loaded.SetResult(old); await initialization;
        Assert.Equal(PreferenceInitialization.Ready, vm.PreferenceState); Assert.Equal("keep", vm.SavedFilters.Single().Id);
        Assert.Equal(operation is "theme" or "rapid" ? NativeTheme.Light : NativeTheme.Dark, vm.Theme);
        Assert.Equal(operation == "layout" ? "list" : "grid", vm.Layout);
        Assert.Equal(operation == "sort" ? "name" : "rating", vm.Query.Sort);
        Assert.Equal(vm.Preferences, store.Writes.Last());
    }
    [Fact]
    public async Task FailedPreferenceLoadNeverWritesEvenAfterIntentionalChanges()
    {
        var entered = Gate<bool>(); var loaded = Gate<ClientPreferences>();
        var store = new Preferences { Load = _ => { entered.SetResult(true); return loaded.Task; } };
        await using var vm = Vm(new(), store); var initialization = vm.InitializeAsync(); await entered.Task;
        await vm.ChangeAppearanceAsync(NativeTheme.Dark, "list"); loaded.SetException(new InvalidDataException("corrupt")); await initialization;
        Assert.Equal(PreferenceInitialization.LoadFailed, vm.PreferenceState); Assert.Empty(store.Writes);
        await vm.ChangeAppearanceAsync(NativeTheme.Light, "grid"); await vm.SetQueryAsync(new(Sort: "name")); await vm.SaveFilterAsync("not persisted");
        Assert.Equal(NativeTheme.Light, vm.Theme); Assert.Equal("name", vm.Query.Sort); Assert.Empty(store.Writes); Assert.False(vm.CanManageFilters);
    }
    [Theory]
    [InlineData(ClientOutcome.Committed, false)]
    [InlineData(ClientOutcome.CommittedWithWarning, false)]
    [InlineData(ClientOutcome.Committed, true)]
    [InlineData(ClientOutcome.CommittedWithWarning, true)]
    public async Task CommitAndRefreshFailureHaveSeparateOutcomes(ClientOutcome outcome, bool detailsFail)
    {
        var client = new Client { SaveResult = new(outcome, outcome == ClientOutcome.CommittedWithWarning ? "修改已经保存；commit warning" : "修改已经保存") };
        await using var vm = Vm(client); await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay); vm.Draft!.Notes = "saved";
        client.Search = (_, _) => Task.FromException<LibraryQueryResult>(new IOException("refresh"));
        client.Detail = detailsFail ? (_, _) => Task.FromException<GameDetails>(new IOException()) : (id, _) => Task.FromResult(Detail(id, "saved"));
        var receipt = await vm.SaveAsync(); Assert.Equal(outcome, receipt.Outcome); Assert.Equal(outcome, vm.PersistenceResult!.Outcome);
        Assert.Equal(RefreshOutcome.Failed, vm.LastRefresh); Assert.Equal(ClientOutcome.CommittedWithWarning, vm.Notice.Outcome);
        Assert.Contains("已经保存", vm.Notice.Message); Assert.Contains("刷新失败", vm.Notice.Message);
        if (outcome == ClientOutcome.CommittedWithWarning) Assert.Contains("commit warning", vm.Notice.Message);
        Assert.False(vm.IsDirty); Assert.Equal(1, client.Saves); Assert.False(vm.CanSave);
        Assert.Equal(!detailsFail, vm.CanEditDetails);
        if (!detailsFail) Assert.Equal("saved", vm.Draft!.Notes);
    }
    [Fact]
    public async Task FailedCommitDoesNotRefreshOrRetireDraft()
    {
        var client = new Client { SaveResult = new(ClientOutcome.IoFailure, "没有保存") }; await using var vm = Vm(client);
        await vm.InitializeAsync(); await vm.NavigateAsync(WorkspacePage.Details, "a", Stay); vm.Draft!.Notes = "retain";
        client.Search = (_, _) => throw new Xunit.Sdk.XunitException("No refresh after failed commit");
        Assert.Equal(ClientOutcome.IoFailure, (await vm.SaveAsync()).Outcome); Assert.Equal("retain", vm.Draft.Notes); Assert.True(vm.IsDirty); Assert.Equal(RefreshOutcome.None, vm.LastRefresh);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedBatchPreprocessingIsTrackedThroughCloseOrCancellation(bool cancel)
    {
        using var text = new GatedText(); var client = new Client(); var vm = Vm(client, text: text); await vm.InitializeAsync();
        var batch = vm.ApplyBatchAsync(["a"], "all", "keep", "", "label", false); await text.Entered.Task;
        Assert.True(vm.IsSaving); var close = vm.ResolveUnsavedAsync(Stay); Assert.False(close.IsCompleted);
        if (cancel) vm.CancelPendingOperation();
        text.Release.Set(); var result = await batch; Assert.True(await close);
        Assert.Equal(cancel ? ClientOutcome.Cancelled : ClientOutcome.Committed, result.Outcome); Assert.Equal(cancel ? 0 : 1, client.Batches);
        if (cancel) { Assert.Contains("未写入本次修改", result.Message); Assert.DoesNotContain("草稿已保留", result.Message); }
        Assert.False(vm.IsSaving); await vm.DisposeAsync();
    }
    [Fact]
    public async Task ConfirmedBatchAdmissionDrainsAndPreservesWarning()
    {
        var entered = Gate<bool>(); var finished = Gate<ClientResult>(); var client = new Client { Batch = (_, _, _) => { entered.SetResult(true); return finished.Task; } };
        await using var vm = Vm(client); await vm.InitializeAsync(); var batch = vm.ApplyBatchAsync(["a"], "all", "keep", "", "label", false); await entered.Task;
        var close = vm.ResolveUnsavedAsync(Stay); Assert.False(close.IsCompleted);
        finished.SetResult(new(ClientOutcome.CommittedWithWarning, "已经保存，warning")); await batch; Assert.True(await close);
        Assert.Equal(ClientOutcome.CommittedWithWarning, vm.Notice.Outcome); Assert.Equal(1, client.Batches);
    }
    [Fact]
    public async Task BatchParseFailureIsVisibleAndNeverEscapesUiEntry()
    {
        using var text = new GatedText { Fail = true }; text.Release.Set(); var client = new Client(); await using var vm = Vm(client, text: text); await vm.InitializeAsync();
        var result = await vm.ApplyBatchAsync(["a"], "all", "keep", "", "bad", false);
        Assert.Equal(ClientOutcome.ValidationFailure, result.Outcome); Assert.Contains("invalid labels", vm.Notice.Message); Assert.Equal(0, client.Batches); Assert.False(vm.IsSaving);
    }
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("new-query")]
    public async Task ReloadOwnsAndTerminatesSupersededSearch(string mode)
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync();
        var entered = Gate<bool>(); var delayed = Gate<LibraryQueryResult>();
        client.Search = (q, _) => { if (q.Text == "old") { entered.TrySetResult(true); return delayed.Task; } return Task.FromResult(Rows(q.Text)); };
        var search = vm.SetQueryAsync(new("old")); await entered.Task;
        var load = Gate<ClientResult>(); client.Load = _ => load.Task; var reload = vm.ReloadAsync(); Assert.False(vm.IsSearching);
        await vm.SetQueryAsync(new(mode == "new-query" ? "latest" : "next"));
        if (mode == "failure") { client.CapabilityValue = new(false, false, "error"); load.SetResult(new(ClientOutcome.LoadFailure, "error")); }
        else if (mode == "cancel") load.SetCanceled(); else load.SetResult(new(ClientOutcome.Success, ""));
        await reload; delayed.SetException(new IOException("old error")); await search;
        Assert.False(vm.IsSearching); Assert.False(vm.IsLoading);
        if (mode == "new-query") Assert.Equal("latest", vm.Items.Single().Id);
        if (mode == "failure") Assert.Equal("error", vm.Notice.Message);
    }
    [Theory]
    [InlineData("layout")]
    [InlineData("sort")]
    [InlineData("filter")]
    [InlineData("search")]
    [InlineData("reload")]
    public async Task ExistingLibraryActionsExplicitlyInvalidateSelection(string action)
    {
        await using var vm = Vm(new()); await vm.InitializeAsync(); var version = vm.SelectionVersion;
        switch (action)
        {
            case "layout": await vm.ChangeAppearanceAsync(vm.Theme, "list"); break;
            case "sort": await vm.SetQueryAsync(new(Sort: "name")); break;
            case "filter": await vm.SetQueryAsync(new(Favorites: true)); break;
            case "search": await vm.SetQueryAsync(new("hidden")); break;
            default: await vm.ReloadAsync(); break;
        }
        Assert.True(vm.SelectionVersion > version);
    }
    [Fact]
    public async Task QueryDuringReloadProjectionWinsAfterNewSnapshotWasOpened()
    {
        var client = new Client(); await using var vm = Vm(client); await vm.InitializeAsync();
        var entered = Gate<bool>(); var delayed = Gate<LibraryQueryResult>();
        client.Search = (q, _) => { if (q.Text == "") { entered.TrySetResult(true); return delayed.Task; } return Task.FromResult(Rows(q.Text)); };
        var reload = vm.ReloadAsync(); await entered.Task;
        await vm.SetQueryAsync(new("latest")); delayed.SetResult(Rows("old")); await reload;
        Assert.Equal("latest", vm.Items.Single().Id); Assert.False(vm.IsSearching); Assert.False(vm.IsLoading);
    }
    [Fact]
    public async Task ConfirmedBatchCommitWarningSurvivesQueryRefreshFailure()
    {
        var client = new Client { SaveResult = new(ClientOutcome.CommittedWithWarning, "已经保存，commit warning") };
        await using var vm = Vm(client); await vm.InitializeAsync();
        client.Search = (_, _) => Task.FromException<LibraryQueryResult>(new IOException());
        var result = await vm.ApplyBatchAsync(["a"], "all", "keep", "", "tag", false);
        Assert.True(result.IsCommitted); Assert.Equal(1, client.Batches); Assert.Contains("commit warning", vm.Notice.Message);
        Assert.Contains("刷新失败", vm.Notice.Message); Assert.Equal(RefreshOutcome.Failed, vm.LastRefresh);
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Func<CancellationToken, Task<ClientPreferences>> Load = _ => Task.FromResult(new ClientPreferences());
        public readonly List<ClientPreferences> Writes = [];
        public Task<ClientPreferences> LoadAsync(CancellationToken token) => Load(token);
        public Task SaveAsync(ClientPreferences value, CancellationToken token) { Writes.Add(value); return Task.CompletedTask; }
    }
    private sealed class Client : IOfflineLibraryClient
    {
        public LibraryCapability CapabilityValue = new(true, true, ""); public LibraryCapability Capability => CapabilityValue;
        public Func<CancellationToken, Task<ClientResult>> Load = _ => Task.FromResult(new ClientResult(ClientOutcome.Success, ""));
        public Func<LibraryQuery, CancellationToken, Task<LibraryQueryResult>> Search = (_, _) => Task.FromResult(Rows());
        public Func<string, CancellationToken, Task<GameDetails>> Detail = (id, _) => Task.FromResult(PresentationRemediationTests.Detail(id));
        public Func<ImmutableArray<string>, BulkPersonalPatch, CancellationToken, Task<ClientResult>>? Batch;
        public ClientResult SaveResult = new(ClientOutcome.Committed, "已经保存"); public int Saves, Batches;
        public Task<ClientResult> LoadAsync(CancellationToken t) => Load(t);
        public Task<LibraryQueryResult> QueryAsync(LibraryQuery q, CancellationToken t) => Search(q, t);
        public Task<GameDetails> DetailsAsync(string id, CancellationToken t) => Detail(id, t);
        public Task<ClientResult> SaveAsync(GameDetails d, PersonalInput p, CancellationToken t) { Saves++; return Task.FromResult(SaveResult); }
        public Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken t) { Batches++; return Batch?.Invoke(ids, patch, t) ?? Task.FromResult(SaveResult); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // Deliberately stalls only the background parser, never a dispatcher or production operation.
    private sealed class GatedText : IClientTextRules, IDisposable
    {
        private readonly LegacyClientTextRules inner = new(); public readonly ManualResetEventSlim Release = new(); public readonly TaskCompletionSource<bool> Entered = Gate<bool>(); public bool Fail;
        public ImmutableArray<string> SplitLabels(string value) { Entered.TrySetResult(true); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); if (Fail) throw new InvalidDataException("invalid labels"); return inner.SplitLabels(value); }
        public string NormalizeSearch(string v) => inner.NormalizeSearch(v); public ImmutableArray<string> QueryTerms(string v) => inner.QueryTerms(v);
        public int CompareNames(string a, string b) => inner.CompareNames(a, b); public int CompareIds(string a, string b) => inner.CompareIds(a, b);
        public void Dispose() => Release.Dispose();
    }
}
