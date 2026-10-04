using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;
using System.ComponentModel;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    public OnlineViewModel Online { get; }
    private bool synchronizing;
    public SettingsPage(WorkspaceViewModel viewModel, OnlineViewModel online)
    {
        ViewModel = viewModel; Online = online; InitializeComponent();
        Loaded += async (_, _) => await Online.CheckCredentialAsync();
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnState; Synchronize(); };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnState;
    }
    private async void OnSaveCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var secret = ApiCredential.Password;
        if (await Online.SaveCredentialAsync(secret) && ApiCredential.Password == secret) ApiCredential.Password = "";
    }
    private async void OnClearCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { ApiCredential.Password = ""; await Online.ClearCredentialAsync(); }
    private async void OnTestConnection(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Online.TestConnectionAsync();
    private void OnState(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(ViewModel.Theme) or nameof(ViewModel.Layout)) Synchronize(); }
    private void Synchronize() { synchronizing = true; ThemeChoice.SelectedIndex = (int)ViewModel.Theme; LayoutChoice.SelectedIndex = ViewModel.Layout == "grid" ? 0 : 1; synchronizing = false; }
    private async void OnAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || ThemeChoice.SelectedIndex < 0 || LayoutChoice.SelectedIndex < 0) return;
        await ViewModel.ChangeAppearanceAsync((NativeTheme)ThemeChoice.SelectedIndex, LayoutChoice.SelectedIndex == 0 ? "grid" : "list");
    }
}
