using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;
public sealed partial class DetailsPage
{
    private IOnlineCatalog? catalog;
    private CatalogPresentationService? presentations;
    private IPersonalBannerStore? personalBanners;
    private IEntityCatalog? entities;
    private IRemoteCoverRegistry? registry;
    private Func<long>? authGeneration;
    private Func<LibrarySnapshot?>? bangumiLibrary;
    private CancellationTokenSource? catalogStop;
    private long catalogGeneration;
    private string? catalogIdentity;
    private long catalogDataMilliseconds;
    private bool catalogPrepared;
    private bool catalogMediaPrepared;
    private Control? browserReturnFocus;
    private CatalogBrowserView? browser;
    private RelatedMediaDetailView? relatedMedia;
    private Control? relatedReturnFocus;
    public void ConfigureCatalog(IOnlineCatalog catalog, IEntityCatalog entities, IRemoteCoverRegistry registry, Func<long> authGeneration, CatalogPresentationService presentations, Func<LibrarySnapshot?> readLibrary, IPersonalBannerStore? personalBanners = null, IRelatedGameIdentityReader? relatedIdentities = null)
    {
        this.presentations = presentations; this.catalog = catalog; this.entities = entities; this.registry = registry; this.authGeneration = authGeneration;
        this.personalBanners = personalBanners;
        BangumiRelated.ConfigureGameIdentities(relatedIdentities);
        bangumiLibrary = readLibrary;
        HeroFacts.Browse = OpenCatalogAsync;
        RichCatalog.ShowRelatedWorks = false;
        RichCatalog.Images = Images; RichCatalog.Registry = registry; RichCatalog.Browse = OpenCatalogAsync; RichCatalog.OpenLink = OpenCatalogLinkAsync;
        PersonalNotesText.Images = Images; PersonalNotesText.Registry = registry; PersonalNotesText.OpenLink = OpenCatalogLinkAsync;
        (ReviewHost.Content as RecognitionReview)?.ConfigureCatalog(registry, OpenCatalogLinkAsync);
        HeroStage.AdaptiveLandscape = true;
        browser = new(catalog, entities, Images, registry, () => this.authGeneration?.Invoke() ?? 0, presentations, readLibrary) { ReturnToSearch = () => { CloseCatalogBrowser(); return Task.CompletedTask; } };
        CatalogHost.Content = browser;
        browserRelatedIdentities = relatedIdentities;
    }
    private IRelatedGameIdentityReader? browserRelatedIdentities;
    private void SynchronizeCatalog(GameDetails? detail)
    {
        var identity = detail is null ? null : detail.Id + "/" + detail.Metadata?.Id + "/" + detail.Metadata?.FetchedAt;
        if (catalogIdentity == identity && catalogStop is not null && !catalogStop.IsCancellationRequested) return;
        CancelCatalog(); catalogIdentity = identity;
        BangumiRelated.SetCatalog(detail?.Metadata);
        heroPresentation.SetPending(true);
        if(presentations is null||detail?.Metadata is null)RichCatalog.SetWork(detail?.Metadata, detail?.Personal.Name, collapseFacts: true);
        else RichCatalog.SetWork(detail.Metadata, detail.Personal.Name, collapseFacts: true);
        SetHeroArtwork(null);
        CatalogReadStatus.Text = ""; RetryCatalog.Visibility = Visibility.Collapsed;
        CatalogStatusRow.Visibility = Visibility.Collapsed;
        if (detail?.Metadata is { } work && presentations is not null && registry is not null && IsLoaded) _ = LoadCatalogAsync(work);
        else if (detail is not null && personalBanners is not null && IsLoaded) _ = LoadPersonalBannerOnlyAsync(detail.Id);
        else heroPresentation.SetPending(false);
    }
    internal void RefreshPersonalBanner(string gameId)
    {
        if (!IsLoaded || ViewModel.Draft?.Source.Id != gameId) return;
        catalogIdentity = null; SynchronizeCatalog(ViewModel.Draft.Source);
    }
    private async Task<string?> ReadPersonalBannerAsync(string? gameId, CancellationToken token)
    {
        if (personalBanners is null || gameId is null) return null;
        try { return await personalBanners.GetAsync(gameId, token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException)
        { App.TraceVisualFailure(e); return null; }
    }
    private async Task LoadPersonalBannerOnlyAsync(string gameId)
    {
        catalogStop?.Dispose(); catalogStop = new(); var token = catalogStop.Token; var generation = ++catalogGeneration;
        bool Accept() => IsLoaded && generation == catalogGeneration && !token.IsCancellationRequested && ViewModel.Draft?.Source.Id == gameId;
        try
        {
            var key = await ReadPersonalBannerAsync(gameId, token);
            if (Accept())
                SetHeroArtwork(key is null ? null : new(key, "", 1, "local", gameId));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Accept()) App.TraceVisualFailure(e); }
        finally { if (Accept()) heroPresentation.SetPending(false); }
    }
    public void CancelCatalog() { catalogGeneration++; catalogStop?.Cancel(); CloseRelatedMedia(false); CloseCatalogBrowser(); BangumiRelated.CancelPending(); }
    internal void ResetBrowsing() { CloseRelatedMedia(false); CloseCatalogBrowser(); }
    private void CloseCatalogBrowser()
    {
        browser?.Reset();
        if (CatalogHost.Visibility != Visibility.Visible) return;
        CatalogHost.Visibility = Visibility.Collapsed; DetailsRoot.Visibility = Visibility.Visible;
        SynchronizeBangumi();
        BangumiRelated.ResumeNavigation();
        HeroStage.Reattach();
        BangumiRelated.RestoreNavigationFocus();
        var focus = browserReturnFocus; browserReturnFocus = null;
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded && CatalogHost.Visibility != Visibility.Visible && focus?.IsLoaded == true) focus.Focus(FocusState.Programmatic); });
    }
    internal async Task<bool> GoBackCatalogAsync()
    {
        if (RelatedMediaHost.Visibility == Visibility.Visible) { if (relatedMedia?.GoBackMedia() != true) CloseRelatedMedia(true); return true; }
        if (CatalogHost.Visibility != Visibility.Visible || browser is null) return false;
        if (!await browser.GoBackAsync()) CloseCatalogBrowser();
        return true;
    }
    private async void OnRetryCatalog(object sender, RoutedEventArgs e) { if (ViewModel.Draft?.Source.Metadata is { } work) await LoadCatalogAsync(work, true); }
    internal void RefreshPrepared(string id)
    {
        browser?.RefreshPrepared(id);
        if (IsLoaded && ViewModel.Draft?.Source.Metadata is { } work && work.Id == id) _ = LoadCatalogAsync(work);
    }
    private async Task LoadCatalogAsync(WorkMetadata saved, bool refresh = false)
    {
        if (presentations is null || registry is null) return;
        catalogStop?.Cancel(); catalogStop?.Dispose(); catalogStop = new(); var token = catalogStop.Token; var generation = ++catalogGeneration; var auth = authGeneration?.Invoke();
        var gameId = ViewModel.Draft?.Source.Id;
        bool Accept() => IsLoaded && generation == catalogGeneration && !token.IsCancellationRequested && auth == authGeneration?.Invoke() && ViewModel.Draft?.Source.Id == gameId && ViewModel.Draft?.Source.Metadata?.Id == saved.Id;
        CatalogReadStatus.Text = UiText.Get("正在补充目录资料…"); RetryCatalog.Visibility = Visibility.Collapsed;
        CatalogStatusRow.Visibility = Visibility.Visible;
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var personalKey = await ReadPersonalBannerAsync(gameId, token); if (!Accept()) return;
            if (personalKey is not null) SetHeroArtwork(new(personalKey, "", 1, "local", saved.Id));
            var cached = await presentations.GetAsync(saved.Id, refresh, token); if (!Accept()) return;
            if(CatalogPresentationService.NeedsPreparation(cached))presentations.Queue([saved.Id],priority:true);
            catalogDataMilliseconds = timer.ElapsedMilliseconds; catalogPrepared = cached.Prepared;catalogMediaPrepared=cached.MediaPrepared;
            var work = cached.Work;
            RichCatalog.SetWork(work, ViewModel.Draft?.Source.Personal.Name, collapseFacts: true);
            BangumiRelated.SetCatalog(work);
            HeroFacts.SetWork(work, ViewModel.Draft?.Source.Personal.Name);
            UpdateHeroFrame(HeroFrame.ActualWidth);
            SourceLinks.ItemsSource = CandidateDetailLinks.From(work).Select(l => new DetailSourceLink(l.Title, l.Url)).ToArray();
            CatalogReadStatus.Text = work.Presentation?.IntroSource is "kungal" or "curated" ? UiText.Get("KunGal 目录资料") : UiText.Get("KunGal 目录 · ") + CatalogContentView.SiteName(work.Presentation?.IntroSource ?? "") + UiText.Get(" 资料");
            if (!cached.Prepared) CatalogReadStatus.Text += UiText.Get(" · 图片正在后台准备");
            CatalogStatusRow.Visibility = Visibility.Visible;
            var choice = personalKey is null ? cached.Banner : new BannerChoice(personalKey, "", 1, "local", saved.Id);
            SetHeroArtwork(choice);
            if (refresh) presentations.Queue([saved.Id]);
            // Provenance stays inside the collapsed sources section, never on the CG overlay.
            ArtworkSourceText.Text = string.Join("\n", new[] { ViewModel.Draft?.Source.Relationship, ViewModel.Draft?.Source.CoverDescription,
                choice is null ? UiText.Get("尚无合适横图；保留简洁背景") : choice.Kind == "local" ? UiText.Get("个人本地横幅 · 原图") : choice.ReferenceTitle is { } sourceTitle ? UiText.Get("横幅 CG · ") + sourceTitle + UiText.Get("（关联版本）") : UiText.Get("本版本横幅 CG") });
        }
        catch (OperationCanceledException) { }
        catch (OnlineException e) { if (Accept()) { CatalogReadStatus.Text = UiText.Get("已保留本地资料。") + e.Message; RetryCatalog.Visibility = Visibility.Visible; CatalogStatusRow.Visibility = Visibility.Visible; } }
        catch (Exception e) { if (Accept()) { App.TraceVisualFailure(e); CatalogReadStatus.Text = UiText.Get("已保留本地资料，在线补充未完成。"); RetryCatalog.Visibility = Visibility.Visible; CatalogStatusRow.Visibility = Visibility.Visible; } }
        finally { if (Accept()) heroPresentation.SetPending(false); }
    }
    private Task OpenCatalogAsync(CatalogEntityRef entity) => OpenCatalogWithRelationAsync(entity, null);
    private Task OpenRelatedCatalogAsync(RelatedWorkCard card) => card.Catalog is not { } entity ? Task.CompletedTask
        : OpenCatalogWithRelationAsync(entity, new CatalogIncomingRelation(ViewModel.Draft?.Source.Metadata?.Id ?? "",
            ViewModel.Draft?.Source.Personal.Name ?? "原作", card.Label, card.RawRelation, card.Evidence, card.Url));
    private async Task OpenCatalogWithRelationAsync(CatalogEntityRef entity, CatalogIncomingRelation? incoming)
    {
        if (browser is null || !IsLoaded) return;
        // The catalog replaces the main content area. The hidden game view retains its scroll
        // position and personal draft; browsing itself does not commit or discard anything.
        browserReturnFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Control;
        BangumiRelated.SuspendNavigation();
        HeroStage.ReleaseBeforeLeave(); DetailsRoot.Visibility = Visibility.Collapsed;
        CatalogHost.Visibility = Visibility.Visible;
        await browser.OpenAsync(entity, false, incoming: incoming);
    }
    private async Task OpenCatalogLinkAsync(string url)
    {
        if (KunGalNavigation.TryEntity(url, out var entity)) { await OpenCatalogAsync(entity); return; }
        if (MarkdownContent.AllowedLink(url)) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private Func<string> bangumiAccessScope = () => "anonymous";
    public void ConfigureBangumi(IBangumiRelationsReader reader, IBangumiSubjectDetailsReader subjectReader, IRelatedSemanticReader semantics, Func<string>? accessScope = null)
    {
        bangumiAccessScope = accessScope ?? (() => "anonymous");
        browser?.ConfigureRelatedWorks(reader, subjectReader, semantics, browserRelatedIdentities, bangumiAccessScope);
        relatedMedia = new();
        relatedMedia.Configure(subjectReader, Images, registry, OpenBangumiLinkAsync);
        RelatedMediaHost.Content = relatedMedia;
        BangumiRelated.OpenMedia = OpenRelatedMediaAsync;
        BangumiRelated.OpenCatalog = OpenRelatedCatalogAsync;
        BangumiRelated.ConfigureSemantics(semantics);
        BangumiRelated.RefreshContext = SynchronizeBangumi;
        BangumiRelated.Configure(reader, Images, registry, OpenBangumiLinkAsync,
            identity => IsLoaded && ViewModel.Page == WorkspacePage.Details && !ViewModel.IsDetailsLoading
                && CatalogHost.Visibility != Visibility.Visible && RelatedMediaHost.Visibility != Visibility.Visible && ReadBangumiIdentity(out _) == identity);
        SynchronizeBangumi();
    }
    public void ConfigureMusic(IMusicBrainzReader music) { relatedMedia?.ConfigureMusic(music); browser?.ConfigureMusic(music); }
    public void ConfigureAniList(IAniListReader reader) { relatedMedia?.ConfigureAniList(reader); browser?.ConfigureAniList(reader); }
    private async Task OpenRelatedMediaAsync(RelatedWorkCard card)
    {
        if (relatedMedia is null || card.Bangumi is not { } item || !IsLoaded || ViewModel.IsDetailsLoading) return;
        relatedReturnFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Control;
        BangumiRelated.SuspendNavigation(); HeroStage.ReleaseBeforeLeave();
        DetailsRoot.Visibility = Visibility.Collapsed; RelatedMediaHost.Visibility = Visibility.Visible;
        relatedMedia.RelationLabel = card.Label; relatedMedia.RelationEvidence = card.Evidence;
        await relatedMedia.OpenAsync(item, ViewModel.Draft?.Source.Personal.Name ?? "原作");
    }
    private void CloseRelatedMedia(bool restore)
    {
        relatedMedia?.Deactivate();
        if (RelatedMediaHost.Visibility != Visibility.Visible) return;
        RelatedMediaHost.Visibility = Visibility.Collapsed; DetailsRoot.Visibility = Visibility.Visible;
        var focus = relatedReturnFocus; relatedReturnFocus = null;
        if (!restore) return;
        SynchronizeBangumi(); BangumiRelated.ResumeNavigation(); HeroStage.Reattach();
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded && RelatedMediaHost.Visibility != Visibility.Visible && focus?.IsLoaded == true) focus.Focus(FocusState.Programmatic); });
        BangumiRelated.RestoreNavigationFocus();
    }
    private static async Task OpenBangumiLinkAsync(string url)
    {
        if (!BangumiRelationIdentityResolver.TryReferenceId(url, out var subjectId)
            || url != "https://bgm.tv/subject/" + subjectId
            || !await Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
            throw new InvalidOperationException("未能打开 Bangumi 页面。");
    }
    private BangumiRelationIdentity? ReadBangumiIdentity(out string reason)
    {
        reason = "请先打开一部书架作品。";
        if (ViewModel.Draft?.Source is not { } details) return null;
        var entry = bangumiLibrary?.Invoke()?.Entries.FirstOrDefault(e => e.Id == details.Id);
        if (entry is null) { reason = "当前作品已不在书架中，请返回书架后重新打开。"; return null; }
        if (entry.Metadata?.Id != details.Metadata?.Id)
        { reason = "作品资料已变化，请重新打开详情后读取。"; return null; }
        var lifecycle = details.Token.Epoch.ToString("N") + ":" + details.Token.Generation.ToString("N");
        return BangumiRelationIdentityResolver.TryCreate(entry, lifecycle, out var identity, out reason) ? identity! with { AccessScope = bangumiAccessScope() } : null;
    }
    private void SynchronizeBangumi()
    {
        if (!IsLoaded || ViewModel.IsDetailsLoading) { BangumiRelated.Deactivate(); return; }
        if (CatalogHost.Visibility == Visibility.Visible || RelatedMediaHost.Visibility == Visibility.Visible) return;
        var identity = ReadBangumiIdentity(out var reason);
        BangumiRelated.SetContext(identity, reason);
    }
    public void DisposeBangumi() { BangumiRelated.Dispose(); relatedMedia?.Dispose(); browser?.DisposeRelated(); }
    public void InvalidateBangumiAccess()
    {
        CloseRelatedMedia(false); browser?.InvalidateBangumiAccess();
        BangumiRelated.SetContext(null, "番组计划访问方式已变化，请重新打开作品。");
        if (IsLoaded && ViewModel.Page == WorkspacePage.Details) SynchronizeBangumi();
    }
    internal object BangumiEvidence => new { relations = BangumiRelated.Evidence, mediaVisible = RelatedMediaHost.Visibility == Visibility.Visible, media = relatedMedia?.Evidence, catalogVisible = CatalogHost.Visibility == Visibility.Visible, catalog = browser?.RelationEvidence, gameId = ViewModel.Draft?.Source.Id, gameScroll = DetailsScroll.VerticalOffset };
    internal async Task BangumiFixtureAsync(string action, BangumiRelationKind? kind = null, string? subjectId = null)
    {
        if (CatalogHost.Visibility == Visibility.Visible && browser is not null && action != "back")
        { await browser.RelatedFixtureAsync(action, kind, subjectId); return; }
        switch (action)
        {
            case "expand": SynchronizeBangumi(); await BangumiRelated.ExpandForFixtureAsync(); break;
            case "collapse": BangumiRelated.CollapseForFixture(); break;
            case "refresh": SynchronizeBangumi(); await BangumiRelated.RefreshForFixtureAsync(); break;
            case "cancel": BangumiRelated.CancelForFixture(); break;
            case "select" when kind is { } value: BangumiRelated.SelectForFixture(value); break;
            case "all": BangumiRelated.AllForFixture(); break;
            case "open" when subjectId is not null: await BangumiRelated.OpenForFixtureAsync(subjectId); break;
            case "openCatalog" when subjectId is not null: await BangumiRelated.OpenCatalogForFixtureAsync(subjectId); break;
            case "back": await GoBackCatalogAsync(); break;
            case "mediaCancel": relatedMedia?.CancelForFixture(); break;
            case "mediaRefresh": if (relatedMedia is not null) await relatedMedia.RefreshForFixtureAsync(); break;
            case "musicPreview": case "musicConfirm": case "musicEditions": case "musicTracks":
                if (RelatedMediaHost.Visibility == Visibility.Visible && relatedMedia is not null) await relatedMedia.MusicFixtureAsync(action, subjectId); break;
            case "aniPreview": case "aniConfirm": case "aniCharacters": case "aniStaff": case "aniRelations": case "aniExplore": case "aniMore":
                if (RelatedMediaHost.Visibility == Visibility.Visible && relatedMedia is not null) await relatedMedia.AniListFixtureAsync(action, subjectId); break;
            case "sources": BangumiRelated.SourcesForFixture(); break;
            case "rawEvidence": BangumiRelated.RawEvidenceForFixture(); break;
            case "show": BangumiRelated.StartBringIntoView(); break;
        }
    }
}
