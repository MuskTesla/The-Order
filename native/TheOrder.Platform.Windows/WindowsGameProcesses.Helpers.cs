using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed partial class WindowsGameProcesses
{
    private PathObservation SteamClient()
    {
        if (steamClient is not null) return steamClient();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
        var path = (key?.GetValue("SteamExe") as string)?.Replace('/', '\\');
        var value = path is null ? null : filesystem.Inspect(path, false).Observation;
        if (value is null || value.ViaReparsePoint || !Path.GetFileName(value.StoredPath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new LaunchValidationException(LaunchOutcome.ExecutableMissing, "Steam 客户端不可用；请安装并启动一次 Steam，或关闭 Steam 专用启动。");
        return value;
    }
    private IObservedProcess StartSteam(LaunchSnapshot snapshot, PathObservation steam, long startedAt, CancellationToken cancellation)
    {
        var id = snapshot.Installation.SteamAppId;
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[1-9][0-9]{0,9}$")) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "Steam App ID 无效。");
        var before = DateTimeOffset.UtcNow.ToFileTime();
        var request = new ProcessStartInfo(LaunchExecutablePath(steam)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steam.StoredPath)! };
        request.ArgumentList.Add("-applaunch"); request.ArgumentList.Add(id); foreach (var argument in snapshot.Installation.Arguments) request.ArgumentList.Add(argument);
        using var broker = Process.Start(request) ?? throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "Steam 未能接收启动请求。");
        // Steam.exe is a broker, never the played game. Only a newly observed exact image
        // earns a session; an external pre-existing process cannot be claimed as our launch.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (cancellation.IsCancellationRequested) throw new LaunchValidationException(LaunchOutcome.Cancelled, "Steam 已收到请求，已取消等待，未开始计时；请检查游戏是否已启动，避免重复点击。");
            var targets = FindExisting(snapshot.Executable, 8192).Where(x => x.CreationFileTime >= before).ToArray();
            if (targets.Length > 1) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "Steam 已接收请求，但出现多个游戏进程，未开始计时。请在游戏运行后核验。");
            if (targets.Length == 1 && Attach(targets[0]) is { } target) return target;
            if (cancellation.WaitHandle.WaitOne(250)) throw new LaunchValidationException(LaunchOutcome.Cancelled, "Steam 已收到请求，已取消等待，未开始计时；请检查游戏是否已启动，避免重复点击。");
        }
        throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "Steam 已接收请求，但尚未观察到选定游戏进程，未开始计时。请确认 Steam 登录、更新和实际启动程序，避免重复点击。");
    }
    private IObservedProcess StartElevated(PathObservation image, IEnumerable<string> arguments, string cwd, WindowsCommandScript? script, long startedAt)
    {
        if (script is not null) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "管理员辅助仅支持 exe，请选择游戏的 exe。");
        var command = string.Join(' ', arguments.Select(WindowsProcessLifetimeScope.Quote));
        if (command.Length > 30000) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "启动参数过长。");
        var request = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x40 | 0x100, Verb = "runas", File = LaunchExecutablePath(image), Parameters = command, Directory = cwd, Show = 1 };
        if (!ShellExecuteEx(ref request)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (request.Process == IntPtr.Zero) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "Windows 未返回管理员进程；请检查游戏是否已启动，未开始计时。");
        var handle = new SafeProcessHandle(request.Process, true);
        var pid = GetProcessId(handle);
        return new Observed((int)pid, handle, filesystem, image, startedAt);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb, File, Parameters, Directory;
        public int Show; public IntPtr Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey; public uint HotKey; public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle handle);
}
