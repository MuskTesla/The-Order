using System.Runtime.InteropServices;

namespace TheOrder.Platform.Windows;

public enum TrayCommand { Show, Pause, Resume, Exit }

// One application-owned shell icon and one subclass. No game handles or accounting timers.
public sealed class WindowsTrayLifecycle : IDisposable
{
    public const uint CallbackMessage = 0x8047;
    private readonly IntPtr window;
    private readonly Subclass callback;
    private readonly Action<TrayCommand> command;
    private readonly Func<bool, bool> queryEnd;
    private readonly Action<bool> endSession;
    private readonly uint taskbarCreated;
    private NotifyIconData data;
    private bool disposed, attached, paused, canPause, canResume;
    private string title = "当前没有游玩会话", state = "";
    public bool Available { get; private set; }
    public event Action? Unavailable;
    public event Action? SystemSettingsChanged;
    public WindowsTrayLifecycle(IntPtr window, Action<TrayCommand> command, Func<bool, bool> queryEnd, Action<bool> endSession, bool simulateFailure = false)
    {
        this.window = window; this.command = command; this.queryEnd = queryEnd; this.endSession = endSession;
        callback = Receive;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        attached = SetWindowSubclass(window, callback, 0x5b2, 0);
        if (!attached) throw new System.ComponentModel.Win32Exception();
        data = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = 7 | 0x80,
            Callback = CallbackMessage, Icon = LoadIcon(IntPtr.Zero, (IntPtr)32512), Tip = "The Order · 书架与游玩计时", Info = "", InfoTitle = "" };
        if (!simulateFailure) Add();
    }
    private void Add()
    {
        Available = ShellNotifyIcon(0, ref data);
        if (Available) { data.Version = 4; ShellNotifyIcon(4, ref data); }
        else Unavailable?.Invoke();
    }
    public void Update(string game, string status, bool pauseEnabled, bool resumeEnabled)
    {
        title = game; state = status; canPause = pauseEnabled; canResume = resumeEnabled; paused = resumeEnabled;
        var text = $"The Order · {game} · {status}"; if (text.Length > 127) text = text[..127];
        if (disposed || text == data.Tip) return;
        data.Tip = text;
        if (Available && !ShellNotifyIcon(1, ref data)) { Available = false; Unavailable?.Invoke(); }
    }
    private IntPtr Receive(IntPtr hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (disposed) return DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message == 0x11) { try { queryEnd((unchecked((long)lParam) & 0x80000000L) != 0); } catch { } return (IntPtr)1; }
        if (message == 0x16) { try { endSession(wParam != 0); } catch { } return IntPtr.Zero; }
        if (message is 0x1a or 0x15 or 0x31a) // WM_SETTINGCHANGE, WM_SYSCOLORCHANGE, WM_THEMECHANGED
        {
            // Let XAML process the new theme resources before notifying our policy.
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            SystemSettingsChanged?.Invoke();
            return result;
        }
        if (message == CallbackMessage)
        {
            var notification = (uint)((long)lParam & 0xffff);
            if (notification is 0x400 or 0x401 or 0x203) command(TrayCommand.Show);
            else if (notification is 0x7b or 0x205) Menu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private void Menu()
    {
        var menu = CreatePopupMenu(); if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, 1, "打开 The Order");
            AppendMenu(menu, 3, 0, title.Replace("&", "&&"));
            if (state.Length > 0) AppendMenu(menu, 3, 0, state.Replace("&", "&&"));
            AppendMenu(menu, (canPause || canResume) ? 0u : 3u, 2, paused ? "恢复计时" : "暂停计时");
            AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 3, "退出 The Order");
            GetCursorPos(out var point); SetForegroundWindow(window);
            var selected = TrackPopupMenuEx(menu, 0x102, point.X, point.Y, window, IntPtr.Zero);
            PostMessage(window, 0, 0, 0);
            if (selected == 1) command(TrayCommand.Show);
            else if (selected == 2 && (canPause || canResume)) command(paused ? TrayCommand.Resume : TrayCommand.Pause);
            else if (selected == 3) command(TrayCommand.Exit);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        ShellNotifyIcon(2, ref data); Available = false;
        if (attached) { RemoveWindowSubclass(window, callback, 0x5b2); attached = false; }
        SystemSettingsChanged = null;
        // LoadIcon returns a shared system icon: it is not owned and must not be destroyed.
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Subclass(IntPtr hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint reference);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr Balloon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, Subclass callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, Subclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, nuint wParam, nint lParam);
}
