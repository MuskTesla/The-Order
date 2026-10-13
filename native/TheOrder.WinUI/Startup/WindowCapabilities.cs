using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using TheOrder.Application;
using TheOrder.Platform.Windows;

namespace TheOrder.WinUI.Startup;

internal sealed record WindowCapabilities(Version HostVersion, bool CustomTitleBar, bool Mica,
    bool CaptionTheme, string MonospaceFont, string Scenario)
{
    internal static WindowCapabilities Detect()
    {
        var version = Environment.OSVersion.Version;
        bool custom;
        try { custom = AppWindowTitleBar.IsCustomizationSupported(); }
        catch (Exception) { custom = false; }
        var win11 = WindowsCompatibility.IsWindows11(version);
        var mica = WindowsCompatibility.CanUseMica(version, MicaController.IsSupported);
        var scenario = "host";
        // Only an explicitly isolated Test fixture can force missing capabilities.
        // HostVersion always remains the actual host; this is not Win10 acceptance.
        if (Program.Profile?.Manifest.ProfileKind == NativeProfileKind.Test &&
            Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_VISUAL_TEST") == "1")
            scenario = Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_COMPATIBILITY_TEST") switch
            {
                "win10" => "win10-fallback-simulation",
                "native-caption" => "native-caption-simulation",
                "mica-fault" => "mica-attachment-failure-simulation",
                _ => "host"
            };
        return new(version, custom && scenario != "native-caption-simulation",
            mica && scenario != "win10-fallback-simulation", win11 && scenario == "host",
            win11 && scenario == "host" ? "Cascadia Mono" : "Consolas", scenario);
    }
}
