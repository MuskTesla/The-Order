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
    private readonly Views.OnlinePage onlinePage;
    private readonly OnlineViewModel online;
    private readonly RecognitionViewModel recognition;
    private readonly RecognitionPage recognitionPage;
    private readonly ImportPage importPage;
    private readonly InstallationsPage installationsPage;
    private readonly FilesViewModel files;
    public LaunchViewModel Launch { get; }
    public AppSessionHost Sessions { get; }
    private bool commandsBlocked;
    private string? lifecycleMessage;
    private readonly DispatcherTimer launchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly InstallationNavigation installationNavigation;
    private readonly IImagePipeline images;
    private bool navigating, synchronizing;
    private Task? initialization;
    private readonly DispatcherTimer? diagnosticsTimer;
    private readonly SyntheticScenario? scenario;
    private readonly SyntheticOnline? onlineFixture;
    private readonly bool performanceFixture;
    public MainWindow(WorkspaceViewModel workspace, NativeConfiguration configuration, FilesViewModel files, IImagePipeline images, IOfflineFilesClient filesClient, OnlineViewModel online, RecognitionViewModel recognition, LaunchViewModel launch, AppSessionHost sessions, SyntheticScenario? scenario = null, SyntheticOnline? onlineFixture = null)
    {
        Sessions = sessions;
        this.scenario = scenario; this.online = online; this.onlineFixture = onlineFixture; Launch = launch;
        this.recognition = recognition;
        this.files = files; this.images = images;
        performanceFixture = configuration.Diagnostics && Program.Profile?.Manifest.ProfileKind == NativeProfileKind.Test
            && Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_PERFORMANCE_OUTPUT") is not null;
        installationNavigation = new(files);
        ViewModel = workspace; InitializeComponent();
        // Fluent shell: content extends into the caption area and the TitleBar control is the drag region.
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Navigation.IsEnabled = false;
        libraryPage = new(workspace, configuration.Diagnostics, images) { OpenDetails = id => Navigate(WorkspacePage.Details, id), ResolveUnsaved = ResolveUnsavedAsync, OpenImport = () => Navigate(WorkspacePage.Import), QuickLaunch = QuickLaunchAsync };
        detailsPage = new(workspace, images, launch) { GoBack = GoBackAsync, ManageInstallations = async () => { if (workspace.SelectedId is { } id) await installationNavigation.OpenAsync(id, ResolveUnsavedAsync, () => Navigate(WorkspacePage.Installations)); },
            ChangeCover = async () => { if (await ResolveUnsavedAsync() && workspace.Draft?.Source is { } details && await PickFile(".png", ".jpg", ".jpeg") is { } path) await workspace.ExecuteFileMutationAsync(token => filesClient.CoverAsync(details, path, token), details.Id); } };
        onlinePage = new(online, images);
        recognitionPage = new(recognition, images) { OpenLocalDetails = id => Navigate(WorkspacePage.Details, id) };
        libraryPage.RecognizeSelected = async ids => { if (await ResolveUnsavedAsync()) { await recognition.StartAsync(ids, RecognitionMode.Reidentify); await Navigate(WorkspacePage.Recognition); } };
        detailsPage.Recognize = async () => { if (workspace.SelectedId is { } id && await ResolveUnsavedAsync()) { await recognition.StartAsync([id], RecognitionMode.Reidentify); await Navigate(WorkspacePage.Recognition); } };
        settingsPage = new(workspace, online) { ChangeMaterialMode = SetMaterialMode };
        detailsPage.RefreshOnline = async () => { if (await ResolveUnsavedAsync() && workspace.SelectedId is { } id) await online.RefreshEntryAsync(id); };
        detailsPage.RemoveWishlist = async () => { if (await ResolveUnsavedAsync() && workspace.SelectedId is { } id) { await online.RemoveWishlistAsync(id); if (workspace.PersistenceResult?.IsCommitted == true && workspace.SelectedId == id && workspace.Page == WorkspacePage.Details) await Navigate(WorkspacePage.Library); } };
        importPage = new(files, workspace) { ChooseFolder = PickFolder };
        installationsPage = new(files, workspace) { ChooseFolder = PickFolder, ChooseExecutable = () => PickFile(".exe") };
        InitializeVisualSystem(configuration);
        workspace.ThemeChanged += ApplyTheme; workspace.PropertyChanged += OnState;
        ApplyTheme(); ShowPage();
        InitializeShellInput(configuration.Diagnostics);
        launchTimer.Tick += OnLaunchRefresh; launchTimer.Start();
        if (configuration.Diagnostics)
        {
            DiagnosticsPanel.Visibility = Visibility.Visible;
            if (onlineFixture?.BoundaryFixture == true) RecognitionBoundaryControls.Visibility = Visibility.Visible;
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
            if (performanceFixture) _ = RunPerformanceAfterInitializationAsync();
        };
    }
    private async Task Initialize()
    {
        try { await Sessions.InitializeAsync(); await ViewModel.InitializeAsync(); }
        catch (Exception exception) { App.TraceVisualFailure(exception); lifecycleMessage = "启动恢复未完成，已禁用操作；请退出后重试。"; ViewModel.ReportUnexpected(); }
        RefreshSession();
    }
    public void SetCommandsBlocked(bool value) { commandsBlocked = value; RefreshSession(); }
    public void ShowLifecycleError(string message) { lifecycleMessage = message; RefreshSession(); }
    public void CancelBackgroundRequests() { online.Leave(); recognition.CancelAll(); files.CancelScan(); ViewModel.CancelPendingOperation(); }
    private async void OnPauseTiming(object sender, RoutedEventArgs e) { try { await Sessions.PauseAsync(); } catch { ShowLifecycleError("暂停计时未完成，请重试。"); } RefreshSession(); }
    private async void OnResumeTiming(object sender, RoutedEventArgs e) { try { await Sessions.ResumeAsync(); } catch { ShowLifecycleError("恢复计时未完成，请重试。"); } RefreshSession(); }
    private void RefreshSession()
    {
        Navigation.IsEnabled = Sessions.Ready && !Sessions.Closing && !commandsBlocked;
        var value = SessionPresentation.From(Sessions.Session, Sessions.Library, Sessions.CanMutate && !commandsBlocked);
        LifecycleNotice.IsOpen = lifecycleMessage is not null; LifecycleNotice.Message = lifecycleMessage ?? "";
        SessionTitle.Text = value.Title; SessionState.Text = Sessions.Error ?? (Sessions.Ready ? value.State : "正在恢复上次会话…");
        SessionDuration.Text = value.Duration; SessionFlyoutDuration.Text = value.Duration; SessionHistory.Text = value.History;
        SessionChipTitle.Text = Sessions.Session?.Session is null ? (Sessions.Error is not null ? "会话异常" : "正在恢复会话") : value.Title;
        SessionHistory.Visibility = string.IsNullOrEmpty(value.History) ? Visibility.Collapsed : Visibility.Visible;
        PauseTiming.IsEnabled = value.CanPause; ResumeTiming.IsEnabled = value.CanResume;
        UpdateSessionChrome(value);
        if (visualFixtureTimer is not null && round3TimerOverride is not null) { SessionDuration.Text = round3TimerOverride; SessionDuration.Visibility = Visibility.Visible; }
        App.Instance?.RefreshTray(value);
    }
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
        LeaveGallery();
        // Swapchain owners release while still in the live tree (proven early-release order).
        if (ViewModel.Page != WorkspacePage.Library) libraryPage.ReleaseStageBeforeLeave();
        if (ViewModel.Page != WorkspacePage.Details) detailsPage.ReleaseStageBeforeLeave();
        PageContent.Content = ViewModel.Page switch { WorkspacePage.Recognition => recognitionPage, WorkspacePage.Online => onlinePage, WorkspacePage.Details => detailsPage, WorkspacePage.Settings => settingsPage, WorkspacePage.Import => importPage, WorkspacePage.Installations => installationsPage, _ => libraryPage };
        Navigation.SelectedItem = ViewModel.Page == WorkspacePage.Settings ? SettingsNavigation : ViewModel.Page == WorkspacePage.Online ? OnlineNavigation : ViewModel.Page == WorkspacePage.Recognition ? RecognitionNavigation : LibraryNavigation;
        AppTitleBar.IsBackButtonEnabled = ViewModel.Page != WorkspacePage.Library;
        synchronizing = false;
        SynchronizeTitleSearch();
        AnimatePage();
    }
    private async Task Navigate(WorkspacePage page, string? id = null)
    {
        installationNavigation.Invalidate();
        if (page != WorkspacePage.Online) online.Leave();
        if (navigating) return; navigating = true;
        try { if (await ResolveInstallationDraft()) { await ViewModel.NavigateAsync(page, id, ChooseUnsaved); ShowPage(); } }
        catch (Exception) { ViewModel.ReportUnexpected(); }
        finally { navigating = false; }
    }
    private async void OnNavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "visual-gallery" }) { ShowGallery(); return; }
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "visual-states" }) { ShowSessionStateFixtures(); return; }
        if (!synchronizing && args.InvokedItemContainer is NavigationViewItem item) await Navigate((item.Tag as string) switch { "settings" => WorkspacePage.Settings, "online" => WorkspacePage.Online, "recognition" => WorkspacePage.Recognition, _ => WorkspacePage.Library });
    }
    private async void OnTitleBack(TitleBar sender, object args) => await GoBackAsync();
    private Task GoBackAsync() => ViewModel.Page == WorkspacePage.Library ? Task.CompletedTask : Navigate(WorkspacePage.Library);
    private async Task<UnsavedChoice> ChooseUnsaved()
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "保留尚未保存的修改？", Content = "离开前可以保存修改，或放弃草稿。", PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃草稿", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => UnsavedChoice.Save, ContentDialogResult.Secondary => UnsavedChoice.Discard, _ => UnsavedChoice.Stay };
    }
    public async Task<bool> ResolveUnsavedAsync() => !navigating && await ResolveInstallationDraft() && await ViewModel.ResolveUnsavedAsync(ChooseUnsaved);
    private async Task<bool> ResolveInstallationDraft()
    {
        await files.WaitForMutationAsync();
        if (files.Draft is null) return true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "保留安装草稿？", Content = "安装路径和参数尚未保存。", PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃草稿", CloseButtonText = "继续编辑", DefaultButton = ContentDialogButton.Close };
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
    private void ApplyTheme()
    {
        Root.RequestedTheme = ViewModel.Theme switch { NativeTheme.Light => ElementTheme.Light, NativeTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyMaterialPolicy();
    }
    public void CancelOnlineRequests() => online.Leave();
    private void OnLaunchRefresh(object? sender, object e) { Launch.Refresh(); RefreshSession(); }
    public async ValueTask StopAsync()
    {
        DisposeVisualSystem();
        DisposeShellInput();
        onlineFixture?.ReleaseCandidate();
        launchTimer.Stop(); launchTimer.Tick -= OnLaunchRefresh;
        await Launch.DisposeAsync();
        installationNavigation.Invalidate();
        if (diagnosticsTimer is not null) { diagnosticsTimer.Stop(); diagnosticsTimer.Tick -= OnDiagnostics; }
        ViewModel.ThemeChanged -= ApplyTheme; ViewModel.PropertyChanged -= OnState;
        // Startup may still hold user preference overrides. Finish loading/merging them
        // before disposal cancels the lifetime, then drain the resulting persistence queue.
        if (initialization is not null) await initialization;
        await recognition.DisposeAsync();
        await online.DisposeAsync();
        await files.DisposeAsync();
        await ViewModel.DisposeAsync();
    }
    private void OnDelayDetails(object sender, RoutedEventArgs e) => scenario?.DelayDetails();
    private void OnDelayCandidate(object sender, RoutedEventArgs e) => onlineFixture?.DelayCandidate();
    private void OnReleaseCandidate(object sender, RoutedEventArgs e) => onlineFixture?.ReleaseCandidate();
    private async void OnSyntheticAuthChange(object sender, RoutedEventArgs e)
    { if (onlineFixture?.BoundaryFixture == true) await online.SaveCredentialAsync("synthetic-only-replacement"); }
    private void OnFailRefresh(object sender, RoutedEventArgs e) => scenario?.FailNextCommittedRefresh();
    private void OnDelayBatch(object sender, RoutedEventArgs e) => scenario?.DelayBatch();
    private void OnDelayQuery(object sender, RoutedEventArgs e) => scenario?.DelayQuery();
    private void OnDelayScan(object sender, RoutedEventArgs e) => scenario?.DelayScan();
    private void OnFailReload(object sender, RoutedEventArgs e) => scenario?.FailNextReload();
    private void OnRelease(object sender, RoutedEventArgs e) => scenario?.Release();
    private void OnCancelOperation(object sender, RoutedEventArgs e) => ViewModel.CancelPendingOperation();
    private void OnDiagnostics(object? sender, object e)
    {
        if (performanceFixture)
        {
            double now = Program.StartupClock.Elapsed.TotalMilliseconds;
            if (performanceLastUiTick > 0 && performanceUiGaps.Count < 32768) performanceUiGaps.Add(now - performanceLastUiTick);
            performanceLastUiTick = now;
        }
        using var process = Process.GetCurrentProcess();
        var state = ViewModel.IsLoading ? "loading" : !ViewModel.IsLoaded ? "error" : ViewModel.IsReadOnly ? "read-only"
            : ViewModel.IsSearching ? "searching" : ViewModel.TotalCount == 0 ? "empty" : ViewModel.Items.Count == 0 ? "zero-results" : "normal";
        var snapshot = new
        {
            schema = 1, timestamp = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
            visual = VisualEvidence(),
            launch = new { outcome = Launch.Runtime.Outcome.ToString(), tracking = Launch.Runtime.Tracking.ToString(), game = Launch.Runtime.Snapshot?.Game.GameId,
                operation = Launch.Runtime.Snapshot?.OperationId, generation = Launch.Runtime.Snapshot?.Generation, initialPid = Launch.Runtime.Initial?.Pid,
                currentPid = Launch.Runtime.Current?.Pid, creationTime = Launch.Runtime.Current?.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture), created = Launch.Runtime.ProcessCreated,
                startMs = Launch.Runtime.StartMilliseconds, attachMs = Launch.Runtime.AttachMilliseconds, handoffMs = Launch.Runtime.HandoffMilliseconds,
                exitOccurredAtUtc = Launch.Runtime.ExitOccurredAtUtc,
                lastRequest = Launch.LastRequestOutcome?.ToString(), selected = Launch.SelectedInstallationId, availability = Launch.Availability.ToString() },
            build = typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString(),
            session = new { id = Sessions.Session?.Session?.Id, lifecycle = Sessions.Session?.Session?.Native?.Lifecycle.ToString(), paused = Sessions.Session?.Session?.Native?.UserPaused,
                installation = Sessions.Session?.Session?.Native?.InstallationId, recoveryCount = Sessions.Session?.Session?.Native?.Recovery.Count, seconds = Sessions.Session?.Session?.Seconds,
                tracking = Sessions.Session?.Tracking.ToString(), timing = Sessions.Session?.Timing.ToString(), error = Sessions.Error,
                pid = Sessions.Session?.Current?.Pid, creationTime = Sessions.Session?.Current?.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                accumulated100ns = Sessions.Session?.Session?.Native?.Accumulated100ns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                count = Sessions.Library?.Entries.FirstOrDefault(e => e.Id == Sessions.Session?.Session?.Native?.GameId)?.PlayCount,
                totalSeconds = Sessions.Library?.Entries.FirstOrDefault(e => e.Id == Sessions.Session?.Session?.Native?.GameId)?.TotalPlaySeconds },
            lifecycle = new { ready = Sessions.Ready, closing = Sessions.Closing, activations = Program.Activations, tray = App.Instance?.TrayAvailable, hidden = App.Instance?.WindowHidden },
            checkpoint = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            itemCount = ViewModel.Items.Count, totalCount = ViewModel.TotalCount, viewMode = ViewModel.Layout,
            theme = Root.ActualTheme.ToString(), readOnly = ViewModel.IsReadOnly, state, page = galleryVisible ? visualScreen : ViewModel.Page.ToString(),
            viewport = libraryPage.RuntimeViewport(), workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            processCpuMs = process.TotalProcessorTime.TotalMilliseconds, processHandles = process.HandleCount,
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
            localCoverCount = ViewModel.Items.Count(i => i.HasLocalCover),
            onlineState = online.SearchState.ToString(), onlineCount = online.Items.Count, onlineDetails = online.DetailState.ToString(), onlineWorkId = online.Details?.Id, onlineHasCover = online.DetailCoverKey is not null, onlineConnection = online.ConnectionState.ToString(),
            onlineAuthGeneration = online.AuthGeneration, onlineCredentialPresent = online.HasCredential, syntheticDetailRequests = onlineFixture?.DetailRequests,
            onlineQueryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(online.Query.Trim()))),
            recognition = new {
                progress = recognition.Progress, reviewCount = recognition.Reviews.Count,
                selectedGame = recognition.SelectedGameId, selectedReview = recognition.SelectedId,
                status = recognition.SelectedReview?.Status.ToString(), candidateCount = recognition.Candidates.Count,
                candidates = recognition.SelectedReview?.Result?.Candidates.Select(c => new { id = c.Work.Id, high = c.HighConfidence, veto = c.Vetoed, uncertain = c.VersionNeedsConfirmation, score = c.Confidence }),
                selectedCandidate = recognition.SelectedCandidateId, canApply = recognition.CanApply,
                displayedCandidate = recognition.DisplayedCandidateId, remoteFacts = recognition.RemoteFacts, hasRemoteCover = recognition.RemoteCoverKey is not null,
                localCandidates = recognition.SelectedReview?.Result?.Identity.LocalCandidates.Length,
                unavailable = recognition.SelectedReview?.Result?.EnrichmentUnavailable,
                algorithm = recognition.SelectedReview?.AlgorithmVersion, decision = recognition.LastDecision?.Outcome.ToString(),
                lastJob = recognition.LastJobState?.ToString(), online = recognition.UseOnline, persistenceError = recognition.PersistenceError
            }
        };
        RuntimeState.Text = "THE_ORDER_RUNTIME " + JsonSerializer.Serialize(snapshot);
    }
}
