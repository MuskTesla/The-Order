using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TheOrder.Application;
using TheOrder.Presentation;
using TheOrder.WinUI.Materials;

namespace TheOrder.WinUI.Views;

// App quality setting (设置 › 材质效果). System rules (MaterialPolicy) always win.
public enum MaterialMode { Auto, Advanced, Standard, Opaque }
public enum StageVariant { Clear, Continuous }
public enum StagePolarity { Deepen, Lift }

// Artwork stage: an app-rendered ambient band built from the game's own decoded cover pixels, with one
// Liquid Glass plate. The background is stable (it changes only with cover, size, DPI or policy), so the
// glass is event-driven: one scene revision per change, draws only on geometry change, zero idle work.
// Approved configurations (review 2026-10-08): 完全清透 = body mode 3, 连续玻璃 = body mode 2, both with
// C1 geometry 23/30/30/.8/.55, edge density .20, luminance bound .42 (lift) / .08 (deepen).
public sealed partial class ArtworkStage : Grid
{
    public const string ClearConfiguration = "完全清透 · mode3 · C1 23/30/30/.8/.55 · tint α .10/.16 · bound .42/.08 · edge .20";
    public const string ContinuousConfiguration = "连续玻璃 · mode2 · C1 23/30/30/.8/.55 · σ12 · tint α .22/.30 · bound .42/.08 · edge .20";
    private readonly Image fallbackArt = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Rectangle fallbackScrim = new() { IsHitTestVisible = false };
    private readonly SwapChainPanel panel = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ImageRequestSlot request = new();
    private readonly float[] constants = new float[28];
    private DispatcherQueueTimer? debounce;
    private OpticalRenderer? optical;
    private RasterPixels? pixels;
    private string? pixelsKey, sceneKey, geometry;
    private double sceneWidth, sceneHeight, sceneScale;
    private bool loaded, allowed, highContrast, attached, initFailed, retirementPending;
    private UIElement? overlay;
    public IImagePipeline? Images { get; set; }
    public Border? Plate { get; set; }
    public StageVariant Variant { get; set; } = StageVariant.Clear;
    public float AmbientSigma { get; set; } = 18;
    public double StageCornerRadius { get; set; }
    public MaterialMode Mode { get; private set; } = MaterialMode.Auto;
    public StagePolarity Polarity { get; private set; } = StagePolarity.Deepen;
    public string Effective { get; private set; } = "Standard";
    public string? LastFailure { get; private set; }
    public long SceneRevisions { get; private set; }
    public long Draws { get; private set; }
    public long RenderersCreated { get; private set; }
    public long RenderersReleased { get; private set; }
    public long FrostPasses { get; private set; }
    // Text colour of the plate: polarity-driven over artwork, theme-driven when the stage is opaque/HC.
    public event Action<Brush?>? ForegroundChanged;
    public UIElement? Overlay
    {
        get => overlay;
        set { if (overlay is not null) Children.Remove(overlay); overlay = value; if (value is not null) Children.Add(value); }
    }
    private string? coverKey;
    public string? CoverKey
    {
        get => coverKey;
        set
        {
            if (coverKey == value) return;
            // Retire the old artwork before awaiting another cover. A missing/failed cover must not
            // leave the preceding game's swapchain or fallback bitmap behind the new foreground.
            request.Clear(); Release(); fallbackArt.Source = null;
            coverKey = value; pixels = null; pixelsKey = null; sceneKey = null;
            ForegroundChanged?.Invoke(null);
            Invalidate(immediate: true);
        }
    }
    public ArtworkStage()
    {
        Children.Add(fallbackArt); Children.Add(fallbackScrim); Children.Add(panel);
        Loaded += (_, _) => { loaded = true; Attach(); Invalidate(immediate: true); };
        // Re-parenting (e.g. grid header → list header) can raise the old Unloaded after the new Loaded.
        Unloaded += (_, _) => { if (IsLoaded) return; loaded = false; Detach(); Release(); request.Clear(); };
        SizeChanged += (_, _) => { ApplyClip(); if (optical is not null) DrawNow(); Invalidate(immediate: false); };
    }
    // Called by the window policy owner: effects allowed and window visible, plus the user's quality mode.
    public void SetPolicy(bool effectsAllowed, bool contrast, MaterialMode mode)
    {
        var changed = effectsAllowed != allowed || contrast != highContrast || mode != Mode;
        allowed = effectsAllowed; highContrast = contrast;
        if (mode != Mode) { Mode = mode; initFailed = false; }
        if (changed) Invalidate(immediate: true);
    }
    // Before the page leaves the visual tree (proven early-release order for swapchains).
    public void ReleaseBeforeLeave() { loaded = false; request.Clear(); Detach(); Release(); }
    // After an explicit early release while staying in the tree (re-parenting), resume event-driven drawing.
    public void Reattach() { if (!IsLoaded) return; loaded = true; Attach(); Invalidate(immediate: true); }
    private void Attach()
    {
        if (attached) return; attached = true;
        LayoutUpdated += OnLayoutUpdated;
        if (XamlRoot is not null) XamlRoot.Changed += OnRootChanged;
    }
    private void Detach()
    {
        if (!attached) return; attached = false;
        LayoutUpdated -= OnLayoutUpdated;
        if (XamlRoot is not null) XamlRoot.Changed -= OnRootChanged;
        debounce?.Stop();
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) { if (sender.RasterizationScale != sceneScale) Invalidate(immediate: false); }
    private void OnLayoutUpdated(object? sender, object e)
    {
        if (optical is null || Plate is null || !loaded) return;
        var rect = PlateRect();
        var key = $"{rect.X:F1}/{rect.Y:F1}/{rect.Width:F1}/{rect.Height:F1}/{ActualWidth:F1}/{ActualHeight:F1}";
        if (key != geometry) DrawNow();
    }
    private Windows.Foundation.Rect PlateRect()
    {
        if (Plate is null || Plate.ActualWidth <= 0) return default;
        var p = Plate.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point());
        return new(p.X, p.Y, Plate.ActualWidth, Plate.ActualHeight);
    }
    private void ApplyClip()
    {
        if (StageCornerRadius <= 0 || ActualWidth <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)ActualWidth, (float)ActualHeight); geometry.CornerRadius = new Vector2((float)StageCornerRadius);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }
    private void Invalidate(bool immediate)
    {
        if (!loaded) return;
        if (immediate) { Refresh(); return; }
        debounce ??= CreateDebounce();
        debounce.Stop(); debounce.Start();
    }
    private DispatcherQueueTimer CreateDebounce()
    {
        var timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(120); timer.IsRepeating = false;
        timer.Tick += (_, _) => Refresh(); return timer;
    }
    private string EffectiveMode() => highContrast ? "HighContrast" : Mode == MaterialMode.Opaque || !allowed ? "Opaque"
        : Mode == MaterialMode.Standard || (Mode == MaterialMode.Auto && initFailed) ? "Standard" : "Glass";
    private void Refresh()
    {
        if (!loaded) return;
        // OpticalDestroy can fail to detach. Keep its owner for a later lifecycle retry,
        // and never draw or replace it while retirement is incomplete.
        if (retirementPending) { Release(); if (retirementPending) return; }
        Effective = EffectiveMode();
        if (Effective != "Glass") Release();
        ApplyLayers();
        if (coverKey is null) { ForegroundChanged?.Invoke(null); return; }
        if (pixels is null || pixelsKey != coverKey) { StartLoad(); return; }
        if (Effective != "Glass") return;
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1;
            if (ActualWidth < 1 || ActualHeight < 1) return;
            if (optical is null) { optical = new OpticalRenderer(panel); RenderersCreated++; sceneKey = null; }
            var key = $"{pixelsKey}/{ActualWidth:F0}/{ActualHeight:F0}/{scale}/{Variant}/{AmbientSigma}/{Polarity}";
            if (key != sceneKey)
            {
                var scrim = Polarity == StagePolarity.Deepen ? 0x38000000u : 0x2EFFFFFFu;
                optical.RenderArtwork(pixels, ActualWidth, ActualHeight, scale, AmbientSigma, 0xFF000000u, scrim);
                sceneWidth = ActualWidth; sceneHeight = ActualHeight; sceneScale = scale; sceneKey = key; SceneRevisions++;
                if (Variant == StageVariant.Continuous) { optical.Frost(12); FrostPasses++; }
                else if (optical.FrostBytes != 0) optical.Frost(0);
            }
            DrawNow();
        }
        catch (Exception exception) { Fail(exception); }
    }
    private void DrawNow()
    {
        if (optical is null || Plate is null || sceneWidth <= 0) return;
        try
        {
            var rect = PlateRect();
            if (rect.Width < 64 || rect.Height < 64) { panel.Visibility = Visibility.Collapsed; return; }
            var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
            var lift = Polarity == StagePolarity.Lift; var clear = Variant == StageVariant.Clear;
            var c = constants;
            c[0] = (float)ActualWidth; c[1] = (float)ActualHeight; c[2] = (float)sceneWidth; c[3] = (float)sceneHeight;
            c[4] = (float)(rect.X + rect.Width / 2); c[5] = (float)(rect.Y + rect.Height / 2); c[6] = (float)rect.Width / 2; c[7] = (float)rect.Height / 2;
            c[8] = 23; c[9] = 30; c[10] = 30; c[11] = .8f; c[12] = .55f; c[13] = 0; c[14] = c[15] = 0; c[16] = scale;
            c[17] = 0; c[18] = clear ? 1 : 0; c[19] = 0;
            c[20] = c[21] = c[22] = lift ? 250 / 255f : 32 / 255f;
            c[23] = clear ? (lift ? .10f : .16f) : (lift ? .22f : .30f);
            c[24] = 1; c[25] = lift ? .42f : .08f; c[26] = lift ? 1 : -1; c[27] = .20f;
            optical.Draw((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale), c);
            geometry = $"{rect.X:F1}/{rect.Y:F1}/{rect.Width:F1}/{rect.Height:F1}/{ActualWidth:F1}/{ActualHeight:F1}";
            Draws++;
            panel.Visibility = Visibility.Visible; fallbackArt.Visibility = Visibility.Collapsed; fallbackScrim.Visibility = Visibility.Collapsed;
            if (Plate.Background is not SolidColorBrush { Color.A: 0 }) Plate.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        catch (Exception exception) { Fail(exception); }
    }
    private void StartLoad()
    {
        if (Images is null || coverKey is null) return;
        var key = coverKey;
        var edge = 512;
        var lease = request.Begin(key);
        _ = LoadAsync(Images, key, edge, lease.Generation, lease.Cancellation);
    }
    private async Task LoadAsync(IImagePipeline images, string key, int edge, long version, CancellationToken cancellation)
    {
        try
        {
            var result = await images.LoadAsync(key, edge, cancellation);
            if (!loaded || !request.IsCurrent(key, version) || coverKey != key || result.Pixels is not { } loadedPixels) return;
            pixels = loadedPixels; pixelsKey = key;
            Polarity = MeanLuminance(loadedPixels) > .30 ? StagePolarity.Lift : StagePolarity.Deepen;
            var bitmap = new WriteableBitmap(loadedPixels.Width, loadedPixels.Height);
            using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(loadedPixels.Bgra);
            bitmap.Invalidate(); fallbackArt.Source = bitmap;
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (request.IsCurrent(key, version)) { LastFailure = "cover:" + exception.GetType().Name; ApplyLayers(); } }
    }
    // Mean linear luminance over a sparse grid of the decoded cover (once per cover).
    internal static double MeanLuminance(RasterPixels value)
    {
        static double Linear(byte channel) { var c = channel / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        double sum = 0; var count = 0; var stepX = Math.Max(1, value.Width / 48); var stepY = Math.Max(1, value.Height / 48);
        for (var y = 0; y < value.Height; y += stepY)
            for (var x = 0; x < value.Width; x += stepX)
            {
                var i = (y * value.Width + x) * 4;
                sum += .2126 * Linear(value.Bgra[i + 2]) + .7152 * Linear(value.Bgra[i + 1]) + .0722 * Linear(value.Bgra[i]); count++;
            }
        return count == 0 ? 0 : sum / count;
    }
    private void ApplyLayers()
    {
        var art = Effective is "Glass" or "Standard" && fallbackArt.Source is not null && pixelsKey == coverKey;
        var glassShowing = Effective == "Glass" && optical is not null && panel.Visibility == Visibility.Visible;
        fallbackArt.Visibility = art && !glassShowing ? Visibility.Visible : Visibility.Collapsed;
        fallbackScrim.Visibility = fallbackArt.Visibility;
        fallbackScrim.Fill = new SolidColorBrush(Polarity == StagePolarity.Deepen ? Windows.UI.Color.FromArgb(0x8C, 0, 0, 0) : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        // Overlay theme first (R3), then surfaces: the plate must never resolve theme brushes under a stale theme.
        var overArt = Effective is "Glass" or "Standard" && (art || glassShowing);
        ForegroundChanged?.Invoke(overArt ? new SolidColorBrush(Polarity == StagePolarity.Deepen ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xE4, 0, 0, 0)) : null);
        Background = Effective is "Opaque" or "HighContrast" || !art && !glassShowing ? OwnedBrush(ref layerBrush, "LayerFillColorDefault") : null;
        if (Plate is not null && Effective != "Glass")
        {
            // Standard: system in-app Acrylic, Fluent radius. Opaque/HC: card surface.
            Plate.CornerRadius = new CornerRadius(8);
            Plate.Background = Effective == "Standard" && art
                ? new AcrylicBrush { TintColor = Polarity == StagePolarity.Deepen ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250), TintOpacity = .55, TintLuminosityOpacity = .65,
                    FallbackColor = Polarity == StagePolarity.Deepen ? Windows.UI.Color.FromArgb(255, 44, 44, 44) : Windows.UI.Color.FromArgb(255, 245, 245, 245) }
                : OwnedBrush(ref contentBrush, "CardBackgroundFillColorDefault");
        }
        else if (Plate is not null) Plate.CornerRadius = new CornerRadius(23);
    }
    // Stage-owned theme brushes. Shared app brushes must not be assigned here: the plate can carry a different
    // RequestedTheme (polarity), and a shared brush's ThemeResource would be re-resolved for every user of it.
    private Brush? layerBrush, contentBrush;
    private static Brush OwnedBrush(ref Brush? cache, string colorKey) => cache ??= (Brush)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        $"<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='{{ThemeResource {colorKey}}}'/>");
    private void Fail(Exception exception)
    {
        LastFailure = exception.GetType().Name + ":0x" + exception.HResult.ToString("X8");
        initFailed = true; Release(); Effective = "Standard"; ApplyLayers();
    }
    private void Release()
    {
        panel.Visibility = Visibility.Collapsed;
        var owner = optical; sceneKey = null; geometry = null; sceneWidth = 0;
        if (owner is null) return;
        retirementPending = true;
        try { owner.Dispose(); optical = null; retirementPending = false; RenderersReleased++; }
        catch (Exception exception) { LastFailure = "release:" + exception.GetType().Name; }
    }
    internal object Evidence => new
    {
        effective = Effective, mode = Mode.ToString(), variant = Variant.ToString(), configuration = Variant == StageVariant.Clear ? ClearConfiguration : ContinuousConfiguration,
        coverKey, loadedCoverKey = pixelsKey, fallbackPresent = fallbackArt.Source is not null, plateBrush = Plate?.Background?.GetType().Name,
        polarity = Polarity.ToString(), ambientSigma = AmbientSigma, allowed, highContrast, rendererActive = optical is not null, panelVisible = panel.Visibility == Visibility.Visible,
        sceneRevisions = SceneRevisions, draws = Draws, frostPasses = FrostPasses, renderersCreated = RenderersCreated, renderersReleased = RenderersReleased,
        sourceBytes = optical?.SourceBytes ?? 0, frostBytes = optical?.FrostBytes ?? 0, coverLoaded = pixelsKey is not null && pixelsKey == coverKey, failure = LastFailure, attached, retirementPending
    };
}
