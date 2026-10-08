using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TheOrder.Application;
using TheOrder.Presentation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TheOrder.WinUI;

// Revision 2 shell: Fluent TitleBar (back, pane, search, session indicator), Mica, keyboard model,
// session-scoped material mode, library quick launch and live Windows accent updates.
public sealed partial class MainWindow
{
    private Views.MaterialMode materialMode = Views.MaterialMode.Auto;
    private MicaBackdrop? mica;
    private UISettings? colorSettings;
    private long accentEvents;
    private string accentSource = "Windows theme resources (startup)";
    private bool diagnosticsToggle;

    private void InitializeShellInput(bool diagnostics)
    {
        diagnosticsToggle = diagnostics;
        void Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => { args.Handled = true; action(); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Accelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => TitleSearch.Focus(FocusState.Keyboard));
        Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _ = GoBackAsync());
        Accelerator(VirtualKey.GoBack, VirtualKeyModifiers.None, () => _ = GoBackAsync());
        if (diagnostics) Accelerator(VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => DiagnosticsPanel.Visibility = DiagnosticsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        // Mouse back button (XButton1) goes back like Alt+Left.
        Root.PointerPressed += (_, args) => { if (args.GetCurrentPoint(Root).Properties.IsXButton1Pressed) { args.Handled = true; _ = GoBackAsync(); } };
        ViewModel.PropertyChanged += OnShellState;
        try { colorSettings = new UISettings(); colorSettings.ColorValuesChanged += OnSystemColorsChanged; }
        catch (Exception) { colorSettings = null; accentSource = "UISettings unavailable; startup theme resources"; }
    }

    private void DisposeShellInput()
    {
        ViewModel.PropertyChanged -= OnShellState;
        if (colorSettings is not null) { colorSettings.ColorValuesChanged -= OnSystemColorsChanged; colorSettings = null; }
    }

    private void OnShellState(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Query)) SynchronizeTitleSearch();
    }

    // Search lives in the title bar and always drives the Library query; typing elsewhere returns to the Library.
    private async void OnTitleSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        try
        {
            if (ViewModel.Page != WorkspacePage.Library && !galleryVisible) await Navigate(WorkspacePage.Library);
            if (ViewModel.Query.Text != sender.Text) await ViewModel.SetQueryAsync(ViewModel.Query with { Text = sender.Text });
        }
        catch (Exception) { ViewModel.ReportUnexpected(); }
    }
    private void OnTitleSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => libraryPage.FocusResults();
    private void SynchronizeTitleSearch()
    {
        if (TitleSearch is not null && TitleSearch.Text != ViewModel.Query.Text) TitleSearch.Text = ViewModel.Query.Text;
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
        try
        {
            LaunchNotice.IsOpen = false;
            await Launch.OpenAsync(game.Id);
            if (Launch.GameId != game.Id) return;
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

    // 设置 › 材质效果. Session-scoped: the persisted preference schema rejects unknown members in older builds.
    internal Views.MaterialMode MaterialModeSetting => materialMode;
    internal void SetMaterialMode(Views.MaterialMode mode)
    {
        if (materialMode == mode) return;
        materialMode = mode;
        ApplyMaterialPolicy();
    }

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
        if (changed) RefreshTestThemeResources();
    }
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
            titleBar = AppTitleBar.ActualHeight, backEnabled = AppTitleBar.IsBackButtonEnabled, search = TitleSearch.Text.Length, searchFocus = TitleSearch.FocusState.ToString(),
            paneMode = Navigation.DisplayMode.ToString(), paneOpen = Navigation.IsPaneOpen, sessionIndicator = SessionSurface.Visibility == Visibility.Visible,
            exitButtonPresent = false, accentEvents, accentSource, accentRgb = accent is { } a ? $"{a.R:X2}{a.G:X2}{a.B:X2}" : "framework",
            launchNotice = LaunchNotice.IsOpen ? LaunchNotice.Severity.ToString() : null, diagnosticsToggle
        };
    }
}
