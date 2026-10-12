namespace TheOrder.Platform.Windows;

// Keep OS-dependent decoration separate from business/data behavior. WinUI and
// .NET are shipped with the application; a new reference SDK does not raise this floor.
public static class WindowsCompatibility
{
    public const int MinimumBuild = 17763;
    public static readonly Version MinimumVersion = new(10, 0, MinimumBuild);

    public static bool IsSupported(Version version) => version >= MinimumVersion;
    public static bool IsWindows11(Version version) => version >= new Version(10, 0, 22000);

    // Do not activate a Win11 material on Win10. On Win11 its controller can still
    // be unavailable (driver, RDP, policy); inability to probe means a solid canvas.
    public static bool CanUseMica(Version version, Func<bool> probe)
    {
        if (!IsWindows11(version)) return false;
        try { return probe(); }
        catch (Exception) { return false; }
    }
}
