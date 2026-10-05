using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using TheOrder.Platform.Windows;

namespace TheOrder.WinUI;

internal static class Program
{
    private static AppInstance? primary;
    private static AppInstance? current;
    public static int Activations;
    public static string ActivationArguments = "";
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            var key = NativeInstanceIdentity.ForProfile(Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_DATA_DIR"));
            current = AppInstance.GetCurrent();
            current.Activated += Activated; // Subscribe before registration exposes us to a simultaneous secondary.
            var activation = current.GetActivatedEventArgs();
            primary = AppInstance.FindOrRegisterForKey(key);
            if (!primary.IsCurrent)
            {
                AllowSetForegroundWindow(primary.ProcessId);
                using var done = new ManualResetEvent(false);
                var redirect = Task.Run(async () => { try { await primary.RedirectActivationToAsync(activation); } finally { try { done.Set(); } catch (ObjectDisposedException) { } } });
                // Pump COM on the STA; a failed/timed-out redirect never creates a second writer.
                var wait = CoWaitForMultipleObjects(0, 10000, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
                if (wait != 0) return 2;
                try { redirect.GetAwaiter().GetResult(); return 0; }
                catch { return 3; } // Failed forwarding still exits without creating an application/writer.
            }
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return 0;
        }
        catch (Exception e) { MessageBox(IntPtr.Zero, "The Order 无法安全启动：" + e.GetType().Name + "。请核对隔离资料目录；没有打开第二个书架。", "The Order", 0x10); return 1; }
        finally { if (current is not null) current.Activated -= Activated; if (primary?.IsCurrent == true) primary.UnregisterKey(); }
    }
    private static void Activated(object? sender, AppActivationArguments args)
    {
        // Current CLI supports showing the library only. Arguments are forwarded, never executed.
        var text = (args.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments ?? "";
        ActivationArguments = text.Length <= 32768 ? text : "";
        Interlocked.Increment(ref Activations); App.Instance?.RequestActivation();
    }
    [DllImport("ole32.dll")] private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
