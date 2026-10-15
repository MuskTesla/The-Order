using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;
public sealed partial class OnlinePage : Page, IImageHost
{
    public OnlineViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    private readonly CatalogBrowserView browser;
    private ItemsWrapGrid? wrap;
    public bool IsBrowsing => BrowserHost.Visibility == Visibility.Visible;
    public event Action? NavigationChanged;
    public OnlinePage(OnlineViewModel viewModel, IImagePipeline images, IOnlineCatalog catalog, IEntityCatalog entities, IRemoteCoverRegistry registry, CatalogPresentationService presentations, Func<LibrarySnapshot?> readLibrary)
    {
        ViewModel = viewModel; Images = images; InitializeComponent();
        browser = new(catalog, entities, images, registry, () => viewModel.AuthGeneration, presentations, readLibrary) { ReturnToSearch = ReturnToSearchAsync, AddWishlist = id => viewModel.AddWishlistAsync(id) }; BrowserHost.Content = browser;
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnSearchState; UpdateSearchLayout(); };
        Unloaded += (_, _) => { ViewModel.PropertyChanged -= OnSearchState; browser.Cancel(); };
        SizeChanged += (_, _) => UpdateSearchLayout();
    }
    private void OnSearchState(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(OnlineViewModel.Items) or nameof(OnlineViewModel.SearchBusy) or nameof(OnlineViewModel.CanLoadMore)) UpdateSearchLayout(); }
    private void UpdateSearchLayout()
    {
        var empty = ViewModel.Items.Count == 0;
        var narrow = ActualWidth is > 0 and < 641;
        OnlineRoot.Padding = narrow ? new Thickness(16, 12, 16, 16) : new Thickness(24, 16, 24, 24);
        Grid.SetRow(SearchHeader, empty ? 1 : 0);
        SearchHeader.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top;
        SearchHeader.Spacing = empty ? 20 : 12;
        SearchHeader.MaxWidth = empty ? 760 : double.PositiveInfinity;
        SearchHeader.Margin = empty ? ActualHeight > 560 ? new Thickness(12, 24, 12, 72) : new Thickness(8, 8, 8, 8) : new Thickness(0);
        SearchBar.MaxWidth = 760;
        SearchBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        DiscoveryTitle.TextAlignment = empty ? TextAlignment.Center : TextAlignment.Left;
        DiscoveryHint.Visibility = ProviderAttribution.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.TextAlignment = empty ? TextAlignment.Center : TextAlignment.Left;
        CancelSearchButton.Visibility = ViewModel.SearchBusy ? Visibility.Visible : Visibility.Collapsed;
        ResultsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.Visibility = ViewModel.CanLoadMore ? Visibility.Visible : Visibility.Collapsed;
        ResizeCards();
    }
    public static Visibility TextVisible(string? text) => !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    public void FocusSearch() { _ = ReturnToSearchAsync(); OnlineQueryBox.Focus(FocusState.Keyboard); }
    public void Cancel() => browser.Cancel();
    internal void ConfigureMusic(IMusicBrainzReader music, IMusicBrainzEntityReader? entities = null) => browser.ConfigureMusic(music, entities);
    internal void ConfigureAniList(IAniListReader reader, IWatchLinkPreferencesStore? watchPreferences = null, IBangumiWatchLinksReader? watchReader = null) => browser.ConfigureAniList(reader, watchPreferences, watchReader);
    internal void ConfigureReleases(ICatalogReleaseReader reader, Func<long> auth) => browser.ConfigureReleases(reader, auth);
    internal void ConfigureEpisodes(IBangumiEpisodesReader reader) => browser.ConfigureEpisodes(reader);
    internal void ConfigureBooks(IBookBibliographyReader books, IBangumiBookRelationsReader relations) => browser.ConfigureBooks(books, relations);
    internal void ConfigureRelatedWorks(IBangumiRelationsReader reader, IBangumiSubjectDetailsReader subjects,
        IRelatedSemanticReader semantics, IRelatedGameIdentityReader? identities, Func<string>? accessScope = null) => browser.ConfigureRelatedWorks(reader, subjects, semantics, identities, accessScope);
    internal void InvalidateBangumiAccess() => browser.InvalidateBangumiAccess();
    internal void DisposeRelated() => browser.DisposeRelated();
    internal void ResetBrowsing() { browser.Reset(); BrowserHost.Visibility = Visibility.Collapsed; SearchResults.Visibility = Visibility.Visible; NavigationChanged?.Invoke(); }
    internal void RefreshPrepared(string id) => browser.RefreshPrepared(id);
    internal void SetStagePolicy(bool allowed, bool contrast, MaterialMode mode) => browser.SetStagePolicy(allowed, contrast, mode);
    internal object? StageEvidence => browser.HeroEvidence;
    public async Task<bool> GoBackAsync() { if (BrowserHost.Visibility != Visibility.Visible) return false; if (!await browser.GoBackAsync()) await ReturnToSearchAsync(); return true; }
    private Task ReturnToSearchAsync() { ResetBrowsing(); ResultsGrid.Focus(FocusState.Programmatic); return Task.CompletedTask; }
    public Visibility BusyVisibility(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;
    private async void OnSearch(object sender, RoutedEventArgs e) => await ViewModel.SearchAsync();
    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; ViewModel.Query = OnlineQueryBox.Text; await ViewModel.SearchAsync(); } }
    private async void OnMore(object sender, RoutedEventArgs e) => await ViewModel.SearchAsync(true);
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelSearch();
    private async void OnWork(object sender, ItemClickEventArgs e) { if (e.ClickedItem is OnlineCard card) { SearchResults.Visibility = Visibility.Collapsed; BrowserHost.Visibility = Visibility.Visible; NavigationChanged?.Invoke(); await browser.OpenAsync(new(CatalogEntityKind.Work, card.Id, card.Title), false); } }
    private void OnWrapLoaded(object sender, RoutedEventArgs e) { wrap = sender as ItemsWrapGrid; ResizeCards(); }
    private void OnResultsSizeChanged(object sender, SizeChangedEventArgs e) => ResizeCards();
    private void ResizeCards()
    {
        if (wrap is null || ResultsGrid.ActualWidth <= 0) return;
        var width = Math.Max(140, ResultsGrid.ActualWidth - 4);
        var minCard = ActualWidth is > 0 and < 641 ? 136 : 168;
        var columns = Math.Max(1, (int)Math.Floor(width / minCard));
        var card = Math.Floor(width / columns);
        wrap.ItemWidth = card;
        wrap.ItemHeight = Math.Round((card - 16) * 4d / 3 + 94);
    }
}
