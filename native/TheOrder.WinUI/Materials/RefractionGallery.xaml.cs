using System.Numerics;
using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TheOrder.WinUI.Materials;

/// <summary>
/// Development/Test-only Gallery. Its owner controls window visibility through
/// SetAdvanced and owns final Dispose. Unload releases every composition resource;
/// reload creates a new bounded tree. No timer or perpetual animation is owned.
/// </summary>
public sealed partial class RefractionGallery : UserControl, IDisposable
{
    private readonly UISettings? uiSettings;
    private readonly AccessibilitySettings? accessibility;
    private CompositionCapabilities? capabilities;
    private RefractionLens? lens;
    private bool advancedAllowed;
    private bool ownerAllowed;
    private bool ownerMotionAllowed = true;
    private bool loaded;
    private bool disposed;
    private bool initialized;
    private string? failure;
    private string? failurePhase;
    private string? failureHResult;
    private readonly Dictionary<string, NotificationWarning> notificationWarnings = new(StringComparer.Ordinal);
    private double patternPosition;
    private int allocationCycles;
    private bool effectsSubscribed;
    private bool animationsSubscribed;
    private bool contrastSubscribed;
    private bool capabilitySubscribed;
    private int cleanupFailures;
    private bool syncingPatternSlider;
    private bool? systemMotionAllowed;
    private Vector3 finiteMotionStart;
    private double finiteMotionStartPosition;
    private long finiteMotionUntil;
    private Color? appliedBackground;
    private Color? appliedForeground;

    public RefractionGallery() : this(true) { }
    public RefractionGallery(bool advancedAllowed = true)
    {
        ownerAllowed = advancedAllowed;
        this.advancedAllowed = true;
        InitializeComponent();
        var phase = "initialize.uiSettings";
        try
        {
            uiSettings = new UISettings();
            phase = "initialize.accessibilitySettings";
            accessibility = new AccessibilitySettings();
        }
        catch (Exception exception) { RecordFailure(phase, exception); }
        BuildPattern();
        initialized = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AdvancedSwitch.IsOn = true;
        AdvancedSwitch.IsEnabled = ownerAllowed;
        UpdateState();
    }

    internal object ResourceCounts => new
    {
        SurfaceCount = lens?.SurfaceCount ?? 0,
        BrushCount = lens?.BrushCount ?? 0,
        VisualCount = lens?.VisualCount ?? 0,
        ClipCount = lens?.ClipCount ?? 0,
        GeometryCount = lens?.GeometryCount ?? 0,
        EventSubscriptionCount = (effectsSubscribed ? 1 : 0) + (animationsSubscribed ? 1 : 0) +
            (contrastSubscribed ? 1 : 0) + (capabilitySubscribed ? 1 : 0),
        CleanupFailures = cleanupFailures,
        OwnedDeviceCount = 0,
        XamlPatternElementCount = PatternLayer.Children.Count,
        TimerCount = 0,
        PerpetualAnimationCount = 0,
        FiniteAnimationWithinRequestedWindow = finiteMotionUntil > Stopwatch.GetTimestamp(),
        OwnerMotionAllowed = ownerMotionAllowed,
        SystemMotionAllowed = systemMotionAllowed,
        AllocationCycles = allocationCycles,
        PatternPosition = patternPosition,
        AdvancedActive = lens is not null,
        Failure = failure,
        FailurePhase = failurePhase,
        FailureHResult = failureHResult,
        NotificationWarnings = notificationWarnings.Values.ToArray(),
        NotificationRoute = "Available events and existing owner policy refresh; no new timer",
        Source = "live app-internal CompositionVisualSurface",
        Model = "18 bounded geometric magnification bands"
    };

    internal void SetAdvanced(bool allowed)
    {
        if (disposed || ownerAllowed == allowed) return;
        ownerAllowed = allowed;
        if (initialized) AdvancedSwitch.IsEnabled = allowed;
        RefreshMaterial();
    }

    internal void SetMotionAllowed(bool allowed)
    {
        if (disposed || ownerMotionAllowed == allowed) return;
        ownerMotionAllowed = allowed;
        if (!allowed)
        {
            try { StopFiniteMotion(restoreStart: true); }
            catch (Exception exception) { SetCompositionFailure(exception, "motion.ownerStop"); }
        }
        UpdateState();
    }

    internal void StepPattern(double position)
    {
        if (disposed || !initialized) return;
        patternPosition = Math.Clamp(position, 0, 100);
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PatternLayer);
            visual.StopAnimation("Offset");
            finiteMotionUntil = 0;
            visual.Offset = new Vector3((float)(-patternPosition * 1.2), (float)(-patternPosition * .16), 0);
            SyncPatternSlider();
        }
        catch (Exception e) { SetCompositionFailure(e, "motion.stepPattern"); }
        UpdateState();
    }

    private void BuildPattern()
    {
        for (var row = 0; row < 11; row++)
        for (var column = 0; column < 22; column++)
        {
            var square = new Rectangle { Width = 64, Height = 64,
                Fill = new SolidColorBrush((row + column) % 2 == 0 ? Color.FromArgb(255, 223, 234, 237) : Color.FromArgb(255, 244, 246, 238)) };
            Canvas.SetLeft(square, column * 64); Canvas.SetTop(square, row * 64);
            PatternLayer.Children.Add(square);
        }
        AddBlock(74, 40, 116, 224, Color.FromArgb(255, 77, 137, 159));
        AddBlock(348, 164, 184, 128, Color.FromArgb(255, 204, 137, 89));
        AddBlock(704, 56, 154, 192, Color.FromArgb(255, 131, 165, 141));
        for (var i = 0; i < 26; i++)
        {
            var line = new Rectangle { Width = 2, Height = 680,
                Fill = new SolidColorBrush(Color.FromArgb(150, 33, 75, 91)) };
            Canvas.SetLeft(line, 18 + i * 48); PatternLayer.Children.Add(line);
        }
        for (var i = 0; i < 12; i++)
        {
            var line = new Rectangle { Width = 1300, Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(180, 44, 68, 78)) };
            Canvas.SetTop(line, 18 + i * 48); PatternLayer.Children.Add(line);
        }
    }

    private void AddBlock(double x, double y, double width, double height, Color color)
    {
        var block = new Rectangle { Width = width, Height = height, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(block, x); Canvas.SetTop(block, y); PatternLayer.Children.Add(block);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed || loaded) return;
        loaded = true;
        AttachSettings();
        try
        {
            capabilities ??= new CompositionCapabilities();
        }
        catch (Exception e2) { RecordFailure("initialize.compositionCapabilities", e2); }
        var composition = capabilities;
        if (composition is not null && !capabilitySubscribed)
            capabilitySubscribed = TryAttach("subscribe.compositionCapabilities", () => composition.Changed += OnCapabilitiesChanged);
        RefreshMaterial();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        ReleaseMaterial();
        DetachSettings();
        DetachCapabilities();
    }

    private void OnEffectsChanged(UISettings sender, object args) => QueueRefresh();
    private void OnContrastChanged(AccessibilitySettings sender, object args) => QueueRefresh();
    private void OnCapabilitiesChanged(CompositionCapabilities sender, object args) => QueueRefresh();
    private void OnAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => QueueRefresh();
    private void QueueRefresh()
    {
        if (disposed || !loaded) return;
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (disposed || !loaded) return;
                try
                {
                    var settings = ReadSettings();
                    if (!ownerMotionAllowed || settings.Animations != true) StopFiniteMotion(restoreStart: true);
                    RefreshMaterial();
                }
                catch (Exception exception) { SetCompositionFailure(exception, "callback.refresh"); }
            });
        }
        catch (Exception exception) { RecordFailure("callback.enqueue", exception); }
    }

    private void AttachSettings()
    {
        if (uiSettings is not null && !effectsSubscribed)
            effectsSubscribed = TryAttach("subscribe.advancedEffects", () => uiSettings.AdvancedEffectsEnabledChanged += OnEffectsChanged);
        if (uiSettings is not null && !animationsSubscribed)
            animationsSubscribed = TryAttach("subscribe.animations", () => uiSettings.AnimationsEnabledChanged += OnAnimationsChanged);
        if (accessibility is not null && !contrastSubscribed)
            contrastSubscribed = TryAttach("subscribe.highContrast", () => accessibility.HighContrastChanged += OnContrastChanged);
    }

    private readonly record struct NotificationWarning(string Phase, string ExceptionType, string HResult);

    private bool TryAttach(string phase, Action subscribe)
    {
        try { subscribe(); return true; }
        catch (Exception exception) { RecordNotificationWarning(phase, exception); return false; }
    }

    private void RecordNotificationWarning(string phase, Exception exception)
        => notificationWarnings[phase] = new(phase, exception.GetType().Name, $"0x{exception.HResult:X8}");

    private void DetachSettings()
    {
        if (effectsSubscribed && uiSettings is not null)
            TryDetach("unsubscribe.advancedEffects", () =>
            {
                uiSettings.AdvancedEffectsEnabledChanged -= OnEffectsChanged;
                effectsSubscribed = false;
            });
        if (animationsSubscribed && uiSettings is not null)
            TryDetach("unsubscribe.animations", () =>
            {
                uiSettings.AnimationsEnabledChanged -= OnAnimationsChanged;
                animationsSubscribed = false;
            });
        if (contrastSubscribed && accessibility is not null)
            TryDetach("unsubscribe.highContrast", () =>
            {
                accessibility.HighContrastChanged -= OnContrastChanged;
                contrastSubscribed = false;
            });
    }

    private void DetachCapabilities()
    {
        var currentCapabilities = capabilities;
        if (capabilitySubscribed && currentCapabilities is not null)
            TryDetach("unsubscribe.compositionCapabilities", () =>
            {
                currentCapabilities.Changed -= OnCapabilitiesChanged;
                capabilitySubscribed = false;
            });
        if (!capabilitySubscribed) capabilities = null;
    }

    private void TryDetach(string phase, Action detach)
    {
        try { detach(); }
        catch (Exception exception) { cleanupFailures++; RecordNotificationWarning(phase, exception); }
    }

    private readonly record struct Settings(bool? AdvancedEffects, bool? HighContrast, bool? Animations);

    private Settings ReadSettings()
    {
        var settings = uiSettings;
        var contrast = accessibility;
        var effects = settings is null ? null : Probe("query.advancedEffects", () => settings.AdvancedEffectsEnabled);
        var highContrast = contrast is null ? null : Probe("query.highContrast", () => contrast.HighContrast);
        systemMotionAllowed = settings is null ? null : Probe("query.animations", () => settings.AnimationsEnabled);
        return new(effects, highContrast, systemMotionAllowed);
    }

    private bool? Probe(string phase, Func<bool> read)
    {
        try { return read(); }
        catch (Exception exception) { RecordFailure(phase, exception); return null; }
    }

    private void RecordFailure(string phase, Exception exception)
    {
        failure = exception.GetType().Name;
        failurePhase = phase;
        failureHResult = $"0x{exception.HResult:X8}";
    }

    private void RefreshMaterial()
    {
        if (!initialized || disposed) return;
        var settings = ReadSettings();
        var composition = capabilities;
        var supported = composition is null ? null : Probe("query.compositionCapabilities", composition.AreEffectsSupported);
        var permitted = loaded && ownerAllowed && advancedAllowed && failure is null && settings.AdvancedEffects == true &&
            settings.HighContrast == false && supported == true;
        if (!permitted)
        {
            ReleaseMaterial();
            SetStablePalette(false);
            UpdateState();
            return;
        }
        try
        {
            if (lens is null)
            {
                lens = new RefractionLens(PatternSource, LensHost);
                allocationCycles++;
            }
            lens.Update(StrengthSlider.Value);
            SetStablePalette(true);
        }
        catch (Exception e)
        {
            // Public composition resources may fail on device/capability transitions.
            // Stay on solid until the owner recreates this experimental Gallery.
            RecordFailure("composition.createOrUpdate", e);
            ReleaseMaterial();
            SetStablePalette(false);
        }
        UpdateState();
    }

    private void SetStablePalette(bool transparent)
    {
        var background = Color.FromArgb(transparent ? (byte)140 : (byte)255, 240, 245, 246);
        var foregroundColor = Color.FromArgb(255, 24, 58, 69);
        try
        {
            if (ReadSettings().HighContrast == true && uiSettings is not null)
            {
                background = uiSettings.GetColorValue(UIColorType.Background);
                foregroundColor = uiSettings.GetColorValue(UIColorType.Foreground);
            }
        }
        catch (Exception exception)
        {
            RecordFailure("query.highContrastPalette", exception);
            background = Color.FromArgb(255, 240, 245, 246);
        }
        if (appliedBackground.HasValue && appliedBackground.Value.Equals(background) &&
            appliedForeground.HasValue && appliedForeground.Value.Equals(foregroundColor)) return;
        try
        {
            StableForeground.Background = new SolidColorBrush(background);
            var foreground = new SolidColorBrush(foregroundColor);
            StableIcon.Foreground = foreground;
            StableTitle.Foreground = foreground;
            StableBody.Foreground = foreground;
            StableCaption.Foreground = foreground;
            appliedBackground = background;
            appliedForeground = foregroundColor;
        }
        catch (Exception exception) { RecordFailure("presentation.stablePalette", exception); }
    }

    private void ReleaseMaterial()
    {
        try { ElementCompositionPreview.GetElementVisual(PatternLayer).StopAnimation("Offset"); } catch (Exception) { }
        finiteMotionUntil = 0;
        lens?.Dispose(); lens = null;
    }

    private void SetCompositionFailure(Exception exception, string phase)
    {
        RecordFailure(phase, exception);
        ReleaseMaterial();
        SetStablePalette(false);
    }

    private void SyncPatternSlider()
    {
        if (PatternSlider.Value == patternPosition) return;
        syncingPatternSlider = true;
        try { PatternSlider.Value = patternPosition; }
        finally { syncingPatternSlider = false; }
    }

    private void StopFiniteMotion(bool restoreStart)
    {
        var active = finiteMotionUntil > Stopwatch.GetTimestamp();
        var visual = ElementCompositionPreview.GetElementVisual(PatternLayer);
        visual.StopAnimation("Offset");
        finiteMotionUntil = 0;
        if (restoreStart && active)
        {
            // Getters expose base values, not compositor-interpolated values. Hold
            // the known start state instead of claiming an unobservable current frame.
            visual.Offset = finiteMotionStart;
            patternPosition = finiteMotionStartPosition;
            SyncPatternSlider();
        }
    }

    private void UpdateState()
    {
        if (!initialized) return;
        try
        {
            var settings = ReadSettings();
            if (failure is not null && lens is not null) { ReleaseMaterial(); SetStablePalette(false); }
            var caption = lens is null ? "实色降级 / solid fallback" : "实时内部像素位移 / live internal source";
            if (StableCaption.Text != caption) StableCaption.Text = caption;
            var state = lens is null
                ? $"稳定实色降级 · 原型资源 0 · {failure ?? (settings.HighContrast == true ? "高对比度" : settings.AdvancedEffects == false ? "系统已关闭透明效果" : "原型未启用") }"
                : $"实时内部背景折射 · 1 个共享表面 / 18 个有界采样带 · 背景位置 {patternPosition:0} · 仅手动短演示";
            if (MaterialState.Text != state) MaterialState.Text = state;
        }
        catch (Exception exception) { RecordFailure("presentation.state", exception); ReleaseMaterial(); }
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => RefreshMaterial();
    private void OnAdvancedToggled(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        advancedAllowed = AdvancedSwitch.IsOn;
        RefreshMaterial();
    }
    private void OnPatternChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (initialized && !syncingPatternSlider) StepPattern(e.NewValue);
    }
    private void OnLensChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        LensFrame.RenderTransform = new TranslateTransform { X = e.NewValue };
        RefreshMaterial();
    }
    private void OnStrengthChanged(object sender, RangeBaseValueChangedEventArgs e) { if (initialized) RefreshMaterial(); }
    private void OnResetPattern(object sender, RoutedEventArgs e)
    {
        StepPattern(0);
        LensSlider.Value = 0;
    }
    private void OnMovePattern(object sender, RoutedEventArgs e)
    {
        if (!initialized || disposed) return;
        var settings = ReadSettings();
        if (!ownerMotionAllowed || settings.Animations != true)
        {
            try { StopFiniteMotion(restoreStart: true); }
            catch (Exception exception) { SetCompositionFailure(exception, "motion.disabledStop"); }
            RefreshMaterial();
            return;
        }
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PatternLayer);
            var startOffset = visual.Offset;
            finiteMotionStart = startOffset;
            finiteMotionStartPosition = patternPosition;
            patternPosition = 0;
            SyncPatternSlider();
            visual.StopAnimation("Offset");
            using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0, startOffset);
            animation.InsertKeyFrame(.5f, new Vector3(-120, -16, 0));
            animation.InsertKeyFrame(1, Vector3.Zero);
            animation.Duration = TimeSpan.FromSeconds(3);
            animation.IterationBehavior = AnimationIterationBehavior.Count;
            animation.IterationCount = 1;
            visual.Offset = Vector3.Zero;
            visual.StartAnimation("Offset", animation);
            finiteMotionUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        }
        catch (Exception e2) { SetCompositionFailure(e2, "motion.start"); }
        UpdateState();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        loaded = false;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachSettings();
        DetachCapabilities();
        ReleaseMaterial();
    }
}
