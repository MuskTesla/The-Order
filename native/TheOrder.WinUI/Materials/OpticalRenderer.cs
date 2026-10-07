using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace TheOrder.WinUI.Materials;

// UI-thread owner. One hardware device, one immutable source, two swapchain buffers.
// No desktop/window capture, background thread, periodic timer or idle rendering.
internal sealed class OpticalRenderer : IDisposable
{
    [DllImport("TheOrder.OpticalMaterial.dll", ExactSpelling = true)]
    private static extern int OpticalCreate(IntPtr panel, out IntPtr result);
    [DllImport("TheOrder.OpticalMaterial.dll", ExactSpelling = true)]
    private static extern int OpticalUpload(IntPtr handle, byte[] bytes, uint w, uint h, uint length);
    [DllImport("TheOrder.OpticalMaterial.dll", ExactSpelling = true)]
    private static extern int OpticalDraw(IntPtr handle, uint w, uint h, float[] parameters);
    [DllImport("TheOrder.OpticalMaterial.dll", ExactSpelling = true)]
    private static extern int OpticalDestroy(IntPtr handle);
    private IntPtr handle;
    internal long Draws { get; private set; }
    internal long Uploads { get; private set; }
    internal long SourceBytes { get; private set; }
    internal long BufferBytes { get; private set; }
    internal double DrawCpuMilliseconds { get; private set; }
    internal double MaxDrawCpuMilliseconds { get; private set; }
    internal bool Active => handle != IntPtr.Zero;
    internal bool Ready => SourceBytes != 0;
    internal OpticalRenderer(SwapChainPanel panel)
    {
        using var abi = MarshalInspectable<SwapChainPanel>.CreateMarshaler(panel);
        Marshal.ThrowExceptionForHR(OpticalCreate(abi.ThisPtr, out handle));
    }
    internal void Upload(byte[] pixels, int width, int height)
    {
        ObjectDisposedException.ThrowIf(!Active, this);
        Marshal.ThrowExceptionForHR(OpticalUpload(handle, pixels, checked((uint)width), checked((uint)height), checked((uint)pixels.Length)));
        SourceBytes = pixels.LongLength; Uploads++;
    }
    internal void Draw(int width, int height, float[] parameters)
    {
        ObjectDisposedException.ThrowIf(!Active, this);
        if (parameters.Length != 20 || parameters.Any(x => !float.IsFinite(x))) throw new ArgumentException("Finite optical constants required");
        var begin = Stopwatch.GetTimestamp();
        Marshal.ThrowExceptionForHR(OpticalDraw(handle, checked((uint)width), checked((uint)height), parameters));
        var milliseconds = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        DrawCpuMilliseconds += milliseconds; MaxDrawCpuMilliseconds = Math.Max(MaxDrawCpuMilliseconds, milliseconds);
        Draws++; BufferBytes = (long)width * height * 8;
    }
    public void Dispose()
    {
        if (!Active) return;
        // Failed detach leaves ownership intact so Gallery can retry cleanup.
        Marshal.ThrowExceptionForHR(OpticalDestroy(handle));
        handle = IntPtr.Zero; SourceBytes = BufferBytes = 0;
    }
}
