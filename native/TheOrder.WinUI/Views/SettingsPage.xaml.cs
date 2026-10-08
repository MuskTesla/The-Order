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
    public Func<MaterialMode, Task>? ChangeMaterialMode { get; set; }
    // MainWindow owns persisted intent; safety and per-launch Test qualification own effective state.
    internal void ShowMaterialState(MaterialMode mode, bool effectsAllowed, bool highContrast,
        bool controlledAdvanced, bool readOnly, string? notice)
    {
        synchronizing = true;
        MaterialChoice.SelectedIndex = (int)mode;
        synchronizing = false;
        MaterialChoice.IsEnabled = !readOnly;
        MaterialState.Text = highContrast ? "当前生效：高对比度（系统设置）"
            : mode == MaterialMode.Opaque ? "当前生效：不透明背景"
            : !effectsAllowed ? "当前生效：不透明背景（系统已关闭透明效果或处于节电、远程模式）"
            : mode == MaterialMode.Standard ? "当前生效：标准材质"
            : !controlledAdvanced ? "当前生效：标准材质（高级玻璃仅限受控 Test；已保留所选意图）"
            : "当前生效：详情标题与“继续游玩”使用液态玻璃，其余区域使用标准材质";
        if (notice is not null) MaterialState.Text += " · " + notice;
    }
    internal void SetMaterialSaving(bool saving) => MaterialChoice.IsEnabled = !saving;
    private async void OnMaterialMode(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || MaterialChoice.SelectedIndex < 0) return;
        if (ChangeMaterialMode is { } change) await change((MaterialMode)MaterialChoice.SelectedIndex);
    }
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
