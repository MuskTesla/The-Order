using System.Runtime.InteropServices;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class WindowsArtworkTextDetectorTests
{
    [Fact]
    public async Task BundledCpuModelDistinguishesBlankFromSyntheticOverlayText()
    {
        await using var detector = new WindowsArtworkTextDetector();
        var blank = await detector.AnalyzeAsync(Raster(false), default);
        Assert.True(blank.Available); Assert.Empty(blank.Regions);
        var text = await detector.AnalyzeAsync(Raster(true), default);
        Assert.True(text.Available); Assert.NotEmpty(text.Regions);
        Assert.True(ArtworkTextRules.ContainsOverlayText(text));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task MissingOrChangedModelDoesNotOptIntoUnsafeBannerSelection(bool changed)
    {
        using var fixture = new TransactionProfile();
        var path = Path.Combine(fixture.Root, "model.onnx");
        if (changed) await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var detector = new WindowsArtworkTextDetector(path);
        Assert.False((await detector.AnalyzeAsync(Raster(true), default)).Available);
        Assert.False((await detector.AnalyzeAsync(Raster(false), default)).Available);
    }

    [Fact]
    public async Task CancellationDoesNotPoisonTheNextLocalEvaluation()
    {
        await using var detector = new WindowsArtworkTextDetector();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.AnalyzeAsync(Raster(false), cancel.Token));
        Assert.True((await detector.AnalyzeAsync(Raster(false), default)).Available);
    }

    [Fact]
    public async Task ConcurrentWorkIsDrainedBeforeDisposingTheCpuSession()
    {
        var detector = new WindowsArtworkTextDetector();
        var first = detector.AnalyzeAsync(Raster(false), default);
        var second = detector.AnalyzeAsync(Raster(true), default);
        await detector.DisposeAsync();
        Assert.True((await first).Available); Assert.True((await second).Available);
        Assert.False((await detector.AnalyzeAsync(Raster(false), default)).Available);
        await detector.DisposeAsync();
    }

    // Windowless GDI creates only synthetic pixels; no user screen or game art.
    private static RasterPixels Raster(bool text)
    {
        const int width = 384, height = 128;
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint dc = CreateCompatibleDC(0), bitmap = 0, font = 0, oldBitmap = 0, oldFont = 0;
        try
        {
            bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (dc == 0 || bitmap == 0 || bits == 0) throw new InvalidOperationException("Synthetic GDI surface failed");
            oldBitmap = SelectObject(dc, bitmap);
            var bytes = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            if (text)
            {
                font = CreateFontW(-38, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Arial");
                if (font == 0) throw new InvalidOperationException("Synthetic font failed");
                oldFont = SelectObject(dc, font); SetTextColor(dc, 0); SetBkMode(dc, 1);
                if (!TextOutW(dc, 25, 40, "THE ORDER", 9)) throw new InvalidOperationException("Synthetic text failed");
            }
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new(width, height, bytes);
        }
        finally
        {
            if (oldFont != 0) SelectObject(dc, oldFont);
            if (font != 0) DeleteObject(font);
            if (oldBitmap != 0) SelectObject(dc, oldBitmap);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint dc, int x, int y, string text, int length);
}
