using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    private bool synchronizing;
    public SettingsPage(WorkspaceViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent();
        Loaded += (_, _) => { synchronizing = true; ThemeChoice.SelectedIndex = (int)ViewModel.Theme; LayoutChoice.SelectedIndex = ViewModel.Layout == "grid" ? 0 : 1; synchronizing = false; };
    }
    private async void OnAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || ThemeChoice.SelectedIndex < 0 || LayoutChoice.SelectedIndex < 0) return;
        await ViewModel.ChangeAppearanceAsync((NativeTheme)ThemeChoice.SelectedIndex, LayoutChoice.SelectedIndex == 0 ? "grid" : "list");
    }
}
