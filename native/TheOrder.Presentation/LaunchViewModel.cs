using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed record LaunchInstallationItem(string Id, string Label);

// All members except service notifications are driven on the presentation thread.
// Background tracking is owned by Application and survives page navigation.
public sealed class LaunchViewModel(ILaunchLibrary library, ILaunchCommands service) : ObservableState, IAsyncDisposable
{
    private IAsyncDisposable? ownedService;
    public LaunchViewModel(ILaunchLibrary library, GameLaunchService service) : this(library, new DirectLaunchCommands(service)) { ownedService = service; }
    private readonly HashSet<Task> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? preparation, launch;
    private LaunchEntry? entry;
    private LaunchPreparation? prepared;
    private long generation;
    private bool disposed, busy;
    private LaunchStatus? last;
    public string? GameId => entry?.Identity.GameId;
    public string? SelectedInstallationId { get; private set; }
    public ImmutableArray<LaunchInstallationItem> Installations { get; private set; } = [];
    public string Message { get; private set; } = "选择本地安装。";
    public string Position { get; private set; } = "";
    public string GlobalMessage => service.Status.Message;
    public LaunchStatus Runtime => service.Status;
    public LaunchOutcome? LastRequestOutcome { get; private set; }
    public LaunchOutcome Availability => prepared?.Outcome ?? LaunchOutcome.InstallationMissing;
    public bool Busy => busy;
    public bool CanLaunch => !disposed && !busy && prepared?.Outcome == LaunchOutcome.Ready;
    public bool CanConfirm => !disposed && !busy && prepared?.Snapshot is not null && prepared.Outcome is LaunchOutcome.PositionReviewRequired or LaunchOutcome.StaleInstallation;
    public bool CanCancel => busy && launch is not null;
    public Task OpenAsync(string gameId) => Track(async () =>
    {
        Leave(); var intent = generation; var token = NewPreparation();
        try
        {
            var loaded = await library.CaptureLaunchAsync(gameId, token);
            if (disposed || intent != generation) return;
            entry = loaded;
            Installations = loaded.Installations.Select(i => new LaunchInstallationItem(i.Id,
                (i.Id == loaded.Preferred ? "首选 · " : "") + i.Folder)).ToImmutableArray();
            SelectedInstallationId = LaunchSelection.Select(loaded)?.Id;
            Notify();
            await PrepareCore(intent, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (intent == generation) { Message = "无法读取启动安装，请重新打开详情。"; Notify(); } }
    });
    public Task SelectAsync(string? installationId) => Track(async () =>
    {
        if (disposed || entry is null || installationId == SelectedInstallationId) return;
        SelectedInstallationId = installationId; prepared = null; Position = ""; var intent = ++generation;
        await PrepareCore(intent, NewPreparation());
    });
    private CancellationToken NewPreparation()
    {
        preparation?.Cancel(); preparation?.Dispose();
        preparation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); return preparation.Token;
    }
    private async Task PrepareCore(long intent, CancellationToken token)
    {
        var id = GameId;
        if (id is null || SelectedInstallationId is null) { Message = "没有默认启动安装，请明确选择一项。"; Notify(); return; }
        Message = "正在核对启动位置…"; Notify();
        var value = await service.PrepareAsync(id, SelectedInstallationId, token);
        if (disposed || intent != generation || token.IsCancellationRequested) return;
        prepared = value; Message = value.Message;
        Position = value.Snapshot is { } s ? "启动文件：" + s.Installation.Executable + "\n工作目录：" + s.WorkingDirectory.StoredPath
            + (s.ExplicitWorkingDirectory ? "（明确指定）" : "（程序所在目录）") + "\n参数：" + System.Text.Json.JsonSerializer.Serialize(s.Installation.Arguments) : "";
        last = null; Refresh(); Notify();
    }
    public Task ConfirmAsync() => Track(async () =>
    {
        if (!CanConfirm || prepared?.Snapshot is not { } snapshot) return;
        var intent = generation; busy = true; Notify();
        try
        {
            await service.ConfirmPositionAsync(snapshot, lifetime.Token);
            if (intent == generation) await PrepareCore(intent, NewPreparation());
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (intent == generation) Message = "位置未能保存或已经变化，请重新核对。"; }
        finally { busy = false; Notify(); }
    });
    public Task LaunchAsync() => Track(async () =>
    {
        if (!CanLaunch || GameId is not { } id) return;
        var intent = generation; var installation = SelectedInstallationId;
        busy = true; var previousObservation = launch;
        var operation = launch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Notify();
        try
        {
            var result = await service.LaunchAsync(id, installation, operation.Token);
            LastRequestOutcome = result.Outcome;
            if (result.ProcessCreated) previousObservation?.Dispose();
            else { if (ReferenceEquals(launch, operation)) launch = previousObservation; operation.Dispose(); }
            if (!disposed && intent == generation) { Message = result.Message; last = result; }
        }
        catch (Exception) { if (ReferenceEquals(launch, operation)) launch = previousObservation; operation.Dispose(); if (!disposed && intent == generation) Message = service.Status.ProcessCreated ? "程序已启动，状态显示受限；请勿重复启动。" : "启动请求未完成。"; }
        finally { busy = false; Notify(); }
    });
    public void Cancel() { if (disposed) return; launch?.Cancel(); launch?.Dispose(); launch = null; Notify(); }
    public void Refresh()
    {
        if (disposed) return;
        var value = service.Status;
        Changed(nameof(GlobalMessage));
        if (ReferenceEquals(last, value)) return;
        last = value;
        if (value.Snapshot?.Game == entry?.Identity && entry is not null)
        { Message = value.Message; Notify(); }
    }
    public void Leave()
    {
        if (disposed) return; // The drained WinUI window may unload after host disposal.
        generation++; preparation?.Cancel(); entry = null; prepared = null; Installations = []; SelectedInstallationId = null;
        Position = ""; Message = "选择本地安装。"; last = null; Notify();
    }
    private void Notify() { if (!disposed) Changed(""); }
    private Task Track(Func<Task> action)
    {
        if (disposed) return Task.CompletedTask;
        var task = action(); pending.Add(task); _ = Remove(task); return task;
    }
    private async Task Remove(Task task) { try { await task; } catch { } finally { pending.Remove(task); } }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; generation++; lifetime.Cancel(); preparation?.Cancel(); launch?.Cancel();
        await Task.WhenAll(pending.ToArray()); if (ownedService is not null) await ownedService.DisposeAsync();
        preparation?.Dispose(); launch?.Dispose(); lifetime.Dispose();
    }
}
