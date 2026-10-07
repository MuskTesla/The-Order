using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

namespace TheOrder.WinUI.Materials;

public sealed partial class RefractionGallery
{
    private OpticalRenderer? optical;
    private OpticalRenderer? failedOpticalRelease;
    private bool snapshotBusy, snapshotDirty = true, renderingSubscribed, dispersionEnabled = true;
    private int snapshotGeneration;
    private long snapshotCount, opticalDrawsReleased, opticalUploadsReleased;
    private int snapshotWidth, snapshotHeight;
    private double snapshotScale;
    private double opticalCpuReleased, opticalMaxCpuReleased;
    private long motionStarted, previousFrame;
    private double motionSeconds, motionStartPosition, frameIntervalTotal, frameIntervalMax;
    private long frameIntervals;
    private readonly float[] opticalConstants = new float[20];
    private bool UsesOptical => ComparisonMode.SelectedIndex is 3 or 4;
    private bool MaterialActive => lens is not null || optical?.Ready == true;
    private OpticalProfile Profile => ComparisonMode.SelectedIndex == 3 ? OpticalProfile.Reference : OpticalProfile.Daily;
    private object OpticalEvidence => new
    {
        Active = optical?.Ready == true, SnapshotPending = snapshotBusy, SnapshotDirty = snapshotDirty,
        SnapshotCount = snapshotCount, SourceTextureBytes = optical?.SourceBytes ?? 0,
        SnapshotWidth = snapshotWidth, SnapshotHeight = snapshotHeight, SnapshotScale = snapshotScale,
        SwapchainBufferBytes = optical?.BufferBytes ?? 0, DeviceCount = optical is null ? 0 : 1,
        RenderingSubscription = renderingSubscribed ? 1 : 0,
        Draws = opticalDrawsReleased + (optical?.Draws ?? 0),
        Uploads = opticalUploadsReleased + (optical?.Uploads ?? 0),
        DrawCpuTotalMs = opticalCpuReleased + (optical?.DrawCpuMilliseconds ?? 0),
        DrawCpuMaxMs = Math.Max(opticalMaxCpuReleased, optical?.MaxDrawCpuMilliseconds ?? 0),
        FrameIntervals = frameIntervals, FrameIntervalMeanMs = frameIntervals == 0 ? 0 : frameIntervalTotal / frameIntervals,
        FrameIntervalMaxMs = frameIntervalMax,
        Profile.BevelWidth, Profile.Depth, Dispersion = dispersionEnabled ? Profile.Dispersion : 0, Profile.Gloss,
        RetainedFailedDevice = failedOpticalRelease is null ? 0 : 1,
        Source = "Gallery-only XAML background snapshot at rasterization scale; finite shader translation; foreground excluded",
        Passes = 1, TextureReadsOutside = 1, TextureReadsLensMaximum = 4, BlurPasses = 0
    };

    private void UpdateOptical()
    {
        if (snapshotScale != 0 && snapshotScale != XamlRoot.RasterizationScale && !snapshotDirty) { snapshotGeneration++; snapshotDirty = true; }
        if (lens is not null) ReleaseMaterial();
        if (optical is null)
        {
            optical = new OpticalRenderer(OpticalPanel);
            allocationCycles++; snapshotDirty = true;
            if (failInitialization) throw new InvalidOperationException("Test partial optical initialization failure");
        }
        if (snapshotDirty) CaptureOpticalSource();
        else DrawOptical((float)(-patternPosition * 1.2), (float)(-patternPosition * .16));
    }

    private async void CaptureOpticalSource()
    {
        if (snapshotBusy || !snapshotDirty || optical is null || pendingCovers.Count != 0 || !loaded || disposed) return;
        snapshotBusy = true;
        var generation = snapshotGeneration;
        var owner = optical;
        RenderTargetBitmap? bitmap = null;
        try
        {
            bitmap = new RenderTargetBitmap();
            // Capture a fixed parent: Canvas render bounds alone include overflow.
            // PatternCapture never contains OpticalPanel or LensFrame.
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(PatternLayer).Offset = Vector3.Zero;
            await bitmap.RenderAsync(PatternCapture);
            var buffer = await bitmap.GetPixelsAsync();
            try
            {
                if (disposed || !loaded || generation != snapshotGeneration || optical != owner) return;
                if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidDataException("Empty Gallery snapshot");
                owner.Upload(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
                snapshotWidth = bitmap.PixelWidth; snapshotHeight = bitmap.PixelHeight;
                snapshotScale = XamlRoot.RasterizationScale;
                snapshotDirty = false; snapshotCount++;
                DrawOptical((float)(-patternPosition * 1.2), (float)(-patternPosition * .16));
                UpdateState();
            }
            finally { ((IWinRTObject)buffer).NativeObject.Dispose(); }
        }
        catch (Exception exception) { if (!disposed && generation == snapshotGeneration) SetCompositionFailure(exception, "optical.snapshot"); }
        finally
        {
            if (bitmap is not null) ((IWinRTObject)bitmap).NativeObject.Dispose();
            snapshotBusy = false;
            if (!disposed && loaded && snapshotDirty && optical is not null && generation != snapshotGeneration)
                DispatcherQueue.TryEnqueue(CaptureOpticalSource);
        }
    }

    private void DrawOptical(float offsetX, float offsetY)
    {
        if (snapshotDirty || optical?.Ready != true || !loaded || disposed || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0) return;
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        var p = LensHost.TransformToVisual(Stage).TransformPoint(new Windows.Foundation.Point());
        var model = Profile;
        var c = opticalConstants;
        c[0] = (float)Stage.ActualWidth; c[1] = (float)Stage.ActualHeight; c[2] = 1300; c[3] = 660;
        c[4] = (float)(p.X + LensHost.ActualWidth / 2); c[5] = (float)(p.Y + LensHost.ActualHeight / 2);
        c[6] = (float)LensHost.ActualWidth / 2; c[7] = (float)LensHost.ActualHeight / 2;
        c[8] = 23; c[9] = model.BevelWidth; c[10] = model.Depth; c[11] = dispersionEnabled ? model.Dispersion : 0;
        c[12] = model.Gloss; c[13] = ActualTheme == ElementTheme.Dark ? 1 : 0;
        c[14] = offsetX; c[15] = offsetY; c[16] = scale;
        optical.Draw((int)Math.Ceiling(c[0] * scale), (int)Math.Ceiling(c[1] * scale), c);
        OpticalPanel.Visibility = Visibility.Visible;
    }

    private void StartOpticalMotion(double seconds)
    {
        StopOpticalMotion();
        motionSeconds = seconds; motionStarted = Stopwatch.GetTimestamp(); previousFrame = 0;
        motionStartPosition = patternPosition;
        finiteMotionUntil = motionStarted + (long)(seconds * Stopwatch.Frequency);
        CompositionTarget.Rendering += OnOpticalFrame; renderingSubscribed = true;
    }
    private void OnOpticalFrame(object? sender, object args)
    {
        if (optical?.Ready != true || disposed || !loaded || !ownerAllowed || !ownerMotionAllowed)
        { StopOpticalMotion(); return; }
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (previousFrame != 0) { var ms = Stopwatch.GetElapsedTime(previousFrame, now).TotalMilliseconds; frameIntervals++; frameIntervalTotal += ms; frameIntervalMax = Math.Max(frameIntervalMax, ms); }
            previousFrame = now;
            var t = Math.Clamp(Stopwatch.GetElapsedTime(motionStarted, now).TotalSeconds / motionSeconds, 0, 1);
            var position = t < .5 ? motionStartPosition + (100 - motionStartPosition) * t * 2 : (1 - t) * 200;
            DrawOptical((float)(-position * 1.2), (float)(-position * .16));
            if (t >= 1) { StopOpticalMotion(); patternPosition = 0; SyncPatternSlider(); }
        }
        catch (Exception exception) { SetCompositionFailure(exception, "optical.render"); }
    }
    private void StopOpticalMotion()
    {
        if (renderingSubscribed) { CompositionTarget.Rendering -= OnOpticalFrame; renderingSubscribed = false; }
        finiteMotionUntil = 0;
    }
    private void ReleaseOptical()
    {
        StopOpticalMotion(); snapshotGeneration++; snapshotDirty = true;
        OpticalPanel.Visibility = Visibility.Collapsed;
        if (optical is not null)
        {
            opticalDrawsReleased += optical.Draws; opticalUploadsReleased += optical.Uploads;
            opticalCpuReleased += optical.DrawCpuMilliseconds; opticalMaxCpuReleased = Math.Max(opticalMaxCpuReleased, optical.MaxDrawCpuMilliseconds);
            try { optical.Dispose(); }
            catch (Exception exception) { cleanupFailures++; failedOpticalRelease = optical; RecordFailure("optical.detach", exception); }
            optical = null;
        }
    }
}
