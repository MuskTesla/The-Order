using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using TheOrder.Application;
using TheOrder.Presentation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TheOrder.WinUI;

// Revision 2 shell: Fluent TitleBar (back, pane, search, session indicator), Mica, keyboard model,
// persisted material intent, library quick launch and live Windows accent updates.
public sealed partial class MainWindow
{
    // Advanced materials remain opt-in for the development integration candidate.
    private Views.MaterialMode materialMode = Views.MaterialMode.Standard;
    private readonly IMaterialPreferencesStore materialPreferences;
    private readonly bool controlledAdvanced;
    private Task materialWrite = Task.CompletedTask;
    private bool materialReadOnly;
    private bool materialInitialized;
    private string? materialPreferenceNotice;
    private MicaBackdrop? mica;
    private UISettings? colorSettings;
    private long accentEvents;
    private string accentSource = "Windows theme resources (startup)";
    private bool diagnosticsToggle;
    private int titleSearchEditsPending;
    private long titleSearchEditVersion;
    private Task? titleSearchNavigation;

    private void InitializeShellInput(bool diagnostics)
    {
        diagnosticsToggle = diagnostics;
        void Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => { args.Handled = true; if (!commandsBlocked) action(); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => TitleSearch.Focus(FocusState.Keyboard));
        Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _ = GoBackAsync());
        Accelerator(VirtualKey.GoBack, VirtualKeyModifiers.None, () => _ = GoBackAsync());
        if (diagnostics) Accelerator(VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => DiagnosticsPanel.Visibility = DiagnosticsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        // Mouse back button (XButton1) goes back like Alt+Left.
        Root.PointerPressed += (_, args) => { if (!commandsBlocked && args.GetCurrentPoint(Root).Properties.IsXButton1Pressed) { args.Handled = true; _ = GoBackAsync(); } };
        ViewModel.PropertyChanged += OnShellState;
        Root.ActualThemeChanged += OnCaptionThemeChanged;
        RefreshCaptionTheme();
        try { colorSettings = new UISettings(); colorSettings.ColorValuesChanged += OnSystemColorsChanged; }
        catch (Exception) { colorSettings = null; accentSource = "UISettings unavailable; startup theme resources"; }
    }

    private void DisposeShellInput()
    {
        ViewModel.PropertyChanged -= OnShellState;
        Root.ActualThemeChanged -= OnCaptionThemeChanged;
        if (colorSettings is not null) { colorSettings.ColorValuesChanged -= OnSystemColorsChanged; colorSettings = null; }
    }

    private void OnCaptionThemeChanged(FrameworkElement sender, object args) => RefreshCaptionTheme();
    private void RefreshCaptionTheme()
    {
        if (visualStopped) return;
        // The OS owns caption glyphs. Legacy can retain a light caption after
        // leaving a contrast theme, even while XAML has returned to Dark.
        // Let contrast themes use the system palette, then explicitly restore
        // the live XAML theme instead of a stale OS/RequestedTheme default.
        AppWindow.TitleBar.PreferredTheme = materialPolicy?.HighContrast == true
            ? TitleBarTheme.Legacy
            : Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    private void OnShellState(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Query)) SynchronizeTitleSearch();
    }

    // Search lives in the title bar and always drives the Library query; typing elsewhere returns to the Library.
    private async void OnTitleSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (commandsBlocked || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text;
        var version = ++titleSearchEditVersion;
        var returnToSearch = ViewModel.Page != WorkspacePage.Library;
        titleSearchEditsPending++;
        try
        {
            if (ViewModel.Page != WorkspacePage.Library && !galleryVisible)
            {
                // All edits share the navigation (including its unsaved prompt).
                // ShowPage must not replace a live edit with the previous query.
                var navigation = titleSearchNavigation ??= Navigate(WorkspacePage.Library);
                await navigation;
                if (ReferenceEquals(titleSearchNavigation, navigation)) titleSearchNavigation = null;
            }
            if (version != titleSearchEditVersion) return;
            if (commandsBlocked || ViewModel.Page != WorkspacePage.Library || galleryVisible) { SynchronizeTitleSearch(); return; }
            if (ViewModel.Query.Text != text) await ViewModel.SetQueryAsync(ViewModel.Query with { Text = text });
            // Restore after query/layout completion; earlier focus is lost when
            // the result containers change. Never override a later edit or a
            // user moving focus to another control (including result submission).
            if (returnToSearch) DispatcherQueue.TryEnqueue(() =>
            {
                if (version == titleSearchEditVersion && !commandsBlocked && !visualStopped &&
                    ViewModel.Page == WorkspacePage.Library && Root.XamlRoot is not null &&
                    ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), AppTitleBar))
                    sender.Focus(FocusState.Keyboard);
            });
        }
        catch (Exception) { ViewModel.ReportUnexpected(); }
        finally { if (--titleSearchEditsPending == 0) SynchronizeTitleSearch(); }
    }
    private void OnTitleSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    { if (!commandsBlocked && ViewModel.Page == WorkspacePage.Library && !galleryVisible) libraryPage.FocusResults(); }
    private void SynchronizeTitleSearch()
    {
        if (titleSearchEditsPending == 0 && TitleSearch is not null && TitleSearch.Text != ViewModel.Query.Text) TitleSearch.Text = ViewModel.Query.Text;
    }

    private async void OnSessionDetails(object sender, RoutedEventArgs e)
    {
        SessionSurface.Flyout?.Hide();
        if (Sessions.Session?.Session?.Native?.GameId is { } id) await Navigate(WorkspacePage.Details, id);
    }

    // Library context menu / Ctrl+Enter. Uses the same LaunchViewModel readiness as Details: only a
    // prepared Ready installation launches; anything needing review or a choice opens Details instead.
    private async Task QuickLaunchAsync(GameSummary game)
    {
        if (commandsBlocked || navigating) return;
        try
        {
            LaunchNotice.IsOpen = false;
            await Launch.OpenAsync(game.Id);
            if (commandsBlocked || navigating || Launch.GameId != game.Id) return;
            if (!Launch.CanLaunch)
            {
                await Navigate(WorkspacePage.Details, game.Id);
                return;
            }
            await Launch.LaunchAsync();
            LaunchNotice.Title = game.Name;
            LaunchNotice.Message = Launch.Message;
            LaunchNotice.Severity = Launch.LastRequestOutcome switch
            {
                LaunchOutcome.Started => InfoBarSeverity.Success,
                LaunchOutcome.StartedButTrackingLimited or LaunchOutcome.AlreadyRunning or LaunchOutcome.AlreadyLaunching or LaunchOutcome.Cancelled => InfoBarSeverity.Warning,
                null => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Error
            };
            LaunchNotice.IsOpen = true;
        }
        catch (Exception) { ViewModel.ReportUnexpected(); }
    }

    // Separate optional companion leaves the strict native-ui v1 schema unchanged.
    internal Views.MaterialMode MaterialModeSetting => materialMode;
    private async Task LoadMaterialPreferencesAsync()
    {
        try
        {
            var read = await materialPreferences.LoadAsync();
            materialMode = Enum.Parse<Views.MaterialMode>(read.Preference.Mode);
            materialReadOnly = read.ReadOnly; materialPreferenceNotice = read.Warning;
        }
        catch (Exception) { materialMode = Views.MaterialMode.Standard; materialReadOnly = true; materialPreferenceNotice = "材质设置未能读取，已使用标准材质；原文件保留。"; }
        materialInitialized = true;
        ApplyMaterialPolicy();
    }
    internal Task SetMaterialModeAsync(Views.MaterialMode mode)
    {
        if (commandsBlocked || !materialInitialized || materialReadOnly || !materialWrite.IsCompleted) return materialWrite;
        return materialWrite = SaveMaterialModeAsync(mode);
    }
    private async Task SaveMaterialModeAsync(Views.MaterialMode mode)
    {
        settingsPage.SetMaterialSaving(true);
        try
        {
            await materialPreferences.SaveAsync(new(mode.ToString()));
            if (visualStopped) return;
            materialMode = mode; materialPreferenceNotice = null;
        }
        catch (Exception) { materialPreferenceNotice = "材质设置未保存，已保留上次选项；请重试。"; }
        finally { settingsPage.SetMaterialSaving(false); if (!visualStopped) ApplyMaterialPolicy(); }
    }
    private Views.MaterialMode EffectiveMaterialMode => Enum.Parse<Views.MaterialMode>(MaterialActivationPolicy.Effective(
        materialMode.ToString(), Program.Profile?.Manifest.ProfileKind ?? NativeProfileKind.Development, controlledAdvanced));

    private void ApplyBackdrop(bool translucent)
    {
        if (translucent)
        {
            if (SystemBackdrop is not MicaBackdrop) SystemBackdrop = mica ??= new MicaBackdrop();
            OpaqueCanvas.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (SystemBackdrop is not null) SystemBackdrop = null;
            OpaqueCanvas.Visibility = Visibility.Visible;
        }
    }

    // Live accent: WinUI resolves SystemAccentColor* once per process. On a Windows personalization change
    // the same names are refreshed at application scope and theme resources are re-resolved.
    private void OnSystemColorsChanged(UISettings sender, object args)
    {
        Color[] palette;
        try { palette = [sender.GetColorValue(UIColorType.Accent), sender.GetColorValue(UIColorType.AccentLight1), sender.GetColorValue(UIColorType.AccentLight2), sender.GetColorValue(UIColorType.AccentLight3),
            sender.GetColorValue(UIColorType.AccentDark1), sender.GetColorValue(UIColorType.AccentDark2), sender.GetColorValue(UIColorType.AccentDark3)]; }
        catch (Exception) { return; }
        DispatcherQueue.TryEnqueue(() => ApplyAccentPalette(palette, "Windows UISettings.ColorValuesChanged"));
    }
    private static readonly string[] accentKeys = ["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];
    private void ApplyAccentPalette(Color[] palette, string source)
    {
        if (visualStopped) return;
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        var changed = false;
        for (var i = 0; i < accentKeys.Length; i++)
        {
            if (resources.TryGetValue(accentKeys[i], out var current) && current is Color existing && existing == palette[i]) continue;
            resources[accentKeys[i]] = palette[i]; changed = true;
        }
        accentEvents++; accentSource = source;
        if (changed) UpdateAccentBrushes(palette);
    }
    // Fluent accent brushes are shared instances (control styles alias them via StaticResource), so updating their
    // colours in the Light and Dark dictionaries reaches every control. Shades follow the Fluent theme mapping.
    private static readonly (string Key, int Light, int Dark)[] accentBrushes =
    [
        ("AccentFillColorDefaultBrush", 4, 2), ("AccentFillColorSecondaryBrush", 4, 2), ("AccentFillColorTertiaryBrush", 4, 2),
        ("AccentTextFillColorPrimaryBrush", 5, 3), ("AccentTextFillColorSecondaryBrush", 6, 3), ("AccentTextFillColorTertiaryBrush", 4, 2),
        ("AccentFillColorSelectedTextBackgroundBrush", 0, 0),
        ("AccentBrush", 5, 3), ("AccentFillBrush", 4, 2), ("SelectionBrush", 4, 2)
    ];
    private void UpdateAccentBrushes(Color[] palette)
    {
        var updated = 0;
        foreach (var dictionary in Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries)
            // WinUI keys its dark theme dictionary "Default"; app dictionaries use "Dark".
            foreach (var theme in new[] { "Light", "Dark", "Default" })
            {
                if (!dictionary.ThemeDictionaries.TryGetValue(theme, out var value) || value is not ResourceDictionary themed) continue;
                foreach (var (key, light, dark) in accentBrushes)
                    if (themed.TryGetValue(key, out var brush) && brush is SolidColorBrush solid) { solid.Color = palette[theme == "Light" ? light : dark]; updated++; }
            }
        accentBrushUpdates = updated;
    }
    private int accentBrushUpdates;
    // Test-only: drives the same live-update path with a labelled simulated palette (no personalization change).
    private void SimulateSystemAccentEvent(string value)
    {
        if (visualFixtureTimer is null) throw new InvalidOperationException("Test capability required");
        if (value == "system")
        {
            if (colorSettings is not null) OnSystemColorsChanged(colorSettings, new object());
            return;
        }
        Color baseColor = value switch
        {
            "green" => Color.FromArgb(255, 16, 124, 16), "purple" => Color.FromArgb(255, 136, 23, 152), "orange" => Color.FromArgb(255, 202, 80, 16),
            "red" => Color.FromArgb(255, 196, 43, 28), _ => throw new InvalidDataException("Unknown Test accent")
        };
        Color Mix(double t, byte target) => Color.FromArgb(255, (byte)Math.Round(baseColor.R + (target - baseColor.R) * t), (byte)Math.Round(baseColor.G + (target - baseColor.G) * t), (byte)Math.Round(baseColor.B + (target - baseColor.B) * t));
        ApplyAccentPalette([baseColor, Mix(.25, 255), Mix(.5, 255), Mix(.75, 255), Mix(.25, 0), Mix(.5, 0), Mix(.75, 0)], "Test simulation · " + value);
    }

    private object ShellEvidence()
    {
        Color? accent = null;
        try { if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) accent = color; } catch (Exception) { }
        return new
        {
            selectedId = ViewModel.SelectedId, queryText = ViewModel.Query.Text, sort = ViewModel.Query.Sort, layout = ViewModel.Layout,
            titleBar = AppTitleBar.ActualHeight, captionTheme = AppWindow.TitleBar.PreferredTheme.ToString(), backEnabled = AppTitleBar.IsBackButtonEnabled, search = TitleSearch.Text.Length, searchFocus = TitleSearch.FocusState.ToString(),
            paneMode = Navigation.DisplayMode.ToString(), paneOpen = Navigation.IsPaneOpen, sessionIndicator = SessionSurface.Visibility == Visibility.Visible,
            exitButtonPresent = false, accentEvents, accentSource, accentBrushUpdates, accentRgb = accent is { } a ? $"{a.R:X2}{a.G:X2}{a.B:X2}" : "framework",
            launchNotice = LaunchNotice.IsOpen ? LaunchNotice.Severity.ToString() : null, diagnosticsToggle
        };
    }
}
