using System.Runtime.InteropServices;
using TheOrder.Application;
using TheOrder.Platform.Windows;

namespace TheOrder.Platform.Windows;

// Four immutable native icons for the window and shell. WM_SETICON and the tray
// borrow these handles; the application deletes them only after both users detach.
public sealed class WindowsBrandIcons : IDisposable
{
    private readonly IntPtr[] owned = new IntPtr[4];
    private bool disposed;
    public IntPtr Small(bool light) => owned[light ? 0 : 1];
    public IntPtr Large(bool light) => owned[light ? 2 : 3];
    public static async Task<WindowsBrandIcons> LoadAsync(string assetPath)
    {
        var bytes = await File.ReadAllBytesAsync(assetPath);
        var codec = new WindowsRasterCodec();
        var small = await codec.DecodeAsync(bytes, 32, default); var large = await codec.DecodeAsync(bytes, 64, default);
        var result = new WindowsBrandIcons();
        try { result.owned[0] = Create(small, true); result.owned[1] = Create(small, false); result.owned[2] = Create(large, true); result.owned[3] = Create(large, false); return result; }
        catch { result.Dispose(); throw; }
    }
    internal static byte[] Monochrome(RasterPixels pixels, bool light)
    {
        var output = new byte[pixels.Bgra.Length];
        for (int i = 0; i < output.Length; i += 4) {
            byte alpha = pixels.Bgra[i + 3]; var value = light ? (byte)0 : (byte)((alpha * 245 + 127) / 255);
            output[i] = output[i + 1] = output[i + 2] = value; output[i + 3] = alpha;
        }
        return output;
    }
    private static IntPtr Create(RasterPixels pixels, bool light)
    {
        var info = new BitmapInfo { Header = new() { Size = 40, Width = pixels.Width, Height = -pixels.Height, Planes = 1, Bits = 32 } };
        var color = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        IntPtr mask = IntPtr.Zero;
        try {
            var rgba = Monochrome(pixels, light); Marshal.Copy(rgba, 0, bits, rgba.Length);
            var maskBytes = new byte[((pixels.Width + 15) / 16) * 2 * pixels.Height];
            mask = CreateBitmap(pixels.Width, pixels.Height, 1, 1, maskBytes);
            if (mask == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var icon = new IconInfo { Icon = true, Color = color, Mask = mask }; var result = CreateIconIndirect(ref icon);
            return result != IntPtr.Zero ? result : throw new System.ComponentModel.Win32Exception();
        } finally { if (mask != IntPtr.Zero) DeleteObject(mask); DeleteObject(color); }
    }
    public void Apply(IntPtr window, bool light)
    { if (disposed) return; SendMessage(window, 0x80, 0, Small(light)); SendMessage(window, 0x80, 1, Large(light)); }
    public static void Detach(IntPtr window)
    { if (window == IntPtr.Zero) return; SendMessage(window, 0x80, 0, IntPtr.Zero); SendMessage(window, 0x80, 1, IntPtr.Zero); }
    public static bool SystemLight()
    {
        try {
            if (new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) {
                var foreground = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
                return foreground.R + foreground.G + foreground.B < 384;
            }
        } catch { }
        // Apps and taskbar may have different modes. Read the shell's mode, not Root.ActualTheme.
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("SystemUsesLightTheme") is int mode ? mode != 0 : false; }
        catch { return false; }
    }
    public void Dispose()
    { if (disposed) return; disposed = true; for (int i = 0; i < owned.Length; i++) { if (owned[i] != IntPtr.Zero) DestroyIcon(owned[i]); owned[i] = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint X, Y; public IntPtr Mask, Color; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, nuint wparam, IntPtr lparam);
}
