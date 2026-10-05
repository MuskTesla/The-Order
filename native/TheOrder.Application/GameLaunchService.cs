using System.Collections.Immutable;
using System.Diagnostics;

namespace TheOrder.Application;

public sealed record LaunchTrackingOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RunningPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan InitialTreeWindow { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan HandoffWindow { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan CandidateSettle { get; init; } = TimeSpan.FromMilliseconds(750);
    public int MaximumProcesses { get; init; } = 8192;
    public int MaximumCandidates { get; init; } = 32;
    public int MaximumTreePolls { get; init; } = 240;
}

// Single active launch admission, with no writable library/playtime dependency.
public sealed class GameLaunchService(ILaunchLibrary library, IFilesystemIdentity filesystem, IGameProcessPlatform platform,
    ILaunchPositionStore positions, LaunchTrackingOptions? trackingOptions = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim positionGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly LaunchTrackingOptions options = trackingOptions ?? new();
    private Task? startTask, monitorTask;
    private bool starting, reserved, closing;
    private long generation;
    private LaunchStatus status = new(LaunchOutcome.Ready, ProcessTrackingState.None, null, null, null, "尚未启动。");
    public LaunchStatus Status => Volatile.Read(ref status);
    public event Action<LaunchStatus>? Changed;
    public event Action<ProcessEvent>? ProcessChanged;

    private void Publish(LaunchStatus value, ProcessEventKind? kind = null, ProcessIdentity? previous = null)
    {
        Volatile.Write(ref status, value);
        // Observers cannot turn a successful OS creation into a launch failure.
        foreach (Action<LaunchStatus> listener in Changed?.GetInvocationList() ?? []) { try { listener(value); } catch { } }
        if (kind is null || value.Snapshot is null) return;
        var notification = new ProcessEvent(kind.Value, value.Snapshot.OperationId, previous, value.Current, DateTimeOffset.UtcNow);
        foreach (Action<ProcessEvent> listener in ProcessChanged?.GetInvocationList() ?? []) { try { listener(notification); } catch { } }
    }

    public async Task<LaunchPreparation> PrepareAsync(string gameId, string? explicitInstallation, CancellationToken cancellation = default)
    {
        try
        {
            var entry = await library.CaptureLaunchAsync(gameId, cancellation).ConfigureAwait(false);
            var installation = LaunchSelection.Select(entry, explicitInstallation);
            if (installation is null) return new(LaunchOutcome.InstallationMissing, null, "请选择首选或明确的本地安装。");
            if (installation.Executable is null) return new(LaunchOutcome.ExecutableMissing, null, "请先在安装设置中选择启动 exe。");
            var snapshot = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var folder = Require(installation.Folder, true, LaunchOutcome.InstallationMissing);
                var executable = Require(installation.Executable, false, LaunchOutcome.ExecutableMissing);
                if (executable.IsDirectory || !BoundedScanner.IsGameExecutable(Path.GetFileName(installation.Executable))
                    || !InstallationPreflightService.Within(executable.FinalPath, folder.FinalPath))
                    throw new LaunchValidationException(LaunchOutcome.StaleInstallation, "启动文件不属于已选择的安装目录。");
                var cwd = LaunchSelection.WorkingDirectory(installation);
                var working = Require(cwd, true, LaunchOutcome.WorkingDirectoryInvalid);
                if (working.Identity != folder.Identity && !InstallationPreflightService.Within(working.FinalPath, folder.FinalPath))
                    throw new LaunchValidationException(LaunchOutcome.WorkingDirectoryInvalid, "工作目录必须位于安装目录内，不会自动更换目录。");
                if (installation.Arguments.IsDefault || installation.Arguments.Length > 100 || installation.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
                    throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "启动参数无效，请检查参数数组。");
                return new LaunchSnapshot(Guid.NewGuid(), Interlocked.Increment(ref generation), entry.Identity, installation, entry.Preferred,
                    folder, executable, working, installation.WorkingDirectory is not null);
            }, cancellation).ConfigureAwait(false);
            await positionGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var saved = (await positions.ReadAsync(cancellation).ConfigureAwait(false)).FirstOrDefault(p => p.GameId == gameId && p.InstallationId == installation.Id);
                return saved is null ? new(LaunchOutcome.PositionReviewRequired, snapshot, "首次使用此安装，请核对 exe 和工作目录后确认启动位置。")
                    : LaunchSelection.Matches(saved, snapshot) ? new(LaunchOutcome.Ready, snapshot, "启动位置已核对。")
                    : new(LaunchOutcome.StaleInstallation, snapshot, "安装目标与本机核对记录不同，未允许启动。请重新核对位置。");
            }
            finally { positionGate.Release(); }
        }
        catch (LaunchValidationException error) { return new(error.Outcome, null, error.Message); }
        catch (OperationCanceledException) { return new(LaunchOutcome.Cancelled, null, "启动准备已取消。"); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { return new(LaunchOutcome.StaleInstallation, null, "启动位置未能安全核对，未执行程序。请检查安装和本机核对记录。"); }
    }

    private PathObservation Require(string path, bool directory, LaunchOutcome missing)
    {
        var probe = filesystem.Inspect(path, directory);
        if (!probe.IsValid)
        {
            var message = probe.Status == PreflightStatus.NotFound ? missing switch
            {
                LaunchOutcome.ExecutableMissing => "启动文件不存在，请在安装设置中重新选择 exe。",
                LaunchOutcome.WorkingDirectoryInvalid => "工作目录不存在，未启动程序，也不会自动更换目录。",
                _ => "安装目录不存在，请检查本地安装位置。"
            } : probe.Message;
            throw new LaunchValidationException(probe.Status == PreflightStatus.AccessDenied ? LaunchOutcome.AccessDenied : missing, message);
        }
        return probe.Observation!;
    }

    public async Task ConfirmPositionAsync(LaunchSnapshot reviewed, CancellationToken cancellation = default)
    {
        await positionGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var preflight = new InstallationPreflightService(filesystem);
            await library.WithLaunchAdmissionAsync(reviewed.Game, reviewed.Installation.Id, () =>
            {
                preflight.Revalidate(reviewed.Folder); preflight.Revalidate(reviewed.Executable); preflight.Revalidate(reviewed.WorkingDirectory); return true;
            }, cancellation).ConfigureAwait(false);
            var existing = await positions.ReadAsync(cancellation).ConfigureAwait(false);
            var next = existing.Where(p => p.GameId != reviewed.Game.GameId || p.InstallationId != reviewed.Installation.Id).Append(LaunchSelection.Position(reviewed)).ToImmutableArray();
            await positions.WriteAsync(next, cancellation).ConfigureAwait(false);
        }
        finally { positionGate.Release(); }
    }

    public Task<LaunchStatus> LaunchAsync(string gameId, string? explicitInstallation = null, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (closing) return Task.FromResult(Status with { Outcome = LaunchOutcome.Closed, Message = "管理器正在关闭。" });
            if (starting) return Task.FromResult(Status with { Outcome = LaunchOutcome.AlreadyLaunching, Message = "已有启动操作正在进行，没有重复启动。" });
            if (reserved) return Task.FromResult(Status with { Outcome = LaunchOutcome.AlreadyRunning, Message = "已有游戏运行或追踪尚未确认结束，没有再次启动。" });
            starting = true;
            var task = StartCoreAsync(gameId, explicitInstallation, cancellation); startTask = task; return task;
        }
    }

    private async Task<LaunchStatus> StartCoreAsync(string gameId, string? explicitInstallation, CancellationToken cancellation)
    {
        IObservedProcess? process = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        LaunchSnapshot? snapshot = null;
        var clock = Stopwatch.StartNew();
        try
        {
            var preparation = await PrepareAsync(gameId, explicitInstallation, stop.Token).ConfigureAwait(false);
            snapshot = preparation.Snapshot;
            if (preparation.Outcome != LaunchOutcome.Ready || snapshot is null)
            { var rejected = new LaunchStatus(preparation.Outcome, ProcessTrackingState.None, snapshot, null, null, preparation.Message); Publish(rejected); return rejected; }
            Publish(new(LaunchOutcome.Starting, ProcessTrackingState.Attaching, snapshot, null, null, "正在启动…"));
            var existing = await Task.Run(() => platform.FindExisting(snapshot.Executable, options.MaximumProcesses), stop.Token).ConfigureAwait(false);
            if (!existing.IsEmpty)
            {
                var already = new LaunchStatus(LaunchOutcome.AlreadyRunning, ProcessTrackingState.RunningLimited, snapshot, existing[0], existing[0], "同一启动文件已有进程运行，没有再次启动；跨重启会话恢复尚未启用。");
                lock (gate) reserved = true;
                Publish(already); return already;
            }
            process = await library.WithLaunchAdmissionAsync(snapshot.Game, snapshot.Installation.Id, () => { clock.Restart(); return platform.Start(snapshot, stop.Token); }, stop.Token).ConfigureAwait(false);
            // From here onward every failure describes successful creation with limited observation.
            lock (gate) reserved = true;
            ProcessObservation first;
            try { first = process.Query(); } catch { first = new(null, ProcessTrackingState.Unknown); }
            var identity = first.Identity;
            var created = new LaunchStatus(identity is null ? LaunchOutcome.StartedButTrackingLimited : LaunchOutcome.Started,
                identity is null ? ProcessTrackingState.RunningLimited : ProcessTrackingState.Attaching, snapshot,
                identity, identity, identity is null ? "程序已经启动，但无法取得完整进程身份；请勿重复启动。" : "程序已经启动，正在确认进程连续性。", process.StartTimestamp is { } timestamp ? Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds : clock.Elapsed.TotalMilliseconds);
            Publish(created, ProcessEventKind.ProcessStarted);
            var owned = process; process = null;
            monitorTask = Task.Run(() => MonitorAsync(owned, created, cancellation));
            return created;
        }
        catch (Exception error)
        {
            if (process is not null)
            {
                process.Dispose(); lock (gate) reserved = true;
                var limited = new LaunchStatus(LaunchOutcome.StartedButTrackingLimited, ProcessTrackingState.Unknown, snapshot, null, null, "程序已创建，追踪未完成；请勿重复启动。");
                Publish(limited, ProcessEventKind.TrackingLimited); return limited;
            }
            var outcome = error is OperationCanceledException ? LaunchOutcome.Cancelled : error is LaunchValidationException validation ? validation.Outcome : error is UnauthorizedAccessException ? LaunchOutcome.AccessDenied : LaunchOutcome.LaunchFailed;
            var failure = new LaunchStatus(outcome, ProcessTrackingState.None, snapshot, null, null, outcome == LaunchOutcome.Cancelled ? "创建进程前已取消。" : error is LaunchValidationException known ? known.Message : "未创建游戏进程，请检查启动位置、权限和运行环境。");
            Publish(failure); return failure;
        }
        finally { lock (gate) starting = false; }
    }

    private async Task MonitorAsync(IObservedProcess initial, LaunchStatus created, CancellationToken requestedCancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, requestedCancellation);
        var owned = new List<IObservedProcess> { initial };
        var clock = Stopwatch.StartNew(); var current = initial;
        bool hadWindow = false, ambiguous = false;
        double? exitedAt = null, candidateSince = null;
        ProcessIdentity? candidateIdentity = null;
        int treePolls = 0;
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                var observed = owned.Select(p => p.Query()).ToImmutableArray();
                var root = observed[owned.IndexOf(current)];
                hadWindow |= root.HasWindow;
                if (root.State is ProcessTrackingState.Unknown or ProcessTrackingState.RunningLimited || root.Identity is null)
                { Limited("程序已经启动，但无法可靠查询进程；没有把查询失败当作退出。"); return; }
                bool exited = root.State == ProcessTrackingState.Exited;
                if (exited) exitedAt ??= clock.Elapsed.TotalMilliseconds;
                bool observeTree = clock.Elapsed < options.InitialTreeWindow || exited;
                if (observeTree && treePolls++ < options.MaximumTreePolls)
                {
                    var tree = platform.Descendants(observed, options.MaximumProcesses, options.MaximumCandidates);
                    ambiguous |= !tree.Complete;
                    foreach (var item in tree.Candidates)
                    {
                        var id = item.Observation.Identity;
                        if (id is null || owned.Any(p => p.Pid == id.Pid)) continue;
                        if (owned.Count >= options.MaximumCandidates) { ambiguous = true; break; }
                        var attached = platform.Attach(id);
                        if (attached is not null) owned.Add(attached); else ambiguous = true;
                    }
                }
                else if (observeTree) ambiguous = true;
                if (exited)
                {
                    Publish(Status with { Tracking = ProcessTrackingState.ObservingHandoff, Message = "入口进程已退出，正在核对可能的游戏子进程。" });
                    var candidates = owned.Where(p => p != current).Select(p => (Process: p, Observation: p.Query()))
                        .Where(p => p.Observation.State == ProcessTrackingState.RunningConfirmed && p.Observation.Identity is { } id
                            && p.Observation.HasWindow && LaunchSelection.IsContinuationExecutable(Path.GetFileName(id.ImagePath))
                            && InstallationPreflightService.Within(id.ImagePath, created.Snapshot!.Folder.FinalPath)).ToArray();
                    if (candidates.Length > 1) ambiguous = true;
                    if (!ambiguous && candidates.Length == 1)
                    {
                        var next = candidates[0];
                        if (candidateIdentity != next.Observation.Identity) { candidateIdentity = next.Observation.Identity; candidateSince = clock.Elapsed.TotalMilliseconds; }
                        if (clock.Elapsed.TotalMilliseconds - candidateSince >= options.CandidateSettle.TotalMilliseconds)
                        {
                            var handoffMs = clock.Elapsed.TotalMilliseconds - exitedAt;
                            var previous = Status.Current; current = next.Process; hadWindow = true; exitedAt = null; candidateSince = null;
                            Publish(Status with { Current = next.Observation.Identity, Tracking = ProcessTrackingState.RunningConfirmed, AttachMilliseconds = created.StartMilliseconds + clock.Elapsed.TotalMilliseconds, HandoffMilliseconds = handoffMs,
                                Message = "已根据父子关系、创建时间和唯一游戏窗口接续追踪。" }, ProcessEventKind.ProcessHandoff, previous);
                        }
                    }
                    else { candidateIdentity = null; candidateSince = null; }
                    if (exitedAt is { } ended && clock.Elapsed.TotalMilliseconds - ended >= options.HandoffWindow.TotalMilliseconds)
                    {
                        bool anyUnresolved = ambiguous || owned.Where(p => p != current).Any(p => p.Query().State != ProcessTrackingState.Exited);
                        if (hadWindow && !anyUnresolved)
                        {
                            Publish(Status with { Tracking = ProcessTrackingState.Exited, Message = "已确认所追踪的游戏进程退出。" }, ProcessEventKind.ProcessExited);
                            lock (gate) reserved = false;
                        }
                        else Limited("入口已退出，但未能确认唯一游戏接续进程；程序曾成功创建，请勿重复启动。");
                        return;
                    }
                }
                else if (Status.Tracking != ProcessTrackingState.RunningConfirmed)
                    Publish(Status with { Current = root.Identity, Tracking = ProcessTrackingState.RunningConfirmed, AttachMilliseconds = created.StartMilliseconds + clock.Elapsed.TotalMilliseconds, Message = "游戏进程正在运行。" }, ProcessEventKind.ProcessTrackingAttached);
                await Task.Delay(observeTree ? options.PollInterval : options.RunningPollInterval, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        { Publish(Status with { Outcome = LaunchOutcome.StartedButTrackingLimited, Tracking = ProcessTrackingState.Detached, Message = "观察已停止，已创建的游戏继续运行。" }, ProcessEventKind.Detached); }
        catch (Exception) { Limited("程序已经启动，后续追踪受限；游戏未被结束，请勿重复启动。"); }
        finally { foreach (var process in owned) process.Dispose(); }
        void Limited(string message) => Publish(Status with { Outcome = LaunchOutcome.StartedButTrackingLimited, Tracking = ProcessTrackingState.RunningLimited, Message = message }, ProcessEventKind.TrackingLimited);
    }

    public async ValueTask DisposeAsync()
    {
        Task? start;
        lock (gate) { if (closing) return; closing = true; lifetime.Cancel(); start = startTask; }
        if (start is not null) await start.ConfigureAwait(false);
        if (monitorTask is not null) await monitorTask.ConfigureAwait(false);
        // No Kill/TerminateProcess/job-object close action. Only observer handles are released.
    }
}
