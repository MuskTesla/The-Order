using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.WinUI.Presentation;
using TheOrder.WinUI.Views;

namespace TheOrder.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly LibraryPage libraryPage;
    private readonly SettingsPage settingsPage;
    public ErrorState Errors { get; }
    public MainWindow(WorkspaceViewModel workspace, ErrorState errors, INativeSettings settings)
    {
        Errors = errors;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        libraryPage = new(workspace, lifetime.Token);
        settingsPage = new(settings, workspace.DataDirectory);
        settingsPage.ThemeChanged += ApplyTheme;
        ApplyTheme(settings.Theme);
        Navigation.SelectedItem = LibraryNavigation;
    }
    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
            PageContent.Content = item.Tag as string == "settings" ? settingsPage : libraryPage;
    }
    private void ApplyTheme(NativeTheme theme) => Root.RequestedTheme = theme switch
    {
        NativeTheme.Light => ElementTheme.Light, NativeTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default
    };
    public void CancelOperations() { lifetime.Cancel(); lifetime.Dispose(); }
}
