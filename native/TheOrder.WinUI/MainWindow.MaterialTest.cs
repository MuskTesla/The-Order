using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TheOrder.WinUI;

public sealed partial class MainWindow
{
    private readonly Materials.MaterialTreeProbe bitmapTreeProbe = new(), staleTreeProbe = new();
    private string testAccent = "system";
    private string systemAccentAtStart = "UNAVAILABLE";
    private void ReadSystemAccentOnce()
    {
        try { var color = new UISettings().GetColorValue(UIColorType.Accent); systemAccentAtStart = $"{color.R:X2}{color.G:X2}{color.B:X2}"; }
        catch (Exception) { }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private bool MaterialWindowActive => GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);
    private readonly string[] accentTestKeys = ["AccentBrush", "SelectionBrush", "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed", "AccentButtonForeground", "NavigationViewSelectionIndicatorForeground", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed"];
    private void SetTestAccent(string value)
    {
        if (visualFixtureTimer is null) throw new InvalidOperationException("Test capability required");
        var palette = new Dictionary<string, Color> {
            ["blue"] = Color.FromArgb(255, 0, 90, 200), ["green"] = Color.FromArgb(255, 0, 112, 80),
            ["red"] = Color.FromArgb(255, 184, 28, 45), ["purple"] = Color.FromArgb(255, 117, 50, 170),
            ["pink"] = Color.FromArgb(255, 195, 0, 108), ["pale"] = Color.FromArgb(255, 229, 215, 105), ["deep"] = Color.FromArgb(255, 39, 17, 63) };
        if (value != "system" && !palette.ContainsKey(value)) throw new InvalidDataException("Unknown Test accent");
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        foreach (var key in accentTestKeys) resources.Remove(key);
        testAccent = value;
        Navigation.Resources.Remove("NavigationViewSelectionIndicatorForeground");
        if (value == "system") { RefreshTestThemeResources(); return; }
        if (!palette.TryGetValue(value, out var color)) throw new InvalidDataException("Unknown Test accent");
        // Explicit app-only simulated input. Production uses WinUI theme resources.
        var foreground = RelativeLuminance(color) > .18 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        var semantic = AccessibleTestAccent(color, Root.ActualTheme == ElementTheme.Dark);
        Navigation.Resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(semantic);
        foreach (var key in accentTestKeys) resources[key] = new SolidColorBrush(key is "AccentBrush" or "NavigationViewSelectionIndicatorForeground" ? semantic : key.Contains("Foreground") ? foreground : color) { Opacity = key == "SelectionBrush" ? .14 : 1 };
        RefreshTestThemeResources();
    }
    private void RefreshTestThemeResources()
    {
        var current = Root.RequestedTheme;
        Root.RequestedTheme = current == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        Root.RequestedTheme = current;
    }
    private static double RelativeLuminance(Color c)
    {
        double Channel(byte b) { double v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }
    private static Color AccessibleTestAccent(Color color, bool dark)
    {
        var background = dark ? Color.FromArgb(255, 36, 45, 46) : Color.FromArgb(255, 250, 251, 248);
        var original = color;
        for (var i = 0; i <= 20; i++)
        {
            var a = RelativeLuminance(color); var b = RelativeLuminance(background);
            if ((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5) return color;
            var t = (i + 1) / 20d; var target = dark ? 255 : 0;
            color = Color.FromArgb(255, (byte)Math.Round(original.R + (target - original.R) * t), (byte)Math.Round(original.G + (target - original.G) * t), (byte)Math.Round(original.B + (target - original.B) * t));
        }
        return color;
    }
    private object TestAccentEvidence() => new { source = "Windows UISettings at Test startup + dynamic WinUI ThemeResource", simulation = testAccent, systemRgbAtStart = systemAccentAtStart, polling = false };
    private static object MaterialManagedEvidence()
    {
        var gc = GC.GetGCMemoryInfo();
        return new { committedBytes = gc.TotalCommittedBytes, heapBytes = gc.HeapSizeBytes, totalAllocatedBytes = GC.GetTotalAllocatedBytes(false), collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) } };
    }
    private static int CountMaterialBitmaps(DependencyObject root)
    {
        var count = root is Microsoft.UI.Xaml.Controls.Image { Source: not null } ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) count += CountMaterialBitmaps(VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private async Task MaterialReplayAsync(JsonElement value)
    {
        if (visualFixtureTimer is null || value.ValueKind != JsonValueKind.Number) throw new InvalidDataException();
        var requestedStep = value.GetInt32(); if (requestedStep < 0 || requestedStep > 100000) throw new InvalidDataException(); var step = requestedStep % 48;
        // Profile is a frozen 384-item Test subset; no other content is admitted.
        if (step % 16 == 0) {
            await Navigate(TheOrder.Presentation.WorkspacePage.Library);
            await ViewModel.ChangeAppearanceAsync(ViewModel.Theme, step % 32 == 0 ? "grid" : "list");
            await libraryPage.PerformanceScrollAsync((step % 48) / 47d, (step / 16 * 128) % Math.Max(1, ViewModel.Items.Count));
        }
        else if (step % 16 == 1 && ViewModel.Items.Count > 0) await Navigate(TheOrder.Presentation.WorkspacePage.Details, ViewModel.Items[(step / 16 * 128 + 1) % ViewModel.Items.Count].Id);
        else { ShowGallery(); gallery?.StepPattern(step % 2 == 0 ? 70 : 0); if (step == 44) { SetVisualWindowVisible(false); AppWindow.Hide(); await Task.Delay(200); AppWindow.Show(); Activate(); SetVisualWindowVisible(true); } }
    }
}
