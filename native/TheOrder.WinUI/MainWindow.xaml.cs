using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using TheOrder.Application;
using TheOrder.Presentation;
using TheOrder.WinUI.Views;

namespace TheOrder.WinUI;

public sealed partial class MainWindow : Window
{
    public WorkspaceViewModel ViewModel { get; }
    private readonly LibraryPage libraryPage;
    private readonly DetailsPage detailsPage;
    private readonly SettingsPage settingsPage;
    private bool navigating, synchronizing;
    private Task? initialization;
    public MainWindow(WorkspaceViewModel workspace, NativeConfiguration configuration)
    {
        ViewModel = workspace; InitializeComponent();
        libraryPage = new(workspace, configuration.Diagnostics) { OpenDetails = id => Navigate(WorkspacePage.Details, id), ResolveUnsaved = ResolveUnsavedAsync };
        detailsPage = new(workspace); settingsPage = new(workspace);
        workspace.ThemeChanged += ApplyTheme; workspace.PropertyChanged += OnState;
        ApplyTheme(); ShowPage();
        Root.Loaded += (_, _) =>
        {
            if (initialization is not null) return;
            var scale = Root.XamlRoot.RasterizationScale;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min((int)(1160 * scale), (int)(area.Width * .95));
            var height = Math.Min((int)(820 * scale), (int)(area.Height * .95));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
            initialization = Initialize();
        };
    }
    private async Task Initialize() { try { await ViewModel.InitializeAsync(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    private void OnState(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Page)) ShowPage();
        if (e.PropertyName == nameof(WorkspaceViewModel.Notice))
            NoticeBar.Severity = ViewModel.Notice.Outcome switch
            {
                ClientOutcome.Committed => InfoBarSeverity.Success, ClientOutcome.CommittedWithWarning or ClientOutcome.ReadOnly or ClientOutcome.Conflict => InfoBarSeverity.Warning,
                ClientOutcome.ValidationFailure or ClientOutcome.IoFailure or ClientOutcome.LoadFailure => InfoBarSeverity.Error, _ => InfoBarSeverity.Informational
            };
    }
    private void ShowPage()
    {
        synchronizing = true;
        PageContent.Content = ViewModel.Page switch { WorkspacePage.Details => detailsPage, WorkspacePage.Settings => settingsPage, _ => libraryPage };
        Navigation.SelectedItem = ViewModel.Page == WorkspacePage.Settings ? SettingsNavigation : LibraryNavigation;
        Navigation.IsBackEnabled = ViewModel.Page != WorkspacePage.Library;
        synchronizing = false;
    }
    private async Task Navigate(WorkspacePage page, string? id = null)
    {
        if (navigating) return; navigating = true;
        try { await ViewModel.NavigateAsync(page, id, ChooseUnsaved); ShowPage(); }
        catch (Exception) { ViewModel.ReportUnexpected(); }
        finally { navigating = false; }
    }
    private async void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!synchronizing && args.SelectedItem is NavigationViewItem item) await Navigate(item.Tag as string == "settings" ? WorkspacePage.Settings : WorkspacePage.Library);
    }
    private async void OnBack(NavigationView sender, NavigationViewBackRequestedEventArgs args) => await Navigate(WorkspacePage.Library);
    private async Task<UnsavedChoice> ChooseUnsaved()
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "保留尚未保存的修改？", Content = "离开前可以保存修改，或放弃草稿。", PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃草稿", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => UnsavedChoice.Save, ContentDialogResult.Secondary => UnsavedChoice.Discard, _ => UnsavedChoice.Stay };
    }
    public Task<bool> ResolveUnsavedAsync() => navigating ? Task.FromResult(false) : ViewModel.ResolveUnsavedAsync(ChooseUnsaved);
    private void ApplyTheme() => Root.RequestedTheme = ViewModel.Theme switch { NativeTheme.Light => ElementTheme.Light, NativeTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
    public async ValueTask StopAsync()
    {
        ViewModel.ThemeChanged -= ApplyTheme; ViewModel.PropertyChanged -= OnState;
        await ViewModel.DisposeAsync();
        if (initialization is not null) await initialization;
    }
}
