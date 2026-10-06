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
    private bool refreshQueued;
    private int callbackGeneration;
    private long updatesReleased, sourceUpdates;
    private RefractionLens? failedRelease;
    private string testPolicy = "normal";
    private string interaction = "normal";
    private bool failInitialization;
    private Vector3 finiteMotionStart;
    private double finiteMotionStartPosition;
    private long finiteMotionUntil;
    private Color? appliedBackground;
    private Color? appliedForeground;

    public RefractionGallery() : this(true) { }
    public RefractionGallery(bool advancedAllowed = true, bool requested = false)
    {
        ownerAllowed = advancedAllowed;
        this.advancedAllowed = requested;
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
        AdvancedSwitch.IsOn = requested;
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
        Model = "continuous opacity-masked affine edge; quiet 1:1 center",
        Requested = advancedAllowed, Actual = lens is null ? "Solid" : "Advanced",
        FallbackReason = lens is not null ? null : failure ?? (!loaded ? "Unloaded" : !ownerAllowed ? "OwnerPolicyOrHidden" : !advancedAllowed ? "Disabled" : testPolicy != "normal" ? testPolicy : "CapabilityUnavailable"),
        UpdateCount = updatesReleased + (lens?.UpdateCount ?? 0), SourceUpdateCount = sourceUpdates,
        RetainedFailedResources = failedRelease?.RemainingResources ?? 0,
        PendingCallbackCount = refreshQueued ? 1 : 0, TestPolicy = testPolicy, Interaction = interaction
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
        patternPosition = Math.Clamp(position, 0, 100); sourceUpdates++;
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
        ActualThemeChanged += OnGalleryThemeChanged;
        RefreshMaterial();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        loaded = false; callbackGeneration++; refreshQueued = false;
        ActualThemeChanged -= OnGalleryThemeChanged;
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
        if (disposed || !loaded || refreshQueued) return;
        refreshQueued = true; var generation = callbackGeneration;
        try
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (disposed || !loaded || generation != callbackGeneration) return;
                refreshQueued = false;
                try
                {
                    var settings = ReadSettings();
                    if (!ownerMotionAllowed || settings.Animations != true) StopFiniteMotion(restoreStart: true);
                    RefreshMaterial();
                }
                catch (Exception exception) { SetCompositionFailure(exception, "callback.refresh"); }
            })) refreshQueued = false;
        }
        catch (Exception exception) { refreshQueued = false; RecordFailure("callback.enqueue", exception); }
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
        if (loaded && ownerAllowed && advancedAllowed && failure is null && testPolicy == "normal")
        {
            AttachSettings();
            try { capabilities ??= new CompositionCapabilities(); }
            catch (Exception e) { RecordFailure("initialize.compositionCapabilities", e); }
            if (capabilities is not null && !capabilitySubscribed)
                capabilitySubscribed = TryAttach("subscribe.compositionCapabilities", () => capabilities.Changed += OnCapabilitiesChanged);
        }
        var settings = ReadSettings();
        var composition = capabilities;
        var supported = composition is null ? null : Probe("query.compositionCapabilities", composition.AreEffectsSupported);
        var permitted = testPolicy == "normal" && loaded && ownerAllowed && advancedAllowed && failure is null && settings.AdvancedEffects == true &&
            settings.HighContrast == false && supported == true;
        if (!permitted)
        {
            ReleaseMaterial();
            DetachSettings(); DetachCapabilities();
            SetStablePalette(false);
            UpdateState();
            return;
        }
        try
        {
            if (lens is null)
            {
                lens = new RefractionLens(PatternSource, LensHost, failInitialization);
                allocationCycles++;
            }
            lens.Update(StrengthSlider.Value * (interaction == "pressed" ? .6 : interaction == "hover" ? 1.15 : 1));
            SetStablePalette(true);
        }
        catch (Exception e)
        {
            // Public composition resources may fail on device/capability transitions.
            // Stay on solid until the owner recreates this experimental Gallery.
            RecordFailure("composition.createOrUpdate", e);
            ReleaseMaterial();
            DetachSettings(); DetachCapabilities();
            SetStablePalette(false);
        }
        UpdateState();
    }

    private void SetStablePalette(bool transparent)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var background = dark ? Color.FromArgb(transparent ? (byte)212 : (byte)255, 32, 36, 39) : Color.FromArgb(transparent ? (byte)200 : (byte)255, 250, 251, 251);
        var foregroundColor = dark ? Color.FromArgb(255, 243, 244, 245) : Color.FromArgb(255, 27, 32, 35);
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
        if (lens is not null) { updatesReleased += lens.UpdateCount; lens.Dispose(); cleanupFailures += lens.CleanupFailures; if (lens.RemainingResources != 0) { failedRelease = lens; failure = "NativeReleaseFailed"; } lens = null; }
    }

    private void SetCompositionFailure(Exception exception, string phase)
    {
        RecordFailure(phase, exception);
        ReleaseMaterial();
        DetachSettings(); DetachCapabilities();
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
                : $"实时内部背景折射 · 1 个背景表面 + 1 个连续边缘遮罩 · 背景位置 {patternPosition:0} · 仅手动短演示";
            if (MaterialState.Text != state) MaterialState.Text = state;
        }
        catch (Exception exception) { RecordFailure("presentation.state", exception); ReleaseMaterial(); }
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) { if (!initialized) return; LensFrame.Width = Math.Min(320, Math.Max(180, e.NewSize.Width - 32)); ControlActions.Orientation = e.NewSize.Width < 700 ? Orientation.Vertical : Orientation.Horizontal; var narrow = e.NewSize.Width < 500; Grid.SetColumn(LensSliderPanel, narrow ? 0 : 1); Grid.SetColumn(StrengthSliderPanel, narrow ? 0 : 2); Grid.SetRow(LensSliderPanel, narrow ? 1 : 0); Grid.SetRow(StrengthSliderPanel, narrow ? 2 : 0); RefreshMaterial(); }
    private void OnGalleryThemeChanged(FrameworkElement sender, object e) => RefreshMaterial();
    private void SetInteraction(string value) { if (interaction == value) return; interaction = value; RefreshMaterial(); }
    private void OnLensHover(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetInteraction("hover");
    private void OnLensExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetInteraction("normal");
    private void OnLensPress(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetInteraction("pressed");
    private void OnLensRelease(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetInteraction("hover");
    internal void TestCommand(string value)
    {
        switch (value) {
            case "on": AdvancedSwitch.IsOn = true; break;
            case "off": AdvancedSwitch.IsOn = false; break;
            case "fault": SetCompositionFailure(new InvalidOperationException("TestDeviceLost"), "Test.DeviceLost"); break;
            case "replace": ReleaseMaterial(); BuildPatternReplacement(); break;
            case "initFailure": ReleaseMaterial(); failInitialization = true; break;
            case "cancelUpdate": QueueRefresh(); callbackGeneration++; refreshQueued = false; testPolicy = "unknown"; break;
            case "disposeQueued": QueueRefresh(); Dispose(); return;
            case "largeText": StableTitle.FontSize = 27; StableBody.FontSize = 21; StableCaption.FontSize = 18; break;
            case "normalText": StableTitle.FontSize = 18; StableBody.FontSize = 14; StableCaption.FontSize = 12; break;
            case "normal": case "highContrast": case "unknown": testPolicy = value; break;
            case "hover": case "pressed": SetInteraction(value); break;
            default: throw new InvalidDataException("Unknown material Test seam");
        }
        RefreshMaterial();
    }
    private void BuildPatternReplacement() { PatternLayer.Children.Clear(); BuildPattern(); StepPattern(0); }
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
        loaded = false; callbackGeneration++; refreshQueued = false;
        ActualThemeChanged -= OnGalleryThemeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachSettings();
        DetachCapabilities();
        ReleaseMaterial();
        failedRelease?.Dispose();
    }
}
