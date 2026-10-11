using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// Read-only native browsing. All additions still use the existing explicit wishlist transaction.
public sealed class CatalogBrowserView : Grid, IImageHost
{
    private readonly IOnlineCatalog catalog;
    private readonly CatalogPresentationService presentations;
    private CatalogContentView? workContent;
    private readonly IEntityCatalog entities;
    private readonly IRemoteCoverRegistry registry;
    private readonly Func<long> authGeneration;
    private readonly Func<LibrarySnapshot?> readLibrary;
    private readonly Stack<CatalogEntityRef> history = new();
    private readonly TextBlock title = new() { FontSize = 28, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button back = new() { Content = UiText.Get("返回"), Visibility = Visibility.Collapsed };
    private readonly StackPanel body = new() { Spacing = 20 };
    private readonly ScrollViewer scroll;
    private BannerView? hero;
    private bool stageAllowed, stageContrast;
    private MaterialMode stageMode = MaterialMode.Standard;
    public void SetStagePolicy(bool allowed, bool highContrast, MaterialMode mode) { stageAllowed = allowed; stageContrast = highContrast; stageMode = mode; hero?.SetPolicy(allowed, highContrast, mode); }
    internal object? HeroEvidence => hero?.Evidence;
    private CancellationTokenSource? stop;
    private long generation;
    private CatalogEntityRef? current;
    private string? cursor;
    private readonly HashSet<string> seenWorks = new(), seenCursors = new();
    private TagFlowPanel? worksGrid;
    private TagFlowPanel? shelfGrid;
    private CatalogShelfMembership? shelfMembership;
    private TextBlock shelfHeading = new() { FontSize = 22 }, outsideHeading = new() { FontSize = 22 };
    private TextBlock shelfEmpty = new() { TextWrapping = TextWrapping.Wrap }, outsideEmpty = new() { TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<string> shelfWorkIds = new(StringComparer.Ordinal);
    internal object? ShelfEvidence => current?.Kind == CatalogEntityKind.Tag && shelfMembership is not null
        ? new { loaded = seenWorks.Count, inShelf = shelfGrid?.Children.Count ?? 0, outside = worksGrid?.Children.Count ?? 0,
            more = cursor is not null, inShelfWorkIds = shelfWorkIds.ToArray(), outsideWorkIds = seenWorks.Except(shelfWorkIds).ToArray() } : null;
    private Button? more;
    public IImagePipeline Images { get; }
    public Func<Task>? ReturnToSearch { get; set; }
    public Func<string, Task>? AddWishlist { get; set; }
    public bool CanGoBack => history.Count > 0;
    public CatalogEntityRef? Current => current;

    public CatalogBrowserView(IOnlineCatalog catalog, IEntityCatalog entities, IImagePipeline images, IRemoteCoverRegistry registry, Func<long> authGeneration, CatalogPresentationService presentations, Func<LibrarySnapshot?> readLibrary)
    {
        this.presentations = presentations; this.catalog = catalog; this.entities = entities; Images = images; this.registry = registry; this.authGeneration = authGeneration; this.readLibrary = readLibrary;
        RowSpacing = 16;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 12 }; heading.Children.Add(back); heading.Children.Add(title); Children.Add(heading);
        var status = new StackPanel { Spacing = 8 }; status.Children.Add(progress); status.Children.Add(message); SetRow(status, 1); Children.Add(status);
        scroll = new() { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        SetRow(scroll, 2); Children.Add(scroll);
        back.Click += async (_, _) => { if (!await GoBackAsync() && ReturnToSearch is not null) await ReturnToSearch(); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, "CatalogBrowser");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        // Preserve the live request if XAML delivers an old Unloaded after reattaching this page.
        Unloaded += (_, _) => { if (!IsLoaded) Cancel(); };
    }

    public Task OpenAsync(CatalogEntityRef entity, bool remember = true, WorkMetadata? loadedWork = null)
    {
        if (remember && current is not null && current != entity) { if (history.Count >= 32) history.Clear(); history.Push(current); }
        return LoadAsync(entity, loadedWork);
    }

    public async Task<bool> GoBackAsync()
    {
        if (!history.TryPop(out var previous)) return false;
        await LoadAsync(previous, null); return true;
    }
    public void Reset() { Cancel(); history.Clear(); current = null; body.Children.Clear(); }
    public void Cancel() { generation++; stop?.Cancel(); hero?.ReleaseBeforeLeave(); hero = null; progress.Visibility = Visibility.Collapsed; }

    private async Task LoadAsync(CatalogEntityRef entity, WorkMetadata? loadedWork, bool refresh = false)
    {
        Cancel(); stop?.Dispose(); stop = new(); var cancellation = stop.Token; var version = ++generation; var auth = authGeneration();
        current = entity; title.Text = CatalogLanguage.Name(entity); title.Visibility = Visibility.Visible; back.Visibility = CanGoBack || ReturnToSearch is not null ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Clear(); seenWorks.Clear(); seenCursors.Clear(); shelfWorkIds.Clear(); cursor = null; worksGrid = null; shelfGrid = null; shelfMembership = null; more = null;
        message.Text = UiText.Get("正在读取资料…"); progress.Visibility = Visibility.Visible; scroll.ChangeView(null, 0, null);
        bool Accept() => version == generation && !cancellation.IsCancellationRequested && auth == authGeneration();
        try
        {
            if (entity.Kind == CatalogEntityKind.Work)
            {
                // Search/list projections are not full details and may never overwrite the prepared cache.
                var cached = await presentations.GetAsync(entity.Id, refresh, cancellation);
                var work = CatalogLanguage.Project(cached.Work);
                if (!Accept()) return;
                current = new(CatalogEntityKind.Work, work.Id, work.Title); title.Text = work.Title;
                var banner = new BannerView(Images, registry) { Browse = e => OpenAsync(e) }; hero = banner; banner.SetPolicy(stageAllowed, stageContrast, stageMode); banner.SetPresentation(cached); title.Visibility = Visibility.Collapsed;
                if (AddWishlist is not null) { var add = new Button { Content = UiText.Get("加入愿望单") }; add.Click += async (_, _) => { if (auth != authGeneration()) { message.Text = UiText.Get("凭据已改变，请重新读取。"); return; } await AddWishlist(work.Id); }; body.Children.Add(add); }
                body.Children.Add(banner);
                var content = Content();
                workContent = content; content.Sources = SourceLinks(work, banner.Choice); content.SetWork(work, work.Title, collapseFacts: true); body.Children.Add(content);
                message.Text = UiText.Get("在线资料 · 未自动添加到书架");
                if (CatalogPresentationService.NeedsPreparation(cached)) { message.Text += UiText.Get(" · 图片正在后台准备"); presentations.Queue([work.Id],priority:true); }
            }
            else
            {
                var detail = await entities.EntityAsync(entity.Kind, entity.Id, cancellation); if (!Accept()) return;
                current = detail.Entity; title.Text = CatalogLanguage.Name(detail.Entity);
                if (detail.Entity.ImageUrl is { } url) body.Children.Add(new CoverPresenter { SourceKey = registry.Register(url, "entity"), Width = 180, Height = 220, PreserveEntireImage = true, HorizontalAlignment = HorizontalAlignment.Left });
                if (!detail.Facts.IsEmpty) body.Children.Add(new TextBlock { Text = string.Join("\n", detail.Facts), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (detail.Description.Length > 0) body.Children.Add(new MarkdownView { Text = CatalogLanguage.Select(detail.Introductions)?.Value ?? detail.Description, Images = Images, Registry = registry, OpenLink = OpenLinkAsync });
                if (!detail.Related.IsEmpty) { var links = new TagFlowPanel(); foreach (var item in detail.Related) links.Children.Add(item.Kind == CatalogEntityKind.Trait ? CatalogTagChip.Create(CatalogLanguage.Name(item), item, e => OpenAsync(e)) : EntityLink(item)); body.Children.Add(links); }
                if (entity.Kind == CatalogEntityKind.Trait)
                {
                    body.Children.Add(new TextBlock { Text = UiText.Get("相关角色"), FontSize = 22 }); worksGrid = new(); body.Children.Add(worksGrid);
                    more = new Button { Content = UiText.Get("加载下一页"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left }; more.Click += async (_, _) => await LoadMoreAsync(); body.Children.Add(more);
                    await AppendCharactersAsync(detail.Entity, null, version, auth, cancellation);
                }
                else if (entity.Kind != CatalogEntityKind.Person)
                {
                    if (entity.Kind == CatalogEntityKind.Tag)
                    {
                        var snapshot = readLibrary();
                        if (snapshot is null || snapshot.Access == LibraryAccess.InvalidInput)
                        { Failure(UiText.Get("书架尚未成功读取，暂时无法区分相关作品。请在书架加载后重试。"), entity); return; }
                        shelfMembership = new(snapshot); shelfGrid = new(); worksGrid = new();
                        shelfHeading = new() { FontSize = 22 }; outsideHeading = new() { FontSize = 22 };
                        shelfEmpty = new() { TextWrapping = TextWrapping.Wrap }; outsideEmpty = new() { TextWrapping = TextWrapping.Wrap };
                        var owned = new StackPanel { Spacing = 12 }; owned.Children.Add(shelfHeading); owned.Children.Add(shelfEmpty); owned.Children.Add(shelfGrid);
                        var outside = new StackPanel { Spacing = 12 }; outside.Children.Add(outsideHeading); outside.Children.Add(outsideEmpty); outside.Children.Add(worksGrid);
                        body.Children.Add(owned); body.Children.Add(outside); UpdateShelfGroups();
                    }
                    else { body.Children.Add(new TextBlock { Text = UiText.Get("相关作品"), FontSize = 22 }); worksGrid = new(); body.Children.Add(worksGrid); }
                    more = new Button { Content = UiText.Get("加载下一页"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left }; more.Click += async (_, _) => await LoadMoreAsync(); body.Children.Add(more);
                    await AppendWorksAsync(detail.Entity, null, version, auth, cancellation);
                }
                if (Accept()) message.Text = entity.Kind == CatalogEntityKind.Tag ? UiText.Format($"已加载 {seenWorks.Count} 部相关作品") : UiText.Get("目录资料");
            }
        }
        catch (OperationCanceledException) { if (version == generation) message.Text = UiText.Get("读取已取消。"); }
        catch (OnlineException e) { if (Accept()) Failure(e.Message, entity); }
        catch (Exception e) { if (Accept()) { App.TraceVisualFailure(e); Failure(UiText.Get("资料未能读取，请重试。"), entity); } }
        finally { if (version == generation) { progress.Visibility = Visibility.Collapsed; if (auth != authGeneration()) { body.Children.Clear(); message.Text = UiText.Get("凭据已改变，请重新读取资料。"); } } }
    }

    internal async void RefreshPrepared(string id)
    {
        if (current is not { Kind: CatalogEntityKind.Work } work || work.Id != id || hero is null || stop is null) return;
        var version = generation; var auth = authGeneration(); var token = stop.Token;
        try {
            var cached = await presentations.GetAsync(id, cancellation: token);
            if (version != generation || token.IsCancellationRequested || auth != authGeneration()) return;
            hero.SetPresentation(cached);
            if (workContent is { } content) { content.Sources = SourceLinks(cached.Work, cached.Banner); content.SetWork(cached.Work, cached.Work.Title, collapseFacts: true); }
            message.Text = cached.Prepared ? UiText.Get("在线资料 · 未自动添加到书架") : UiText.Get("图片准备未完成，可通过来源中的刷新重试");
        } catch (OperationCanceledException) { } catch (Exception e) { App.TraceVisualFailure(e); }
    }
    private void Failure(string text, CatalogEntityRef entity) { message.Text = text; var retry = new Button { Content = UiText.Get("重试"), HorizontalAlignment = HorizontalAlignment.Left }; retry.Click += async (_, _) => await LoadAsync(entity, null); body.Children.Add(retry); }
    private CatalogContentView Content() => new() { Images = Images, Registry = registry, Browse = e => OpenAsync(e), OpenLink = OpenLinkAsync };
    private HyperlinkButton EntityLink(CatalogEntityRef entity) { var button = new HyperlinkButton { Content = CatalogLanguage.Name(entity), Padding = new Thickness(0, 0, 12, 0) }; button.Click += async (_, _) => await OpenAsync(entity); return button; }
    private UIElement SourceLinks(WorkMetadata work, BannerChoice? artwork)
    {
        var source = new StackPanel { Spacing = 8 };
        var refresh = new Button { Content = UiText.Get("刷新资料与图片"), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await LoadAsync(new(CatalogEntityKind.Work, work.Id, work.Title), null, true);
        source.Children.Add(refresh);
        if (artwork?.ReferenceTitle is { } title) source.Children.Add(new TextBlock { Text = UiText.Get("横幅 CG · ") + title + UiText.Get("（关联版本）"), TextWrapping = TextWrapping.Wrap });
        var row = new TagFlowPanel();
        foreach (var link in CandidateDetailLinks.From(work)) { var b = new HyperlinkButton { Content = link.Title, Padding = new Thickness(0, 0, 16, 0) }; b.Click += async (_, _) => await OpenSourceAsync(link.Url); row.Children.Add(b); }
        source.Children.Add(row);
        return new Expander { Header = UiText.Get("资料与图片来源"), Content = source, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = false };
    }
    private async Task OpenSourceAsync(string url)
    {
        if (!MarkdownContent.AllowedLink(url)) return;
        try { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(url))) message.Text = UiText.Get("链接未能打开。"); }
        catch { message.Text = UiText.Get("链接未能打开。"); }
    }
    public async Task OpenLinkAsync(string url)
    {
        if (!MarkdownContent.AllowedLink(url)) return;
        if (KunGalNavigation.TryEntity(url, out var entity)) { await OpenAsync(entity); return; }
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); } catch { message.Text = UiText.Get("链接未能打开。"); }
    }
    private async Task LoadMoreAsync()
    {
        if (current is null || cursor is null || stop is null || more is null || !more.IsEnabled) return;
        more.IsEnabled = false; var version = generation; var auth = authGeneration(); var cancellation = stop.Token;
        try { if (current.Kind == CatalogEntityKind.Trait) await AppendCharactersAsync(current, cursor, version, auth, cancellation); else await AppendWorksAsync(current, cursor, version, auth, cancellation); }
        catch (OperationCanceledException) { }
        catch (OnlineException e) { if (version == generation && auth == authGeneration()) message.Text = e.Message; }
        catch (Exception) { if (version == generation) message.Text = UiText.Get("更多作品未能读取，请重试。"); }
        finally { if (version == generation && more is not null) more.IsEnabled = true; }
    }
    private async Task AppendWorksAsync(CatalogEntityRef entity, string? next, long version, long auth, CancellationToken cancellation)
    {
        var page = await entities.EntityWorksAsync(entity.Kind, entity.Id, next, cancellation);
        if (version != generation || cancellation.IsCancellationRequested || auth != authGeneration() || worksGrid is null) return;
        if (next is not null) seenCursors.Add(next);
        foreach (var work in page.Works.Select(CatalogLanguage.Project).Where(w => seenWorks.Count < 500 && seenWorks.Add(w.Id)))
        {
            var card = new StackPanel { Width = 150, Spacing = 6 }; card.Children.Add(new CoverPresenter { SourceKey = registry.Register(work.CoverUrl, work.FetchedAt), Width = 150, Height = 202 }); card.Children.Add(new TextBlock { Text = work.Title, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
            var button = new Button { Content = card, Padding = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 16, 20) }; button.Click += async (_, _) => await OpenAsync(new(CatalogEntityKind.Work, work.Id, work.Title));
            if (shelfGrid is not null && shelfMembership?.Contains(work) == true) { shelfWorkIds.Add(work.Id); shelfGrid.Children.Add(button); }
            else worksGrid.Children.Add(button);
        }
        cursor = page.NextCursor is { } candidate && !seenCursors.Contains(candidate) && seenWorks.Count < 500 ? candidate : null;
        if (more is not null) more.Visibility = cursor is null ? Visibility.Collapsed : Visibility.Visible;
        if (shelfGrid is not null) { UpdateShelfGroups(); message.Text = UiText.Format($"已加载 {seenWorks.Count} 部相关作品"); }
        else if (seenWorks.Count == 0) body.Children.Add(new TextBlock { Text = UiText.Get("此项尚未收录关联作品。") });
    }
    private void UpdateShelfGroups()
    {
        if (shelfGrid is null || worksGrid is null) return;
        shelfHeading.Text = UiText.Format($"已在书架 · {shelfGrid.Children.Count}");
        outsideHeading.Text = UiText.Format($"未在书架 · {worksGrid.Children.Count}");
        shelfEmpty.Text = cursor is null ? UiText.Get("已加载的相关作品中没有已入库作品。") : UiText.Get("当前已加载结果中没有已入库作品，可继续加载。");
        outsideEmpty.Text = cursor is null ? UiText.Get("已加载的相关作品均已在书架。") : UiText.Get("当前已加载结果均已在书架，可继续加载。");
        shelfEmpty.Visibility = shelfGrid.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        outsideEmpty.Visibility = worksGrid.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task AppendCharactersAsync(CatalogEntityRef entity, string? next, long version, long auth, CancellationToken cancellation)
    {
        var page = await entities.TraitCharactersAsync(entity.Id, next, cancellation);
        if (version != generation || cancellation.IsCancellationRequested || auth != authGeneration() || worksGrid is null) return;
        if (next is not null) seenCursors.Add(next);
        foreach (var character in page.Items.Select(CatalogLanguage.Project).Where(c => seenWorks.Count < 500 && seenWorks.Add(c.Id)))
        {
            var card = new StackPanel { Width = 122, Spacing = 6 };
            card.Children.Add(new CoverPresenter { SourceKey = registry.Register(character.ImageUrl, "character"), Width = 122, Height = 146, PreserveEntireImage = true });
            card.Children.Add(new TextBlock { Text = character.Name, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
            var button = new Button { Content = card, Padding = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 12, 16) };
            button.Click += async (_, _) => await OpenAsync(character); worksGrid.Children.Add(button);
        }
        cursor = page.NextCursor is { } candidate && !seenCursors.Contains(candidate) && seenWorks.Count < 500 ? candidate : null;
        if (more is not null) more.Visibility = cursor is null ? Visibility.Collapsed : Visibility.Visible;
        if (seenWorks.Count == 0) body.Children.Add(new TextBlock { Text = UiText.Get("此项尚未收录关联角色。") });
    }

}
