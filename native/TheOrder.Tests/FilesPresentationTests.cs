using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class FilesPresentationTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InstallationView View(string id) => new(new(new(Guid.Empty, id, Guid.Empty, [], null), [], null), []);
    private static WorkspaceViewModel Workspace(Library client) => new(client, new Preferences(), new("synthetic"), new LegacyClientTextRules());
    [Theory][InlineData(ClientOutcome.Committed)][InlineData(ClientOutcome.CommittedWithWarning)]
    public async Task SavedDraftClosesAndRefreshFailureCannotBecomeSaveFailure(ClientOutcome outcome)
    {
        var library = new Library(); await using var workspace = Workspace(library); await workspace.InitializeAsync();
        var client = new Files { SaveResult = new(outcome, "已经保存") }; await using var files = new FilesViewModel(client, workspace);
        await files.LoadInstallationsAsync("a"); files.Edit(null); var notifications = new List<string?>(); files.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        client.Load = (_, _) => Task.FromException<InstallationView>(new IOException());
        await files.SaveAsync(); Assert.False(files.HasDraft); Assert.Contains(nameof(files.HasDraft), notifications); Assert.Contains("已保存", files.InstallationMessage); Assert.Contains("刷新失败", files.InstallationMessage); Assert.Equal(1, client.Saves);
        await files.SaveAsync(); Assert.Equal(1, client.Saves);
    }
    [Fact] public async Task ValidationFailureRetainsDraftAndDraftCannotBeSilentlyReplaced()
    {
        await using var workspace = Workspace(new()); await workspace.InitializeAsync(); var client = new Files { SaveResult = new(ClientOutcome.ValidationFailure, "路径无效") };
        await using var files = new FilesViewModel(client, workspace); await files.LoadInstallationsAsync("a"); files.Edit(null); var draft = files.Draft!; draft.Folder = "keep";
        files.Edit(null); Assert.Same(draft, files.Draft); await files.SaveAsync(); Assert.Same(draft, files.Draft); Assert.Equal("keep", files.Draft!.Folder);
        files.CancelDraft(); Assert.False(files.HasDraft);
    }
    [Fact] public async Task ReadOnlyNeverStartsScanOrEdit()
    {
        var library = new Library { CapabilityValue = new(true, false, "只读") }; await using var workspace = Workspace(library); await workspace.InitializeAsync();
        var client = new Files(); await using var files = new FilesViewModel(client, workspace); await files.ScanAsync("synthetic"); files.Edit(null);
        Assert.Equal(0, client.Scans); Assert.False(files.HasDraft);
    }
    [Fact] public async Task ClosingCancelsAndDrainsScan()
    {
        await using var workspace = Workspace(new()); await workspace.InitializeAsync(); var entered = Gate<bool>();
        var client = new Files { Scan = async (_, token) => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); } };
        var files = new FilesViewModel(client, workspace); var task = files.ScanAsync("synthetic"); await entered.Task; Assert.True(files.IsScanning); Assert.False(workspace.CanEdit);
        await files.DisposeAsync(); await task; Assert.True(workspace.CanEdit);
    }
    [Theory][InlineData(ScanStatus.Cancelled, ImportStage.Cancelled)][InlineData(ScanStatus.Failed, ImportStage.Failed)][InlineData(ScanStatus.NoCandidates, ImportStage.NoCandidates)]
    public async Task DistinctScanOutcomesStayDistinct(ScanStatus outcome, ImportStage stage)
    {
        await using var workspace = Workspace(new()); await workspace.InitializeAsync(); var client = new Files { Scan = (_, _) => Task.FromResult(new ScanResult(outcome, [], new(0, 0, 0, "synthetic"), [], "result")) };
        await using var files = new FilesViewModel(client, workspace); await files.ScanAsync("synthetic"); Assert.Equal(stage, files.Stage); Assert.False(files.IsScanning);
    }
    [Fact] public async Task ClosingTracksAcceptedMutationAndDoesNotResubmit()
    {
        await using var workspace = Workspace(new()); await workspace.InitializeAsync(); var gate = Gate<ClientResult>();
        var client = new Files { Save = _ => gate.Task }; var files = new FilesViewModel(client, workspace); await files.LoadInstallationsAsync("a"); files.Edit(null);
        var saving = files.SaveAsync(); var again = files.SaveAsync(); Assert.Same(saving, again); var close = files.DisposeAsync().AsTask(); Assert.False(close.IsCompleted);
        gate.SetResult(new(ClientOutcome.Committed, "已经保存")); await close; await saving; Assert.Equal(1, client.Saves);
    }
    [Fact] public async Task StaleInstallationLoadsCannotReplaceNewGameAndAllLoadsDrain()
    {
        await using var workspace = Workspace(new()); await workspace.InitializeAsync(); var a = Gate<InstallationView>(); var b = Gate<InstallationView>();
        var client = new Files { Load = (id, _) => id == "a" ? a.Task : b.Task }; var files = new FilesViewModel(client, workspace);
        var first = files.LoadInstallationsAsync("a"); var second = files.LoadInstallationsAsync("b"); b.SetResult(View("b")); await second; Assert.Equal("b", files.Installations!.Details.Token.GameId);
        var close = files.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); a.SetResult(View("a")); await first; await close; Assert.Equal("b", files.Installations.Details.Token.GameId);
    }
    private sealed class Files : IOfflineFilesClient
    {
        public int Scans, Saves;
        public ClientResult SaveResult = new(ClientOutcome.Committed, "已经保存");
        public Func<CancellationToken, Task<ClientResult>>? Save;
        public Func<string, CancellationToken, Task<InstallationView>> Load = (id, _) => Task.FromResult(View(id));
        public Func<string, CancellationToken, Task<ScanResult>> Scan = (_, _) => Task.FromResult(new ScanResult(ScanStatus.NoCandidates, [], new(0, 0, 0, "synthetic"), [], ""));
        public Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellation) { Scans++; return Scan(root, cancellation); }
        public Task<InstallationView> InstallationsAsync(string id, CancellationToken cancellation) => Load(id, cancellation);
        public Task<ClientResult> SaveAsync(InstallationDetails details, InstallationInput input, CancellationToken cancellation) { Saves++; return Save?.Invoke(cancellation) ?? Task.FromResult(SaveResult); }
        public Task<ClientResult> ImportAsync(ImmutableArray<ScanCandidate> candidates, CancellationToken cancellation) => Task.FromResult(SaveResult);
        public Task<ClientResult> RemoveAsync(InstallationDetails details, string id, CancellationToken cancellation) => Task.FromResult(SaveResult);
        public Task<ClientResult> PreferAsync(InstallationDetails details, string id, CancellationToken cancellation) => Task.FromResult(SaveResult);
        public Task<ClientResult> CoverAsync(GameDetails details, string? path, CancellationToken cancellation) => Task.FromResult(SaveResult);
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Library : IOfflineLibraryClient
    {
        public LibraryCapability CapabilityValue = new(true, true, ""); public LibraryCapability Capability => CapabilityValue;
        public Task<ClientResult> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientResult(ClientOutcome.Success, ""));
        public Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation) => Task.FromResult(new LibraryQueryResult([], 0, [], []));
        public Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
