using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;
using System.ComponentModel;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    private bool synchronizing;
    public SettingsPage(WorkspaceViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent();
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnState; Synchronize(); };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnState;
    }
    private void OnState(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(ViewModel.Theme) or nameof(ViewModel.Layout)) Synchronize(); }
    private void Synchronize() { synchronizing = true; ThemeChoice.SelectedIndex = (int)ViewModel.Theme; LayoutChoice.SelectedIndex = ViewModel.Layout == "grid" ? 0 : 1; synchronizing = false; }
    private async void OnAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || ThemeChoice.SelectedIndex < 0 || LayoutChoice.SelectedIndex < 0) return;
        await ViewModel.ChangeAppearanceAsync((NativeTheme)ThemeChoice.SelectedIndex, LayoutChoice.SelectedIndex == 0 ? "grid" : "list");
    }
}
