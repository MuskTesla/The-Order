using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class LibraryBackupPresentationTests
{
    [Fact]
    public async Task UnreadableAutomaticHistoryDoesNotPreventExportingHealthyLibrary()
    {
        var factory = new Factory(); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var store = new Store { Read = _ => Task.FromException<LibraryBackupState>(new InvalidDataException("future backup")) };
        await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace);
        await vm.InitializeAsync(); Assert.True(vm.CanOperate); Assert.False(vm.CanChangeAutomatic); Assert.Contains("future backup", vm.Message);
        await vm.ExportAsync("synthetic-export.json"); Assert.Equal(1, store.Exports); Assert.Contains("已保存", vm.Message);
        await vm.AutomaticAsync(); Assert.Equal(1, store.Reads);
    }
    [Fact]
    public async Task ManualPreviewQueuedBehindAutomaticBackupIsNotSilentlyDropped()
    {
        var factory = new Factory(); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var store = new Store(); await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace); await vm.InitializeAsync();
        var pause = new TaskCompletionSource<LibraryBackupState>(); store.Read = _ => pause.Task;
        var automatic = vm.AutomaticAsync(); Assert.True(vm.IsBusy);
        var preview = vm.PreviewAsync("synthetic-input.json", false, LibraryRestoreMode.AddMissing);
        Assert.Null(vm.Preview); pause.SetResult(new(new(false), [])); await automatic; await preview;
        Assert.NotNull(vm.Preview); Assert.Equal(1, vm.Preview.BackupCount); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task EmptyBookshelfAllowsManualRecoveryWithoutAutomaticEmptySnapshots()
    {
        var factory = new Factory(empty: true); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var store = new Store(); await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace); await vm.InitializeAsync();
        Assert.True(vm.CanOperate); await vm.AutomaticAsync(); Assert.Equal(0, store.Snapshots);
        await vm.PreviewAsync("synthetic-input.json", false, LibraryRestoreMode.ReplaceLibrary);
        Assert.Equal(0, vm.Preview!.CurrentCount); Assert.Equal(1, vm.Preview.ResultCount);
        Assert.True((await vm.RestoreAsync(vm.Preview, default)).IsCommitted); Assert.Equal(1, store.Snapshots);
    }
    [Fact]
    public async Task TrayShutdownDrainsBackupThenContainerReleaseStillCloses()
    {
        var factory = new Factory(); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var store = new Store(); await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace); await vm.InitializeAsync();
        var original = client.CurrentSnapshot!; var closed = false;
        Assert.True(await AppUpdateShutdown.RunAsync(() => Task.FromResult(true), () => Task.FromResult<IAppUpdateHandoff?>(null),
            () => Task.FromResult(true), async () =>
            {
                await vm.DrainAsync(); await vm.AutomaticAsync(onExit: true);
                await vm.DisposeAsync(); // MainWindow.StopAsync drains the presentation owner.
                await vm.DisposeAsync(); // NativeHost then releases the same singleton through its container.
            }, () => closed = true));
        Assert.True(closed); Assert.Equal(1, store.Snapshots); Assert.Same(original, client.CurrentSnapshot);
    }
    [Fact]
    public async Task RepeatedBackupDisposalWaitsForAcceptedWriteAndRejectsLateCommands()
    {
        var factory = new Factory(); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { WriteExternal = async _ => { entered.TrySetResult(); await finish.Task; return null; } };
        await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace); await vm.InitializeAsync();
        var export = vm.ExportAsync("synthetic-export.json"); await entered.Task;
        var first = vm.DisposeAsync().AsTask(); var repeated = vm.DisposeAsync().AsTask();
        try { Assert.False(first.IsCompleted); Assert.False(repeated.IsCompleted); Assert.False(vm.CanOperate); }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(export, first, repeated); await vm.DisposeAsync();
        var reads = store.Reads;
        await vm.InitializeAsync(); await vm.ExportAsync("late.json"); await vm.PreviewAsync("late.json", false, LibraryRestoreMode.AddMissing);
        Assert.Equal(1, store.Exports); Assert.Equal(reads, store.Reads); Assert.Null(vm.Preview); Assert.False(vm.CanOperate);
    }
    [Fact]
    public async Task OptionalBackupFailureDoesNotBlockCleanExitOrChangeMainLibrary()
    {
        var factory = new Factory(); await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules());
        await using var workspace = new WorkspaceViewModel(client, new Preferences(), new("synthetic"), new LegacyClientTextRules()); await workspace.InitializeAsync();
        var store = new Store { FailSnapshot = true }; await using var vm = new LibraryBackupViewModel(new(client, store, SessionTestRig.Codec), workspace); await vm.InitializeAsync();
        var original = client.CurrentSnapshot!; var closed = false;
        Assert.True(await AppUpdateShutdown.RunAsync(() => Task.FromResult(true), () => Task.FromResult<IAppUpdateHandoff?>(null),
            () => Task.FromResult(true), async () => { await vm.AutomaticAsync(onExit: true); await vm.DisposeAsync(); await vm.DisposeAsync(); }, () => closed = true));
        Assert.True(closed); Assert.Contains("备份操作未完成", vm.Message); Assert.Same(original, client.CurrentSnapshot);
    }
    private sealed class Factory(bool empty = false) : ITransactionalLibraryFactory
    {
        private readonly SessionMemoryDisk disk = new() { Main = SessionTestRig.Codec.Encode(empty ? [] : [Entry()], Clock) };
        public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.FromResult(TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk)));
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Store : ILibraryBackupStore
    {
        public Func<CancellationToken, Task<LibraryBackupState>> Read = _ => Task.FromResult(new LibraryBackupState(new(), []));
        public int Reads, Exports, Snapshots;
        public bool FailSnapshot;
        public Func<CancellationToken, Task<string?>> WriteExternal = _ => Task.FromResult<string?>(null);
        private byte[] saved = [];
        public Task<LibraryBackupState> ReadStateAsync(CancellationToken cancellation) { Reads++; return Read(cancellation); }
        public Task SaveSettingsAsync(LibraryBackupSettings settings, CancellationToken cancellation) => Task.CompletedTask;
        public Task<LibraryBackupWrite> SaveSnapshotAsync(ReadOnlyMemory<byte> document, int entries, string reason, CancellationToken cancellation)
        { if (FailSnapshot) throw new IOException("synthetic backup failure"); saved = document.ToArray(); Snapshots++; return Task.FromResult(new LibraryBackupWrite(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, reason, entries, "digest"))); }
        public Task<byte[]> ReadSnapshotAsync(string id, CancellationToken cancellation) => Task.FromResult(saved);
        public Task<byte[]> ReadExternalAsync(string path, CancellationToken cancellation) => Task.FromResult(SessionTestRig.Codec.Encode([Entry("Backup")], Clock));
        public Task<string?> WriteExternalAsync(string path, ReadOnlyMemory<byte> document, CancellationToken cancellation)
        { Exports++; return WriteExternal(cancellation); }
    }
}
