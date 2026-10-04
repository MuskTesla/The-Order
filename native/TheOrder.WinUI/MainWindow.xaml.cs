using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using TheOrder.Application;
using TheOrder.Presentation;
using TheOrder.WinUI.Views;
using TheOrder.WinUI.Startup;
using System.Diagnostics;
using System.Text.Json;

namespace TheOrder.WinUI;

public sealed partial class MainWindow : Window
{
    public WorkspaceViewModel ViewModel { get; }
    private readonly LibraryPage libraryPage;
    private readonly DetailsPage detailsPage;
    private readonly SettingsPage settingsPage;
    private readonly ImportPage importPage;
    private readonly InstallationsPage installationsPage;
    private readonly FilesViewModel files;
    private readonly InstallationNavigation installationNavigation;
    private readonly IImagePipeline images;
    private bool navigating, synchronizing;
    private Task? initialization;
    private readonly DispatcherTimer? diagnosticsTimer;
    private readonly SyntheticScenario? scenario;
    public MainWindow(WorkspaceViewModel workspace, NativeConfiguration configuration, FilesViewModel files, IImagePipeline images, IOfflineFilesClient filesClient, SyntheticScenario? scenario = null)
    {
        this.scenario = scenario;
        this.files = files; this.images = images;
        installationNavigation = new(files);
        ViewModel = workspace; InitializeComponent();
        libraryPage = new(workspace, configuration.Diagnostics, images) { OpenDetails = id => Navigate(WorkspacePage.Details, id), ResolveUnsaved = ResolveUnsavedAsync, OpenImport = () => Navigate(WorkspacePage.Import) };
        detailsPage = new(workspace, images) { ManageInstallations = async () => { if (workspace.SelectedId is { } id) await installationNavigation.OpenAsync(id, ResolveUnsavedAsync, () => Navigate(WorkspacePage.Installations)); },
            ChangeCover = async () => { if (await ResolveUnsavedAsync() && workspace.Draft?.Source is { } details && await PickFile(".png", ".jpg", ".jpeg") is { } path) await workspace.ExecuteFileMutationAsync(token => filesClient.CoverAsync(details, path, token), details.Id); } };
        settingsPage = new(workspace);
        importPage = new(files, workspace) { ChooseFolder = PickFolder };
        installationsPage = new(files, workspace) { ChooseFolder = PickFolder, ChooseExecutable = () => PickFile(".exe") };
        workspace.ThemeChanged += ApplyTheme; workspace.PropertyChanged += OnState;
        ApplyTheme(); ShowPage();
        if (configuration.Diagnostics)
        {
            DiagnosticsPanel.Visibility = Visibility.Visible;
            diagnosticsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
            diagnosticsTimer.Tick += OnDiagnostics; diagnosticsTimer.Start();
        }
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
                ClientOutcome.Committed => InfoBarSeverity.Success, ClientOutcome.CommittedWithWarning or ClientOutcome.ReadOnly or ClientOutcome.Conflict or ClientOutcome.CandidateStale => InfoBarSeverity.Warning,
                ClientOutcome.ValidationFailure or ClientOutcome.IoFailure or ClientOutcome.LoadFailure => InfoBarSeverity.Error, _ => InfoBarSeverity.Informational
            };
    }
    private void ShowPage()
    {
        synchronizing = true;
        PageContent.Content = ViewModel.Page switch { WorkspacePage.Details => detailsPage, WorkspacePage.Settings => settingsPage, WorkspacePage.Import => importPage, WorkspacePage.Installations => installationsPage, _ => libraryPage };
        Navigation.SelectedItem = ViewModel.Page == WorkspacePage.Settings ? SettingsNavigation : LibraryNavigation;
        Navigation.IsBackEnabled = ViewModel.Page != WorkspacePage.Library;
        synchronizing = false;
    }
    private async Task Navigate(WorkspacePage page, string? id = null)
    {
        installationNavigation.Invalidate();
        if (navigating) return; navigating = true;
        try { if (await ResolveInstallationDraft()) { await ViewModel.NavigateAsync(page, id, ChooseUnsaved); ShowPage(); } }
        catch (Exception) { ViewModel.ReportUnexpected(); }
        finally { navigating = false; }
    }
    private async void OnNavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!synchronizing && args.InvokedItemContainer is NavigationViewItem item) await Navigate(item.Tag as string == "settings" ? WorkspacePage.Settings : WorkspacePage.Library);
    }
    private async void OnBack(NavigationView sender, NavigationViewBackRequestedEventArgs args) => await Navigate(WorkspacePage.Library);
    private async Task<UnsavedChoice> ChooseUnsaved()
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "保留尚未保存的修改？", Content = "离开前可以保存修改，或放弃草稿。", PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃草稿", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => UnsavedChoice.Save, ContentDialogResult.Secondary => UnsavedChoice.Discard, _ => UnsavedChoice.Stay };
    }
    public async Task<bool> ResolveUnsavedAsync() => !navigating && await ResolveInstallationDraft() && await ViewModel.ResolveUnsavedAsync(ChooseUnsaved);
    private async Task<bool> ResolveInstallationDraft()
    {
        await files.WaitForMutationAsync();
        if (files.Draft is null) return true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "保留安装草稿？", Content = "安装路径和参数尚未保存。", PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃草稿", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary) { files.CancelDraft(); return true; }
        if (choice == ContentDialogResult.Primary) { await files.SaveAsync(); return files.Draft is null; }
        return false;
    }
    private async Task<string?> PickFolder() => (await new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id) { CommitButtonText = "选择此文件夹" }.PickSingleFolderAsync())?.Path;
    private async Task<string?> PickFile(params string[] extensions)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }
    private void ApplyTheme() => Root.RequestedTheme = ViewModel.Theme switch { NativeTheme.Light => ElementTheme.Light, NativeTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
    public async ValueTask StopAsync()
    {
        installationNavigation.Invalidate();
        if (diagnosticsTimer is not null) { diagnosticsTimer.Stop(); diagnosticsTimer.Tick -= OnDiagnostics; }
        ViewModel.ThemeChanged -= ApplyTheme; ViewModel.PropertyChanged -= OnState;
        // Startup may still hold user preference overrides. Finish loading/merging them
        // before disposal cancels the lifetime, then drain the resulting persistence queue.
        if (initialization is not null) await initialization;
        await files.DisposeAsync();
        await ViewModel.DisposeAsync();
    }
    private void OnDelayDetails(object sender, RoutedEventArgs e) => scenario?.DelayDetails();
    private void OnFailRefresh(object sender, RoutedEventArgs e) => scenario?.FailNextCommittedRefresh();
    private void OnDelayBatch(object sender, RoutedEventArgs e) => scenario?.DelayBatch();
    private void OnDelayQuery(object sender, RoutedEventArgs e) => scenario?.DelayQuery();
    private void OnDelayScan(object sender, RoutedEventArgs e) => scenario?.DelayScan();
    private void OnFailReload(object sender, RoutedEventArgs e) => scenario?.FailNextReload();
    private void OnRelease(object sender, RoutedEventArgs e) => scenario?.Release();
    private void OnCancelOperation(object sender, RoutedEventArgs e) => ViewModel.CancelPendingOperation();
    private void OnDiagnostics(object? sender, object e)
    {
        using var process = Process.GetCurrentProcess();
        var state = ViewModel.IsLoading ? "loading" : !ViewModel.IsLoaded ? "error" : ViewModel.IsReadOnly ? "read-only"
            : ViewModel.IsSearching ? "searching" : ViewModel.TotalCount == 0 ? "empty" : ViewModel.Items.Count == 0 ? "zero-results" : "normal";
        var snapshot = new
        {
            schema = 1, timestamp = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
            build = typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString(),
            checkpoint = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            itemCount = ViewModel.Items.Count, totalCount = ViewModel.TotalCount, viewMode = ViewModel.Layout,
            theme = Root.ActualTheme.ToString(), readOnly = ViewModel.IsReadOnly, state, page = ViewModel.Page.ToString(),
            viewport = libraryPage.RuntimeViewport(), workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            dirty = ViewModel.IsDirty, saving = ViewModel.IsSaving, detailsLoading = ViewModel.IsDetailsLoading,
            preferenceState = ViewModel.PreferenceState.ToString(), savedFilterCount = ViewModel.SavedFilters.Count,
            notice = ViewModel.Notice.Message, persistence = ViewModel.PersistenceResult?.Outcome.ToString(), refresh = ViewModel.LastRefresh.ToString(),
            scenario = scenario?.Status ?? "disabled"
            , importStage = files.Stage.ToString(), candidateCount = files.Candidates.Count,
            scanRoot = files.ScanRoot, scanDirectories = files.Progress?.Directories ?? 0, scanEntries = files.Progress?.Entries ?? 0,
            duplicateCandidates = files.Candidates.Count(c => c.Preflight.Duplicate is not null),
            installationCount = files.Installations?.Rows.Length ?? 0,
            missingInstallations = files.Installations?.Rows.Count(r => r.Status is PreflightStatus.NotFound or PreflightStatus.BrokenLink) ?? 0,
            installationDraft = files.Draft is not null, imageMetrics = images.Metrics,
            localCoverCount = ViewModel.Items.Count(i => i.CoverKey is not null)
        };
        RuntimeState.Text = "THE_ORDER_RUNTIME " + JsonSerializer.Serialize(snapshot);
    }
}
