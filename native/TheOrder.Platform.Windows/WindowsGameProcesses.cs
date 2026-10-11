using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsGameProcesses(IFilesystemIdentity filesystem, IProcessSnapshotSource? snapshots = null,
    Func<int, IObservedProcess?>? openProcess = null) : IGameProcessPlatform
{
    public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
    {
        var leases = new List<SafeFileHandle>();
        try
        {
            var script = snapshot.Executable.StoredPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? WindowsCommandScript.Build(LaunchExecutablePath(snapshot.Executable), snapshot.Installation.Arguments) : null;
            var initialImage = script is null ? snapshot.Executable : filesystem.Inspect(script.Interpreter, false).Observation
                ?? throw new LaunchValidationException(LaunchOutcome.LaunchFailed, UiText.Get("Windows 命令解释器不可用，未执行脚本。"));
            // Pin lexical ancestors/reparse entries as well as final targets. Deny delete on
            // directories and deny write/delete on the exe until CreateProcess returns.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var observation in new[] { snapshot.Folder, snapshot.WorkingDirectory, snapshot.Executable, initialImage }.Distinct())
            {
                string? path = observation.StoredPath;
                while (path is not null)
                {
                    if (paths.Add(path))
                    {
                        bool file = path == snapshot.Executable.StoredPath || path == initialImage.StoredPath;
                        var handle = CreateFile(path, 0x80, file ? 1u : 3u, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new LaunchValidationException(error == 5 ? LaunchOutcome.AccessDenied : LaunchOutcome.StaleInstallation, UiText.Get("启动路径不能固定，未执行程序。")); }
                        leases.Add(handle);
                    }
                    path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                }
                var target = CreateFile(observation.FinalPath, 0x80, observation.IsDirectory ? 3u : 1u, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (target.IsInvalid) { target.Dispose(); throw new LaunchValidationException(LaunchOutcome.StaleInstallation, UiText.Get("启动目标不能固定，未执行程序。")); }
                leases.Add(target);
            }
            var preflight = new InstallationPreflightService(filesystem);
            try { preflight.Revalidate(snapshot.Folder); preflight.Revalidate(snapshot.Executable); preflight.Revalidate(snapshot.WorkingDirectory); }
            catch (InvalidDataException) { throw new LaunchValidationException(LaunchOutcome.StaleInstallation, UiText.Get("安装在启动前发生变化，未执行程序。")); }
            cancellation.ThrowIfCancellationRequested();
            preflight.Revalidate(initialImage);
            var cwd = LaunchWorkingDirectory(snapshot.WorkingDirectory);
            if (cwd.Length >= 260)
                throw new LaunchValidationException(LaunchOutcome.WorkingDirectoryInvalid, UiText.Get("Windows 创建进程不支持此长度的工作目录；未更换目录，也未执行程序。"));
            // Atomic scope association closes the unobserved, short-lived intermediary gap.
            // Structured arguments are encoded once for CreateProcessW, without a shell.
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                var created = WindowsProcessLifetimeScope.Start(LaunchExecutablePath(initialImage), script is null ? snapshot.Installation.Arguments : [], cwd, script);
                return new Observed(created.Pid, created.Handle, filesystem, initialImage, startedAt, created.Scope);
            }
            catch (Win32Exception error)
            {
                throw new LaunchValidationException(error.NativeErrorCode is 5 or 740 ? LaunchOutcome.AccessDenied : LaunchOutcome.LaunchFailed,
                    error.NativeErrorCode == 740 ? UiText.Get("此程序需要提升权限；管理器不会绕过 UAC。") : UiText.Get("Windows 未能创建游戏进程。"));
            }
        }
        finally { foreach (var handle in leases) handle.Dispose(); }
    }

    private static string NormalPath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private string LaunchExecutablePath(PathObservation executable)
    {
        // Ordinary Win32 spelling preserves per-application compatibility/graphics profiles
        // and argv[0] conventions. Keep the extended physical path for identity and pinning.
        var normal = NormalPath(executable.FinalPath);
        if (normal.Length >= 260 || !Path.IsPathFullyQualified(normal)) return executable.FinalPath;
        var actual = filesystem.Inspect(normal, false);
        // Prefix removal can change Win32 parsing (e.g. trailing dots/spaces). Never select
        // a different file; paths requiring extended syntax retain that syntax.
        return actual.IsValid && actual.Observation is { IsDirectory: false } observation
            && observation.Identity == executable.Identity
            && string.Equals(observation.FinalPath, executable.FinalPath, StringComparison.OrdinalIgnoreCase)
            ? normal : executable.FinalPath;
    }

    private string LaunchWorkingDirectory(PathObservation working)
    {
        var normal = NormalPath(working.FinalPath);
        var actual = filesystem.Inspect(normal);
        if (normal.Length >= 260 || !actual.IsValid || actual.Observation is not { IsDirectory: true } observed
            || observed.Identity != working.Identity || !string.Equals(observed.FinalPath, working.FinalPath, StringComparison.OrdinalIgnoreCase))
            throw new LaunchValidationException(LaunchOutcome.WorkingDirectoryInvalid, UiText.Get("工作目录无法转换为同一身份的 Windows 启动路径，未更换目录，也未执行程序。"));
        return normal;
    }

    public ProcessPresence RevalidateExternal(ProcessIdentity identity)
    {
        using var handle = OpenProcess(0x100000 | 0x1000, false, identity.Pid);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() == 87 ? ProcessPresence.Inactive : ProcessPresence.Unknown;
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return ProcessPresence.Unknown;
        if (creation != identity.CreationFileTime) return ProcessPresence.Inactive;
        return WaitForSingleObject(handle, 0) switch { 0 => ProcessPresence.Inactive, 258 => ProcessPresence.Active, _ => ProcessPresence.Unknown };
    }

    public IObservedProcess? Attach(ProcessIdentity identity)
    {
        var handle = OpenProcess(0x100000 | 0x1000, false, identity.Pid);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        var observed = new Observed(identity.Pid, handle, filesystem);
        if (observed.Query().Identity != identity) { observed.Dispose(); return null; }
        return observed;
    }

    public ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximumProcesses)
    {
        var rows = Capture(maximumProcesses);
        if (rows is null) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, UiText.Get("无法完成启动前进程检查，未重复创建程序。"));
        var result = ImmutableArray.CreateBuilder<ProcessIdentity>();
        foreach (var row in rows.Rows.Where(r => string.Equals(r.Name, Path.GetFileName(executable.StoredPath), StringComparison.OrdinalIgnoreCase)))
        {
            using var observed = Open(row.Pid);
            var value = observed?.Query();
            if (value?.State == ProcessTrackingState.Exited) continue;
            if (value?.Identity?.ImageIdentity is null) throw new LaunchValidationException(LaunchOutcome.AccessDenied, UiText.Get("存在同名但无法查询的进程，请先确认游戏是否已运行。"));
            if (value.Identity.ImageIdentity == executable.Identity) result.Add(value.Identity);
        }
        return result.ToImmutable();
    }

    public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximumProcesses, int maximumCandidates)
    {
        var snapshot = Capture(maximumProcesses);
        return snapshot is null ? new([], false) : ProcessSnapshotGeneration.Bind(snapshot, ancestors, maximumCandidates, Open);
    }

    private IObservedProcess? Open(int pid)
    {
        if (openProcess is not null) return openProcess(pid);
        var handle = OpenProcess(0x100000 | 0x1000, false, pid);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new Observed(pid, handle, filesystem);
    }
    private ProcessSnapshot? Capture(int maximum) => snapshots is null ? Snapshot(maximum) : snapshots.Capture(maximum);
    private static ProcessSnapshot? Snapshot(int maximum)
    {
        var before = DateTimeOffset.UtcNow.ToFileTime();
        using var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle.IsInvalid) return null;
        var item = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(handle, ref item)) return Marshal.GetLastWin32Error() == 18 ? new(before, []) : null;
        var rows = ImmutableArray.CreateBuilder<ProcessSnapshotRow>();
        do { if (rows.Count >= maximum) return null; rows.Add(new((int)item.Pid, (int)item.Parent, item.Name)); } while (Process32Next(handle, ref item));
        return Marshal.GetLastWin32Error() == 18 ? new(before, rows.ToImmutable()) : null;
    }

    private sealed class Observed : IObservedProcess
    {
        private readonly SafeProcessHandle handle;
        private readonly WindowsProcessLifetimeScope? scope;
        private readonly IFilesystemIdentity filesystem;
        private readonly PathObservation? expected;
        private ProcessIdentity? identity;
        public int Pid { get; }
        public long? StartTimestamp { get; }
        public Observed(int pid, SafeProcessHandle handle, IFilesystemIdentity filesystem, PathObservation? expected = null, long? startedAt = null, WindowsProcessLifetimeScope? scope = null)
        { Pid = pid; this.handle = handle; this.filesystem = filesystem; this.expected = expected; StartTimestamp = startedAt; this.scope = scope; }
        public IProcessLifetimeScope? RetainLifetimeScope() => scope?.Retain();
        public ProcessObservation Query()
        {
            try
            {
                var retained = handle;
                uint wait = WaitForSingleObject(retained, 0);
                if (wait is not (0 or 258) || !GetProcessTimes(retained, out var creation, out var exit, out _, out _)) return new(identity, ProcessTrackingState.Unknown);
                if (identity is null)
                {
                    var name = new StringBuilder(32768); uint size = (uint)name.Capacity;
                    PathObservation? image = expected;
                    if (QueryFullProcessImageName(retained, 0, name, ref size)) image = filesystem.Inspect(name.ToString(), false).Observation ?? expected;
                    if (image is not null) identity = new(Pid, creation, image.FinalPath, image.Identity);
                }
                int? parent = NtQueryInformationProcess(retained, 0, out var basic, (uint)Marshal.SizeOf<BasicProcessInformation>(), out _) >= 0
                    ? checked((int)basic.ParentPid) : null;
                if (wait == 0) return new(identity, ProcessTrackingState.Exited, exit > 0 ? exit : null, ParentPid: parent);
                bool window = false;
                // A window is supplementary handoff evidence. Failure/racing exit in this
                // optional query must not discard the retained handle's lifetime evidence.
                try { using var live = Process.GetProcessById(Pid); window = live.MainWindowHandle != IntPtr.Zero; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { }
                return new(identity, identity is null ? ProcessTrackingState.RunningLimited : ProcessTrackingState.RunningConfirmed, HasWindow: window, ParentPid: parent);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            { return new(identity, ProcessTrackingState.Unknown); }
        }
        public void Dispose() { handle.Dispose(); scope?.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicProcessInformation
    { public nint ExitStatus, PebAddress, AffinityMask, BasePriority; public nuint Pid, ParentPid; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, out BasicProcessInformation info, uint size, out uint returned);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
