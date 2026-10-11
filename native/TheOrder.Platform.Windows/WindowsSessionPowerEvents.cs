using System.ComponentModel;
using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// No window dependency. Dispose unregisters before releasing the rooted delegate.
public sealed class WindowsSessionPowerEvents : ISessionPowerEvents, IDisposable
{
    private readonly Callback callback;
    private IntPtr registration;
    private int disposed;
    public event Action<bool>? SuspendedChanged;
    public WindowsSessionPowerEvents()
    {
        callback = Receive;
        var parameters = new Parameters { Function = Marshal.GetFunctionPointerForDelegate(callback) };
        var error = PowerRegisterSuspendResumeNotification(2, ref parameters, out registration);
        if (error != 0) throw new Win32Exception((int)error);
    }
    private uint Receive(IntPtr context, uint type, IntPtr setting)
    {
        if (Volatile.Read(ref disposed) != 0 || type is not (4 or 7 or 18)) return 0;
        foreach (Action<bool> listener in SuspendedChanged?.GetInvocationList() ?? [])
            try { listener(type == 4); } catch { /* Never unwind into the native callback. */ }
        return 0;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (registration != IntPtr.Zero) { PowerUnregisterSuspendResumeNotification(registration); registration = IntPtr.Zero; }
        SuspendedChanged = null; GC.KeepAlive(callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public IntPtr Function; public IntPtr Context; }
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Parameters recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
}
