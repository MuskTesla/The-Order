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
    public AppUpdateViewModel Updates { get; }
    public NextMoeSettingsViewModel NextMoe { get; }
    public Func<VerifiedAppUpdate, Task<bool>>? InstallUpdate { get; set; }
    public string AppVersion => "The Order " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    private bool editingCredential;
    private bool synchronizing;
    private bool synchronizingUpdateChannel;
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
        MaterialError.Text = notice ?? "";
        MaterialError.Visibility = string.IsNullOrEmpty(notice) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }
    internal void SetMaterialSaving(bool saving) => MaterialChoice.IsEnabled = !saving;
    private async void OnMaterialMode(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || MaterialChoice.SelectedIndex < 0) return;
        if (ChangeMaterialMode is { } change) await change((MaterialMode)MaterialChoice.SelectedIndex);
    }
    public SettingsPage(WorkspaceViewModel viewModel, OnlineViewModel online, RecognitionViewModel recognition, CatalogBrowseSettings browsing, LanguageSettings languages, AppUpdateViewModel updates, NextMoeSettingsViewModel nextMoe)
    {
        ViewModel = viewModel; Online = online; Recognition = recognition; Browsing = browsing; Languages = languages; Updates = updates; NextMoe = nextMoe; InitializeComponent();
        Loaded += (_, _) => { NextMoe.PropertyChanged += OnNextMoeState; SynchronizeNextMoe(); };
        Unloaded += (_, _) => { NextMoe.PropertyChanged -= OnNextMoeState; };
        Loaded += (_, _) => { Updates.PropertyChanged += OnUpdateState; SynchronizeUpdate(); };
        Unloaded += (_, _) => Updates.PropertyChanged -= OnUpdateState;
        Loaded += (_, _) => { Languages.PropertyChanged += OnLanguageState; SynchronizeLanguage(); };
        Unloaded += (_, _) => Languages.PropertyChanged -= OnLanguageState;
        Loaded += async (_, _) => { Online.PropertyChanged += OnCredentialState; await Online.CheckCredentialAsync(); if (IsLoaded) SynchronizeCredential(); };
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnState; Synchronize(); };
        Loaded += (_, _) => { Browsing.PropertyChanged += OnBrowseState; SynchronizeBrowsing(); };
        Unloaded += (_, _) => Browsing.PropertyChanged -= OnBrowseState;
        Unloaded += (_, _) => { ViewModel.PropertyChanged -= OnState; Online.PropertyChanged -= OnCredentialState; ApiCredential.Password = ""; editingCredential = false; };
    }
    private void OnUpdateState(object? sender, PropertyChangedEventArgs e) => SynchronizeUpdate();
    private void SynchronizeUpdate()
    {
        synchronizingUpdateChannel = true;
        UpdateChannelChoice.SelectedIndex = (int)Updates.SelectedChannel;
        synchronizingUpdateChannel = false;
        var visible = Microsoft.UI.Xaml.Visibility.Visible; var collapsed = Microsoft.UI.Xaml.Visibility.Collapsed;
        UpdateNotes.Visibility = Updates.HasUpdate && Updates.Notes.Length != 0 ? visible : collapsed;
        UpdateHighlights.Visibility = Updates.HasUpdate ? visible : collapsed;
        InstallUpdateButton.Visibility = Updates.HasUpdate ? visible : collapsed;
        UpdateInstallNotice.Visibility = Updates.HasUpdate ? visible : collapsed;
        UpdateProgress.Visibility = Updates.IsBusy ? visible : collapsed;
        CancelUpdateButton.Visibility = Updates.CanCancel ? visible : collapsed;
    }
    private async void OnCheckUpdate(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Updates.CheckAsync();
    private async void OnUpdateChannel(object sender, SelectionChangedEventArgs e)
    { if (!synchronizingUpdateChannel && UpdateChannelChoice.SelectedIndex >= 0) await Updates.ChangeChannelAsync((AppUpdateChannel)UpdateChannelChoice.SelectedIndex); }
    private async void OnInstallUpdate(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (InstallUpdate is not { } install || !Updates.CanInstall) return;
        if (Updates.IsChannelChange)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Updates.InstallText,
                Content = "将切换到 " + Updates.Latest!.Version + "。退出保存后会先备份当前资料，再替换程序。当前书架、游玩记录和个人资料保留。正式版可能不提供 Beta 的跨媒体与辅助启动功能，但其资料和配置会保留。",
                PrimaryButtonText = "确认切换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await Updates.InstallAsync(install);
    }
    private void OnCancelUpdate(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Updates.Cancel();
    internal void ChooseUpdateChannelTest(int index) => UpdateChannelChoice.SelectedIndex = index;
    internal void ShowUpdatesTest() => UpdateSection.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { VerticalAlignmentRatio = 0 });
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
            await NextMoe.InitializeAsync();
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
    private async void OnClearCredential(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { ApiCredential.Password = ""; await Online.ClearCredentialAsync(); await NextMoe.InitializeAsync(); }
    private async void OnTestConnection(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Online.TestConnectionAsync();
    private void OnState(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ViewModel.Theme)) Synchronize(); }
    private void Synchronize() { synchronizing = true; ThemeChoice.SelectedIndex = (int)ViewModel.Theme; synchronizing = false; }
    private async void OnAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizing || ThemeChoice.SelectedIndex < 0) return;
        await ViewModel.ChangeAppearanceAsync((NativeTheme)ThemeChoice.SelectedIndex, ViewModel.Layout);
    }
    private void OnSettingsSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        SettingsColumn.Width = Math.Min(900, Math.Max(0, e.NewSize.Width));
        ResizeSettingsControls();
    }
    private void ResizeSettingsControls()
    {
        var width = Math.Max(0, SettingsColumn.Width - SettingsColumn.Padding.Left - SettingsColumn.Padding.Right);
        foreach (var control in Descendants(SettingsColumn))
            if (control is ComboBox or PasswordBox) control.Width = Math.Min(control.MaxWidth, Math.Max(0, width - (Inside(control, SyncPreviewRows) ? 48 : 0)));
    }
    private static bool Inside(Microsoft.UI.Xaml.DependencyObject control, Microsoft.UI.Xaml.DependencyObject parent)
    { for (var current = control; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, parent)) return true; return false; }
    private static bool IsVisible(Microsoft.UI.Xaml.DependencyObject control)
    { for (var current = control; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        if (current is Microsoft.UI.Xaml.FrameworkElement { Visibility: Microsoft.UI.Xaml.Visibility.Collapsed }) return false; return true; }
    private static IEnumerable<Microsoft.UI.Xaml.FrameworkElement> Descendants(Microsoft.UI.Xaml.DependencyObject root)
    {
        for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index); if (child is Microsoft.UI.Xaml.FrameworkElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    public string ActiveRecognitionMessage(int total, string message) => total > 0 ? message : "";
    public string OperationalMessage(string message) => message.Contains("SFW / NSFW", StringComparison.Ordinal) ? "" : message;
    internal object LayoutEvidence() => new { active = IsLoaded, columnWidth = SettingsColumn.ActualWidth, rootWidth = SettingsRoot.ActualWidth,
        controls = Descendants(SettingsColumn).Where(x => x is ComboBox or PasswordBox).Select(x => {
            var point = x.TransformToVisual(SettingsColumn).TransformPoint(new Windows.Foundation.Point());
            return new { name = x.Name, visible = IsLoaded && IsVisible(x), alignment = x.HorizontalAlignment.ToString(), x = point.X, width = x.ActualWidth, fits = point.X + x.ActualWidth <= SettingsColumn.ActualWidth + .5 }; }).ToArray(),
        headings = Descendants(SettingsColumn).OfType<TextBlock>().Where(x => x.Text.Length > 0 && IsVisible(x)).Select(x => x.Text).ToArray() };
}
