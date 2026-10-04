using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    private readonly INativeSettings settings;
    public string DataDirectory { get; }
    public event Action<NativeTheme>? ThemeChanged;
    public SettingsPage(INativeSettings settings, string directory)
    {
        this.settings = settings; DataDirectory = directory;
        InitializeComponent(); ThemeChoice.SelectedIndex = (int)settings.Theme;
    }
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeChoice.SelectedIndex is < 0 or > 2) return;
        settings.Theme = (NativeTheme)ThemeChoice.SelectedIndex;
        ThemeChanged?.Invoke(settings.Theme);
    }
}
