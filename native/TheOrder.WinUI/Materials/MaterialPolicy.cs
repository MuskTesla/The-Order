using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.System.Power;
using Windows.UI.ViewManagement;

namespace TheOrder.WinUI.Materials;

// Presentation policy only. Business and image services do not know material APIs.
internal sealed class MaterialPolicy : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly Action changed;
    private readonly UISettings? ui;
    private readonly AccessibilitySettings? accessibility;
    private readonly CompositionCapabilities? capabilities;
    private readonly bool effectsSubscribed;
    private readonly bool animationSubscribed;
    private readonly bool contrastSubscribed;
    private readonly bool capabilitySubscribed;
    private readonly bool powerSubscribed;
    private ResourceDictionary? lastResources;
    private AcrylicBrush[] ownedBrushes = [];
    private bool bindingsResolved;
    private bool? lastAppliedFallback;
    private string? failure;
    private string? failurePhase;
    private string? failureHResult;
    private string? failureKind;
    private bool materialFault;
    private readonly Dictionary<string, NotificationWarning> notificationWarnings = new(StringComparer.Ordinal);
    private int disposed;

    internal bool ForceSolid { get; set; }
    internal bool ReducedMotion { get; set; }
    internal bool EffectsAllowed => ReadState().EffectsAllowed;
    internal bool MotionAllowed => ReadState().MotionAllowed;
    internal bool HighContrast => ReadState().HighContrast == true;
    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    // A failed remote-session query must not opt into expensive materials.
    internal static bool Remote
    {
        get
        {
            try { return GetSystemMetrics(0x1000) != 0; }
            catch (Exception) { return true; }
        }
    }

    internal MaterialPolicy(DispatcherQueue dispatcher, Action changed)
    {
        this.dispatcher = dispatcher;
        this.changed = changed;
        ui = CreateCapability("initialize.uiSettings", () => new UISettings());
        accessibility = CreateCapability("initialize.accessibilitySettings", () => new AccessibilitySettings());
        capabilities = CreateCapability("initialize.compositionCapabilities", () => new CompositionCapabilities());
        if (ui is not null)
        {
            effectsSubscribed = TrySubscribe("subscribe.advancedEffects", () => ui.AdvancedEffectsEnabledChanged += EffectsChanged);
            animationSubscribed = TrySubscribe("subscribe.animations", () => ui.AnimationsEnabledChanged += AnimationsChanged);
        }
        if (accessibility is not null)
            contrastSubscribed = TrySubscribe("subscribe.highContrast", () => accessibility.HighContrastChanged += ContrastChanged);
        if (capabilities is not null)
            capabilitySubscribed = TrySubscribe("subscribe.compositionCapabilities", () => capabilities.Changed += CapabilityChanged);
        powerSubscribed = TrySubscribe("subscribe.energySaver", () => PowerManager.EnergySaverStatusChanged += PowerChanged);
    }

    private T? CreateCapability<T>(string phase, Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception exception) { RecordFailure(phase, exception, "CapabilityUnavailable"); return null; }
    }

    private readonly record struct NotificationWarning(string Phase, string ExceptionType, string HResult);

    private bool TrySubscribe(string phase, Action subscribe)
    {
        try { subscribe(); return true; }
        catch (Exception exception)
        {
            // Notification registration is optional. Existing owner refreshes poll
            // every capability getter; a successful getter still describes real state.
            notificationWarnings[phase] = new(phase, exception.GetType().Name, $"0x{exception.HResult:X8}");
            RecordFailure(phase, exception, "NotificationWarning");
            return false;
        }
    }

    private readonly record struct State(bool EffectsAllowed, bool MotionAllowed,
        bool? HighContrast, bool? Remote, string? EnergySaver, bool? AdvancedEffects,
        bool? CompositionSupported, string? Failure);

    private State ReadState()
    {
        if (IsDisposed) return new(false, false, null, null, null, null, null, "Disposed");
        var settings = ui;
        var contrastSettings = accessibility;
        var composition = capabilities;
        var animations = settings is null ? null : Probe("query.animations", () => settings.AnimationsEnabled);
        var contrast = contrastSettings is null ? null : Probe("query.highContrast", () => contrastSettings.HighContrast);
        var advanced = settings is null ? null : Probe("query.advancedEffects", () => settings.AdvancedEffectsEnabled);
        var supported = composition is null ? null : Probe("query.compositionCapabilities", composition.AreEffectsSupported);
        var energy = Probe("query.energySaver", () => PowerManager.EnergySaverStatus);
        var remote = Probe("query.remoteSession", () => GetSystemMetrics(0x1000) != 0);
        var energyAllowed = energy is EnergySaverStatus.Off or EnergySaverStatus.Disabled;
        var effects = !materialFault && ownedBrushes.Length == 4 && !ForceSolid && contrast == false &&
            advanced == true && supported == true && energyAllowed && remote == false;
        // A material/device/power failure must not manufacture a Reduce Motion state.
        var motion = !ReducedMotion && animations == true;
        return new(effects, motion, contrast, remote, energy?.ToString(), advanced, supported, failure);
    }

    private T? Probe<T>(string phase, Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception exception) { RecordFailure(phase, exception, "ProbeFailed"); return null; }
    }

    private void RecordFailure(string phase, Exception exception, string kind = "MaterialFailure")
    {
        failure = exception.GetType().Name;
        failurePhase = phase;
        failureHResult = $"0x{exception.HResult:X8}";
        failureKind = kind;
    }

    private void EffectsChanged(UISettings sender, object args) => Dispatch();
    private void AnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => Dispatch();
    private void ContrastChanged(AccessibilitySettings sender, object args) => Dispatch();
    private void CapabilityChanged(CompositionCapabilities sender, object args) => Dispatch();
    private void PowerChanged(object? sender, object args) => Dispatch();

    private void Dispatch()
    {
        if (IsDisposed) return;
        try
        {
            dispatcher.TryEnqueue(() =>
            {
                if (IsDisposed) return;
                try { changed(); }
                catch (Exception exception)
                {
                    RecordFailure("callback.changed", exception);
                    materialFault = true;
                    if (lastResources is not null) Apply(lastResources);
                }
            });
        }
        catch (Exception exception) { RecordFailure("callback.enqueue", exception); }
    }

    internal void Apply(ResourceDictionary resources)
    {
        if (IsDisposed) return;
        lastResources = resources;
        ResolveOwnedBrushesOnce(resources);
        var fallback = !ReadState().EffectsAllowed;
        if (lastAppliedFallback == fallback) return;
        var failed = false;
        foreach (var brush in ownedBrushes)
        {
            try { brush.AlwaysUseFallback = fallback; }
            catch (Exception exception) { RecordFailure("apply.ownedAcrylicBrush", exception); materialFault = true; failed = true; }
        }
        lastAppliedFallback = failed ? null : fallback;
        if (!failed || fallback) return;
        // A partial apply must not leave the successfully updated brushes advanced.
        foreach (var brush in ownedBrushes)
        {
            try { brush.AlwaysUseFallback = true; }
            catch (Exception exception) { RecordFailure("apply.solidFallback", exception); materialFault = true; }
        }
    }

    private void ResolveOwnedBrushesOnce(ResourceDictionary resources)
    {
        if (bindingsResolved) return;
        bindingsResolved = true;
        var phase = "bindings.designTokensSource";
        var brushes = new List<AcrylicBrush>(4);
        try
        {
            // Inspect dictionary Source only. Enumerating XamlControlsResources or
            // framework ThemeDictionaries would materialize lazy system resources.
            ResourceDictionary? tokens = IsDesignTokens(resources.Source) ? resources : null;
            if (tokens is null)
                foreach (var merged in resources.MergedDictionaries)
                    if (IsDesignTokens(merged.Source)) { tokens = merged; break; }
            if (tokens is null) throw new InvalidOperationException("OwnedDesignTokensDictionaryMissing");
            foreach (var themeName in new[] { "Light", "Dark" })
            {
                phase = $"bindings.{themeName}";
                if (!tokens.ThemeDictionaries.TryGetValue(themeName, out var value) || value is not ResourceDictionary theme)
                    throw new InvalidOperationException("OwnedMaterialThemeMissing");
                foreach (var key in new[] { "NavigationSurfaceBrush", "ToolbarBrush" })
                {
                    phase = $"bindings.{themeName}.{key}";
                    if (!theme.TryGetValue(key, out var resource) || resource is not AcrylicBrush brush)
                        throw new InvalidOperationException("OwnedAcrylicBrushMissing");
                    if (!brushes.Any(existing => ReferenceEquals(existing, brush))) brushes.Add(brush);
                }
            }
        }
        catch (Exception exception) { RecordFailure(phase, exception); }
        finally { ownedBrushes = brushes.ToArray(); }
    }

    private static bool IsDesignTokens(Uri? source)
    {
        if (source is null) return false;
        var path = source.OriginalString.Replace('\\', '/');
        return path.Equals("DesignTokens.xaml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/DesignTokens.xaml", StringComparison.OrdinalIgnoreCase);
    }

    internal object Evidence()
    {
        var state = ReadState();
        return new
        {
            effectsAllowed = state.EffectsAllowed,
            motionAllowed = state.MotionAllowed,
            highContrast = state.HighContrast,
            highContrastSource = "Windows AccessibilitySettings",
            forcedSolid = ForceSolid,
            reducedMotion = ReducedMotion,
            remote = state.Remote,
            energySaver = state.EnergySaver,
            advancedEffects = state.AdvancedEffects,
            compositionSupported = state.CompositionSupported,
            failure = state.Failure,
            failurePhase,
            failureHResult,
            failureKind,
            notificationWarnings = notificationWarnings.Values.ToArray(),
            notificationRoute = "Available events and existing owner refresh poll; no new timer",
            currentRequiredStateKnown = state.HighContrast.HasValue && state.Remote.HasValue &&
                state.EnergySaver is not null && state.AdvancedEffects.HasValue && state.CompositionSupported.HasValue,
            materialFault,
            ownedBrushCount = ownedBrushes.Length,
            bindingsResolved,
            level = state.EffectsAllowed ? 2 : 1
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        TryDetach(() => { if (effectsSubscribed && ui is not null) ui.AdvancedEffectsEnabledChanged -= EffectsChanged; });
        TryDetach(() => { if (animationSubscribed && ui is not null) ui.AnimationsEnabledChanged -= AnimationsChanged; });
        TryDetach(() => { if (contrastSubscribed && accessibility is not null) accessibility.HighContrastChanged -= ContrastChanged; });
        TryDetach(() => { if (capabilitySubscribed && capabilities is not null) capabilities.Changed -= CapabilityChanged; });
        TryDetach(() => { if (powerSubscribed) PowerManager.EnergySaverStatusChanged -= PowerChanged; });
        lastResources = null;
        ownedBrushes = [];
    }

    private static void TryDetach(Action detach)
    {
        try { detach(); }
        catch (Exception) { }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
