using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed class LibraryBackupViewModel(LibraryBackupService service, WorkspaceViewModel workspace) : ObservableState, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task pending = Task.CompletedTask;
    private Task? disposal;
    private bool ready, stateReady, busy, automatic, stopping;
    public bool IsBusy => busy;
    public bool Automatic => automatic;
    public bool CanOperate => ready && !busy && !stopping && workspace.CanEdit;
    public bool CanChangeAutomatic => stateReady && CanOperate;
    public string Message { get; private set; } = "";
    public ImmutableArray<LibraryBackupRecord> Records { get; private set; } = [];
    public LibraryRestorePreview? Preview { get; private set; }
    public string LastBackup => Records.IsEmpty ? "尚无自动备份" : $"最近备份 {Records[0].CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Records[0].Entries} 部作品";
    public void RefreshAvailability() { Changed(nameof(CanOperate)); Changed(nameof(CanChangeAutomatic)); }
    public Task InitializeAsync() => Run(async token =>
    { try { await ReadState(token); stateReady = true; } finally { ready = true; } });
    private async Task ReadState(CancellationToken token)
    {
        var state = await service.ReadStateAsync(token);
        automatic = state.Settings.Automatic; Records = state.Records;
        Changed(nameof(Automatic)); Changed(nameof(Records)); Changed(nameof(LastBackup));
    }
    public Task SetAutomaticAsync(bool value) => Run(async token =>
    { await service.SaveSettingsAsync(value, token); await ReadState(token); Message = value ? "已启用自动备份。" : "已关闭自动备份，已有备份保留。"; });
    public Task ExportAsync(string path) => Run(async token =>
    { var warning = await service.ExportAsync(path, token); Message = warning is null ? "书架备份已保存。" : "书架备份已保存：" + warning; });
    public Task PreviewAsync(string source, bool savedSnapshot, LibraryRestoreMode mode) => Run(async token =>
    { Preview = null; Changed(nameof(Preview)); Preview = await service.PreviewAsync(source, savedSnapshot, mode, token); Changed(nameof(Preview)); });
    public Task<ClientResult> RestoreAsync(LibraryRestorePreview preview, CancellationToken cancellation) => service.RestoreAsync(preview, cancellation);
    public Task ReloadAfterRestoreAsync() => Run(async token => { Preview = null; Changed(nameof(Preview)); await ReadState(token); });
    public Task AutomaticAsync(bool onExit = false)
    {
        if (!stateReady || busy) return Task.CompletedTask;
        return Run(async token =>
        { var saved = await service.AutomaticAsync(onExit, token); if (saved is not null) { await ReadState(token); if (saved.Warning is not null) Message = saved.Warning; } }, quiet: true);
    }
    private Task Run(Func<CancellationToken, Task> action, bool quiet = false)
    {
        if (stopping || lifetime.IsCancellationRequested) return pending;
        if (busy) return quiet ? pending : pending = RunAfter(pending, action, quiet);
        return pending = RunCore(action, quiet);
    }
    private async Task RunAfter(Task preceding, Func<CancellationToken, Task> action, bool quiet)
    {
        await preceding;
        if (!lifetime.IsCancellationRequested) await RunCore(action, quiet);
    }
    private async Task RunCore(Func<CancellationToken, Task> action, bool quiet)
    {
        busy = true; Changed(nameof(IsBusy)); RefreshAvailability();
        if (!quiet) Message = "";
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { if (!quiet) Message = "已取消，书架未被恢复。"; }
        catch (Exception e) when (e is InvalidDataException or LibraryWriteLimitException or LibraryReadOnlyException or IOException or UnauthorizedAccessException or DataIsolationException or NativeProfileException)
        { Message = "备份操作未完成：" + (e is InvalidDataException or LibraryReadOnlyException or LibraryWriteLimitException ? e.Message : "无法安全读写备份文件，已有文件保留。"); }
        finally { busy = false; Changed(nameof(IsBusy)); RefreshAvailability(); Changed(nameof(Message)); }
    }
    public Task DrainAsync() => pending;
    public ValueTask DisposeAsync()
    {
        // Window draining and container teardown share this singleton. Both must await
        // the accepted write, without canceling or disposing its token source twice.
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        stopping = true; lifetime.Cancel(); RefreshAvailability();
        try { await pending; }
        finally { lifetime.Dispose(); }
    }
}
