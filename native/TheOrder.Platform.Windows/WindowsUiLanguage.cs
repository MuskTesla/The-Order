using System.Globalization;
using System.Runtime.InteropServices;
namespace TheOrder.Platform.Windows;
public static class WindowsUiLanguage
{
    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();
    public static string Current()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name; }
        catch (Exception) { return CultureInfo.CurrentUICulture.Name; }
    }
}
