using System.Runtime.InteropServices;

namespace TheOrder.Platform.Windows;

// Explorer does not theme unpackaged .lnk resources. Update only our existing,
// exact-target shortcuts; preserve their arguments and every other property.
public static class WindowsShortcutTheme
{
    public static void Synchronize(string directory, bool light)
    {
        var target = Path.Combine(directory, "TheOrder.exe");
        var icon = Path.Combine(directory, "Assets", light ? "Brand.Light.ico" : "Brand.Dark.ico");
        if (!File.Exists(target) || !File.Exists(icon)) return;
        foreach (var path in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "The Order.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "The Order", "The Order.lnk") })
            try { UpdateShortcut(path, target, icon); } catch { /* A read-only or customized shortcut is left alone. */ }
    }
    internal static bool UpdateShortcut(string path, string expectedTarget, string icon)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            link = ((dynamic)shell!).CreateShortcut(path);
            if (!string.Equals(Path.GetFullPath((string)((dynamic)link).TargetPath), Path.GetFullPath(expectedTarget), StringComparison.OrdinalIgnoreCase)) return false;
            var location = icon + ",0";
            if (string.Equals(((string)((dynamic)link).IconLocation).Replace(", ", ","), location, StringComparison.OrdinalIgnoreCase)) return false;
            ((dynamic)link).IconLocation = location;
            ((dynamic)link).Save();
            SHChangeNotify(0x2000, 0x0005, path, IntPtr.Zero); // SHCNE_UPDATEITEM, SHCNF_PATHW
            return true;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int change, uint flags, string item, IntPtr other);
}
