using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;
using System.ComponentModel;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    public OnlineViewModel Online { get; }
    public RecognitionViewModel Recognition { get; }
    public CatalogBrowseSettings Browsing { get; }
    public LanguageSettings Languages { get; }
    public string AppVersion => "The Order " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    private bool editingCredential;
    private bool synchronizing;
    public Func<Task>? CredentialSaved { get; set; }
    public Func<MaterialMode, Task>? ChangeMaterialMode { get; set; }
    // MainWindow owns persisted intent; system policy owns effective state.
    internal void ShowMaterialState(MaterialMode mode, bool effectsAllowed, bool highContrast,
        bool controlledAdvanced, bool readOnly, string? notice)
    {
        synchronizing = true;
        MaterialChoice.SelectedIndex = mode == MaterialMode.Opaque ? (int)MaterialMode.Standard : (int)mode;
        synchronizing = false;
        MaterialChoice.IsEnabled = !readOnly;
        MaterialState.Text = highContrast ? UiText.Get("当前生效：高对比度（系统设置）")
            : !effectsAllowed ? UiText.Get("当前生效：不透明背景（系统已关闭透明效果或处于节电、远程模式）")
            : mode is MaterialMode.Standard or MaterialMode.Opaque ? UiText.Get("当前生效：标准材质")
            : UiText.Get("当前生效：日常玻璃（无色散），用于详情标题与“继续游玩”");
        if (notice is not null) MaterialState.Text += " · " + notice;
    }
    internal void SetMaterialSaving(bool saving) => MaterialChoice.IsEnabled = !saving;
    private async void OnMaterialMode(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || MaterialChoice.SelectedIndex < 0) return;
        if (ChangeMaterialMode is { } change) await change((MaterialMode)MaterialChoice.SelectedIndex);
    }
    public SettingsPage(WorkspaceViewModel viewModel, OnlineViewModel online, RecognitionViewModel recognition, CatalogBrowseSettings browsing, LanguageSettings languages)
    {
        ViewModel = viewModel; Online = online; Recognition = recognition; Browsing = browsing; Languages = languages; InitializeComponent();
        Loaded += (_, _) => { Languages.PropertyChanged += OnLanguageState; SynchronizeLanguage(); };
        Unloaded += (_, _) => Languages.PropertyChanged -= OnLanguageState;
        Loaded += async (_, _) => { Online.PropertyChanged += OnCredentialState; await Online.CheckCredentialAsync(); if (IsLoaded) SynchronizeCredential(); };
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnState; Synchronize(); };
        Loaded += (_, _) => { Browsing.PropertyChanged += OnBrowseState; SynchronizeBrowsing(); };
        Unloaded += (_, _) => Browsing.PropertyChanged -= OnBrowseState;
        Unloaded += (_, _) => { ViewModel.PropertyChanged -= OnState; Online.PropertyChanged -= OnCredentialState; ApiCredential.Password = ""; editingCredential = false; };
    }
    private void OnBrowseState(object? sender, PropertyChangedEventArgs e) => SynchronizeBrowsing();
    private void OnLanguageState(object? sender, PropertyChangedEventArgs e) => SynchronizeLanguage();
    private void SynchronizeLanguage() { synchronizing = true; LanguageOptions.Visibility = Languages.SelectionAvailable ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed; LanguageChoice.SelectedIndex = Array.IndexOf(UiText.Choices, Languages.Selected); LanguageChoice.IsEnabled = Languages.CanChange; synchronizing = false; }
    private async void OnLanguage(object sender, SelectionChangedEventArgs e) { if (!synchronizing && LanguageChoice.SelectedIndex >= 0) await Languages.ChangeAsync(UiText.Choices[LanguageChoice.SelectedIndex]); }
    private void SynchronizeBrowsing() { synchronizing = true; NsfwSwitch.IsOn = Browsing.ShowNsfw; NsfwSwitch.IsEnabled = Browsing.CanChange; synchronizing = false; }
    private async void OnNsfw(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { if (!synchronizing) await Browsing.ChangeAsync(NsfwSwitch.IsOn); }
    private async void OnSaveCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var secret = ApiCredential.Password;
        if (await Online.SaveCredentialAsync(secret))
        {
            if (ApiCredential.Password == secret) { ApiCredential.Password = ""; editingCredential = false; SynchronizeCredential(); }
            if (CredentialSaved is { } saved) await saved();
        }
    }
    private void OnCredentialState(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(OnlineViewModel.HasCredential)) SynchronizeCredential(); }
    private void SynchronizeCredential()
    {
        CredentialEditor.Visibility = !Online.HasCredential || editingCredential ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        ChangeCredentialButton.Visibility = Online.HasCredential && !editingCredential ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        RemoveCredentialButton.Visibility = Online.HasCredential ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        CancelCredentialButton.Visibility = Online.HasCredential ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    private void OnChangeCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { editingCredential = true; SynchronizeCredential(); ApiCredential.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
    private void OnCancelCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { ApiCredential.Password = ""; editingCredential = false; SynchronizeCredential(); ChangeCredentialButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
    private async void OnMissing(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Recognition.StartUnidentifiedAsync(retryPending: true);
    private void OnCancelRecognition(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Recognition.CancelAll();
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
