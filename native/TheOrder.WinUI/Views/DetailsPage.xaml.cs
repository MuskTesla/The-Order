using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Presentation;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed partial class DetailsPage : Page, IImageHost
{
    public WorkspaceViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    public LaunchViewModel Launch { get; }
    public Func<Task>? ManageInstallations { get; set; }
    public Func<Task>? RefreshOnline { get; set; }
    public Func<Task>? Recognize { get; set; }
    private async void OnRecognize(object sender, RoutedEventArgs e) { try { if (Recognize is not null) await Recognize(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    public Func<Task>? RemoveWishlist { get; set; }
    private async void OnRefreshOnline(object sender, RoutedEventArgs e) { try { if (RefreshOnline is not null) await RefreshOnline(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnRemoveWishlist(object sender, RoutedEventArgs e) { try { if (RemoveWishlist is not null) await RemoveWishlist(); } catch { ViewModel.ReportUnexpected(); } }
    public Func<Task>? ChangeCover { get; set; }
    public Func<Task>? ChooseWorkCover { get; set; }
    public Func<Task>? RestoreDefaultCover { get; set; }
    private async void OnWorkCover(object sender, RoutedEventArgs e) { try { if (ChooseWorkCover is not null) await ChooseWorkCover(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnDefaultCover(object sender, RoutedEventArgs e) { try { if (RestoreDefaultCover is not null) await RestoreDefaultCover(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnInstallations(object sender, RoutedEventArgs e) { try { if (ManageInstallations is not null) await ManageInstallations(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    private async void OnCover(object sender, RoutedEventArgs e) { try { if (ChangeCover is not null) await ChangeCover(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    public Func<Task>? GoBack { get; set; }
    public DetailsPage(WorkspaceViewModel viewModel, IImagePipeline images, LaunchViewModel launch)
    {
        ViewModel = viewModel; Images = images; Launch = launch; InitializeComponent();
        // Approved 完全清透 (body mode 3) over a σ18 ambient band; configuration identifiers live on ArtworkStage.
        HeroStage.Variant = StageVariant.Clear; HeroStage.AmbientSigma = 18; HeroStage.StageCornerRadius = 8;
        HeroStage.Images = images; HeroStage.Plate = HeroPlate;
        // R3: the plate's theme follows the artwork polarity over art; the app theme otherwise.
        HeroStage.ForegroundChanged += brush => HeroPlate.RequestedTheme = brush is null ? ElementTheme.Default
            : HeroStage.Polarity == StagePolarity.Deepen ? ElementTheme.Dark : ElementTheme.Light;
        Loaded += async (_, _) =>
        {
            ViewModel.PropertyChanged += OnState; SynchronizeHero();
            try { if (ViewModel.SelectedId is { } id) await Launch.OpenAsync(id); } catch { ViewModel.ReportUnexpected(); }
        };
        Unloaded += (_, _) => { ViewModel.PropertyChanged -= OnState; Launch.Leave(); };
    }
    private void OnState(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.Draft) or nameof(WorkspaceViewModel.SelectedId) or nameof(WorkspaceViewModel.IsDetailsLoading)) SynchronizeHero();
    }
    private void SynchronizeHero()
    {
        HeroStage.CoverKey = ViewModel.Draft?.Source.CoverKey;
        var title = ViewModel.Draft?.Source.Personal.Name ?? "";
        Breadcrumb.ItemsSource = new[] { "书架", title.Length > 40 ? title[..40] + "…" : title };
    }
    private async void OnBreadcrumb(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    { try { if (args.Index == 0 && GoBack is not null) await GoBack(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnLaunch(object sender, RoutedEventArgs e) { try { await Launch.LaunchAsync(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnConfirmPosition(object sender, RoutedEventArgs e) { try { await Launch.ConfirmAsync(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnLaunchInstallation(object sender, SelectionChangedEventArgs e)
    { try { if (LaunchInstallation.SelectedItem is LaunchInstallationItem item) await Launch.SelectAsync(item.Id); } catch { ViewModel.ReportUnexpected(); } }
    private void OnCancelLaunch(object sender, RoutedEventArgs e) => Launch.Cancel();
    // 完全清透 hero stage: the window routes policy (effects allowed and visible) and early release here.
    internal void SetStagePolicy(bool effectsAllowed, bool highContrast, MaterialMode mode) => HeroStage.SetPolicy(effectsAllowed, highContrast, mode);
    internal void ReleaseStageBeforeLeave() => HeroStage.ReleaseBeforeLeave();
    internal object StageEvidence => new { placement = "Details hero", plateTheme = HeroPlate.RequestedTheme.ToString(), plateWidth = HeroPlate.ActualWidth, plateHeight = HeroPlate.ActualHeight, stage = HeroStage.Evidence };
    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reflow the existing controls; drafts, launch state and image ownership stay in place.
        var narrow = e.NewSize.Width < 720; var single = e.NewSize.Width < 900;
        DetailsColumn.Padding = narrow ? new Thickness(16, 8, 16, 16) : new Thickness(32, 12, 32, 24);
        HeroStage.Height = narrow ? 452 : 300;
        HeroLayout.Padding = new Thickness(narrow ? 16 : 28);
        HeroCover.Width = narrow ? 120 : 180; HeroCover.Height = narrow ? 160 : 240;
        HeroCover.VerticalAlignment = narrow ? VerticalAlignment.Top : VerticalAlignment.Center;
        Grid.SetColumn(HeroPlate, narrow ? 0 : 1); Grid.SetColumnSpan(HeroPlate, narrow ? 2 : 1);
        Grid.SetRow(HeroPlate, narrow ? 1 : 0); Grid.SetRowSpan(HeroPlate, narrow ? 1 : 2);
        Grid.SetRowSpan(HeroCover, narrow ? 1 : 2);
        HeroPlate.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        HeroLayout.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        SideColumn.Width = single ? new GridLength(0) : new GridLength(320);
        Grid.SetColumn(SideCards, single ? 0 : 1); Grid.SetRow(SideCards, single ? 1 : 0);
        BodyGrid.ColumnSpacing = single ? 0 : 16;
    }
    public string StatusLabel(string status) => "当前状态 · " + StatusNames.Display(status);
    public string AlternateTitle(string primary, string other) => string.Equals(primary?.Trim(), other?.Trim(), StringComparison.Ordinal) ? "" : other ?? "";
    public Visibility AlternateTitleVisible(string primary, string other) => string.IsNullOrWhiteSpace(AlternateTitle(primary, other)) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasInstallations(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoInstallations(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisible(bool enabled) => enabled ? Visibility.Visible : Visibility.Collapsed;
    // The accent slot shows 启动游戏 unless the existing launch state asks for confirmation (then 确认启动位置).
    public Visibility LaunchVisible(int installations, bool confirm) => installations > 0 && !confirm ? Visibility.Visible : Visibility.Collapsed;
    // Saved rating only; the editor's in-progress text never reaches the hero.
    public string RatingLabel(double? rating) => rating is null ? "未评分" : $"{rating:0.#} 分";
    public string DirtyText(bool dirty) => dirty ? "有尚未保存的修改" : "当前资料已保存";
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        await ViewModel.CancelAndReloadDetailsAsync();
    }
}
