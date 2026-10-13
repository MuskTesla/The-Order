using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// Reuses the readonly media host and shell back path. This page is instantiated once per host;
// no music requests happen until a Bangumi music card is opened.
public sealed class MusicBrainzAlbumView : Grid, IDisposable
{
    private readonly TextBlock title = Text(28), type = Text(14), variant = Text(16), artist = Text(16), label = Text(16);
    private readonly TextBlock relation = Text(16), status = Text(14), summary = Text(16), choiceReason = Text(13);
    private readonly TextBlock coverCredit = Text(14);
    private readonly CoverPresenter cover = new() { Width = 156, Height = 156, PreserveEntireImage = true, ImageCornerRadius = 8 };
    private readonly Grid header = new() { ColumnSpacing = 20 };
    private readonly ProgressRing ring = new() { Width = 18, Height = 18 };
    private readonly Button cancel = Button("取消读取"), retry = Button("重试"), editions = Button("选择其他发行版"), refresh = Button("刷新音乐资料");
    private readonly Button confirm = Button("确认使用此发行版"), chooseAgain = Button("重新选择"), showTracks = Button("查看曲目列表");
    private readonly TextBox searchQuery = new() { PlaceholderText = "专辑名，或 MusicBrainz 发行版 / 专辑组链接", MaxLength = 2000, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button search = Button("查找专辑");
    private readonly Grid searchRow = new() { ColumnSpacing = 8 };
    private readonly StackPanel candidates = new() { Spacing = 10 }, discs = new() { Spacing = 12 }, personnel = new() { Spacing = 8 };
    private readonly StackPanel candidateSection = new() { Spacing = 12 }, tracksSection = new() { Spacing = 12 }, summarySection = new() { Spacing = 12 };
    private readonly TagFlowPanel actions = new() { HorizontalSpacing = 8, VerticalSpacing = 8 }, confirmActions = new() { HorizontalSpacing = 8, VerticalSpacing = 8 };
    private readonly Expander credits = new() { Header = "制作人员", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Expander sources = new() { Header = "资料来源", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Expander raw = new() { Header = "关系说明与匹配依据", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel sourceBody = new() { Spacing = 8 }, rawBody = new() { Spacing = 8 };
    private readonly ScrollViewer scroll;
    private MusicAlbumViewModel? model;
    private IRemoteCoverRegistry? registry;
    private Func<string, Task>? bangumiLink;
    private MusicBrainzAlbum? shownAlbum;
    private MusicBrainzSearchResult? shownSearch;
    private string? coverKey;
    private string origin = "", relationLabel = "", relationEvidence = "";
    private long generation;
    private bool active, disposed;
    private bool startPending, startQueued, coverFallback;
    private string? requestedCover;
    public MusicBrainzAlbumView()
    {
        AutomationProperties.SetAutomationId(this, "MusicAlbumDetail");
        AutomationProperties.SetAutomationId(title, "MusicAlbumTitle");
        AutomationProperties.SetAutomationId(status, "MusicAlbumStatus"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(cancel, "MusicAlbumCancel"); AutomationProperties.SetAutomationId(retry, "MusicAlbumRetry");
        AutomationProperties.SetAutomationId(editions, "MusicAlbumEditions"); AutomationProperties.SetAutomationId(confirm, "MusicAlbumConfirm");
        AutomationProperties.SetAutomationId(searchQuery, "MusicAlbumSearchQuery"); AutomationProperties.SetAutomationId(search, "MusicAlbumSearch");
        AutomationProperties.SetAutomationId(showTracks, "MusicAlbumShowTracks"); AutomationProperties.SetAutomationId(tracksSection, "MusicAlbumTracks");
        title.FontWeight = FontWeights.SemiBold; type.Opacity = choiceReason.Opacity = .75; credits.Visibility = Visibility.Collapsed;
        confirm.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.Children.Add(cover);
        var info = new StackPanel { Spacing = 8, Children = { type, title, artist, variant, label, choiceReason } }; SetColumn(info, 1); header.Children.Add(info);
        actions.Children.Add(ring); actions.Children.Add(cancel); actions.Children.Add(retry); actions.Children.Add(showTracks); actions.Children.Add(editions);
        confirmActions.Children.Add(confirm); confirmActions.Children.Add(chooseAgain);
        searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchRow.Children.Add(searchQuery); SetColumn(search, 1); searchRow.Children.Add(search);
        candidateSection.Children.Add(Text(22, "选择发行版")); candidateSection.Children.Add(searchRow); candidateSection.Children.Add(candidates);
        tracksSection.Children.Add(Text(22, "曲目列表")); tracksSection.Children.Add(discs);
        credits.Content = personnel; summarySection.Children.Add(Text(22, "简介 · Bangumi")); summarySection.Children.Add(summary);
        raw.Content = rawBody; sourceBody.Children.Add(raw); sourceBody.Children.Add(refresh); sources.Content = sourceBody;
        var body = new StackPanel { Spacing = 20, Children = { header, relation, status, actions, confirmActions, candidateSection, tracksSection, credits, summarySection, sources } };
        scroll = new() { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(scroll);
        scroll.Loaded += (_, _) => QueueStart(); body.SizeChanged += (_, _) => QueueStart();
        scroll.PointerWheelChanged += (_, _) => startPending = false;
        scroll.PointerPressed += (_, _) => startPending = false;
        scroll.KeyDown += (_, _) => startPending = false;
        cancel.Click += (_, _) => model?.Cancel(); retry.Click += async (_, _) => await RefreshAsync(); refresh.Click += async (_, _) => await RefreshAsync();
        editions.Click += async (_, _) => { if (model is not null) await model.ChooseEditionAsync(); };
        confirm.Click += async (_, _) => { if (model is not null) await model.ConfirmAsync(); };
        chooseAgain.Click += (_, _) => model?.BackToEditions();
        showTracks.Click += (_, _) => tracksSection.StartBringIntoView();
        search.Click += async (_, _) => { if (model is not null) await model.SearchAsync(searchQuery.Text); };
        searchQuery.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && model?.CanLoad == true) { e.Handled = true; await model.SearchAsync(searchQuery.Text); } };
        SizeChanged += (_, _) => { var narrow = ActualWidth < 520; cover.Width = cover.Height = narrow ? 104 : 156; title.FontSize = narrow ? 24 : 28; header.ColumnSpacing = narrow ? 14 : 20; };
        Unloaded += (_, _) => { if (!IsLoaded) Deactivate(); };
    }
    public void Configure(IBangumiSubjectDetailsReader subjects, IMusicBrainzReader music, IImagePipeline images,
        IRemoteCoverRegistry? coverRegistry, Func<string, Task> openBangumi)
    {
        Deactivate(); if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); }
        registry = coverRegistry; cover.Pipeline = images; bangumiLink = openBangumi;
        model = new(subjects, music); model.PropertyChanged += StateChanged;
    }
    public Task OpenAsync(BangumiRelationItem item, string originTitle, string? labelText, string? evidence, CancellationToken cancellation)
    {
        active = true; generation++; startPending = true; origin = originTitle; relationLabel = labelText ?? item.Relation; relationEvidence = evidence ?? "";
        shownAlbum = null; shownSearch = null; coverKey = null; sources.IsExpanded = raw.IsExpanded = credits.IsExpanded = false;
        candidates.Children.Clear(); discs.Children.Clear(); personnel.Children.Clear(); credits.Visibility = Visibility.Collapsed; cover.SourceKey = null;
        requestedCover = null; coverFallback = false;
        searchQuery.Text = "";
        scroll.ChangeView(null, 0, null); return model?.OpenAsync(item, cancellation) ?? Task.CompletedTask;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs e) { if (active && !disposed) Render(); }
    private void Render()
    {
        var album = model?.Displayed?.Album; var source = model?.SourceDetails?.Details; var item = model?.Item;
        title.Text = album?.Release.Title ?? source?.Title ?? item?.Title ?? "音乐";
        type.Text = album is null ? "音乐 · Bangumi" : (album.GroupType switch { "Album" => "专辑", "Single" => "单曲发行", "EP" => "EP", _ => "音乐发行" }) + " · MusicBrainz";
        artist.Text = album?.Release.Artist is { Length: > 0 } performer ? performer == "Various Artists" ? "Various Artists（合辑）" : performer
            : string.Join(" / ", source?.MusicFacts?.Artists ?? []);
        variant.Text = album is null ? source?.ReleaseDate ?? "" : string.Join(" · ", new[] { album.Release.Date, Country(album.Release.Country),
            album.Discs.Length > 0 ? album.Discs.Length + " 碟 · " + album.Discs.Sum(x => x.Tracks.Length) + " 首" : "暂无曲目资料" }.Where(x => !string.IsNullOrWhiteSpace(x)));
        label.Text = album is null ? string.Join(" / ", source?.MusicFacts?.CatalogNumbers ?? []) : string.Join(" · ", new[]
        { string.Join(" / ", album.Release.Labels.Select(x => x.Name).Where(x => x.Length > 0).Distinct()),
            string.Join(" / ", album.Release.Labels.Select(x => x.CatalogNumber).Where(x => x is not null).Distinct()) }.Where(x => x.Length > 0));
        choiceReason.Text = model?.State == MusicAlbumState.Preview ? "发行版预览 · 尚未确认" : model?.SelectionReason ?? "";
        foreach (var text in new[] { artist, variant, label, choiceReason }) text.Visibility = Visible(text.Text.Length > 0);
        relation.Text = origin.Length == 0 ? relationLabel : $"与《{origin}》的关联 · {relationLabel}";
        status.Text = model?.Message ?? ""; status.Visibility = Visible(status.Text.Length > 0);
        ring.IsActive = model?.IsBusy == true; ring.Visibility = Visible(ring.IsActive);
        cancel.Visibility = Visible(model?.CanCancel == true); retry.Visibility = Visible(model?.State is MusicAlbumState.Failed or MusicAlbumState.Cancelled);
        retry.IsEnabled = refresh.IsEnabled = model?.CanLoad == true;
        editions.Visibility = Visible(album is not null && model?.State != MusicAlbumState.Choosing); editions.IsEnabled = model?.CanLoad == true;
        showTracks.Visibility = Visible(album?.Discs.Any(x => x.Tracks.Length > 0) == true);
        actions.Visibility = Visible(actions.Children.OfType<FrameworkElement>().Any(x => x.Visibility == Visibility.Visible));
        confirmActions.Visibility = Visible(model?.State == MusicAlbumState.Preview); confirm.IsEnabled = model?.CanConfirm == true;
        candidateSection.Visibility = Visible(model?.State == MusicAlbumState.Choosing || model?.State == MusicAlbumState.Failed && source is not null);
        search.IsEnabled = searchQuery.IsEnabled = model?.CanLoad == true;
        if (!ReferenceEquals(shownSearch, model?.Match?.Search)) { shownSearch = model?.Match?.Search; RenderCandidates(); }
        tracksSection.Visibility = Visible(album?.Discs.Length > 0);
        if (!ReferenceEquals(shownAlbum, album)) { shownAlbum = album; RenderDiscs(album); }
        summary.Text = source?.Summary ?? ""; summarySection.Visibility = Visible(summary.Text.Length > 0);
        RenderSources(album, item, source);
        SetCover(album?.CoverUrl ?? source?.CoverUrl ?? item?.CoverUrl, source?.CoverUrl ?? item?.CoverUrl);
        QueueStart();
    }
    private void QueueStart()
    {
        if (!active || disposed || !startPending || startQueued) return;
        startQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            startQueued = false;
            if (!active || disposed || !startPending || !scroll.IsLoaded || scroll.ActualHeight <= 0) return;
            scroll.UpdateLayout();
            if (scroll.ChangeView(null, 0, null, true)) startPending = false;
        });
    }
    private void RenderCandidates()
    {
        candidates.Children.Clear();
        if (shownSearch is null || model?.Source is not { } bridge) return;
        foreach (var candidate in shownSearch.Candidates)
        {
            var evidence = MusicBrainzMatching.Assess(bridge, candidate);
            var card = new StackPanel { Spacing = 6 };
            var name = Text(18, candidate.Title); name.FontWeight = FontWeights.SemiBold; name.MaxLines = 2; name.TextTrimming = TextTrimming.CharacterEllipsis;
            card.Children.Add(name); card.Children.Add(Text(14, string.Join(" · ", new[] { candidate.Date, Country(candidate.Country), candidate.DiscCount is { } n ? n + " 碟" : null,
                candidate.TrackCount is { } t ? t + " 首" : null }.Where(x => !string.IsNullOrWhiteSpace(x)))));
            var catalogues = string.Join(" / ", candidate.Labels.Select(x => x.CatalogNumber).Where(x => x is not null).Distinct());
            if (catalogues.Length > 0) card.Children.Add(Text(14, "目录编号 · " + catalogues));
            if (candidate.Artist.Length > 0) card.Children.Add(Text(14, candidate.Artist));
            if (candidate.Status.Length > 0) card.Children.Add(Text(13, candidate.Status switch
            { "Official" => "正式发行", "Pseudo-Release" => "曲目别名版本", "Bootleg" => "非官方发行", _ => candidate.Status }));
            card.Children.Add(Text(13, string.Join(" · ", evidence.Evidence)));
            var preview = Button("查看此发行版与曲目"); AutomationProperties.SetAutomationId(preview, "MusicEdition-" + candidate.Id);
            preview.Click += async (_, _) => { if (model is not null) await model.PreviewAsync(candidate.Id); }; card.Children.Add(preview);
            candidates.Children.Add(new Border { CornerRadius = new(8), Padding = new(16), Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = card });
        }
    }
    private void RenderDiscs(MusicBrainzAlbum? album)
    {
        discs.Children.Clear(); personnel.Children.Clear(); credits.Visibility = Visibility.Collapsed;
        if (album is null) return;
        foreach (var credit in album.Credits) personnel.Children.Add(Text(14, Credit(credit)));
        credits.Visibility = Visible(personnel.Children.Count > 0);
        foreach (var disc in album.Discs)
        {
            var expander = new Expander { Header = $"碟 {disc.Position}" + (disc.Title.Length > 0 ? " · " + disc.Title : "")
                + $" · {disc.Format} · {disc.Tracks.Length} 首", IsExpanded = disc.Position == 1,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(expander, "MusicDisc-" + disc.Position);
            bool populated = false;
            void Populate()
            {
                if (populated) return; populated = true;
                // The page owns the only scrolling viewport. EffectiveViewport-based realization
                // keeps long discs bounded without an inner list scrollbar or trapped wheel input.
                var list = new ItemsRepeater { Layout = new StackLayout(), HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = disc.Tracks.Select(x => new MusicTrackRow(x)).ToArray(), ItemTemplate = (DataTemplate)XamlReader.Load(TrackTemplate) };
                AutomationProperties.SetAutomationId(list, "MusicDiscTracks-" + disc.Position); expander.Content = list;
            }
            expander.Expanding += (_, _) => Populate(); if (expander.IsExpanded) Populate(); discs.Children.Add(expander);
        }
    }
    private void RenderSources(MusicBrainzAlbum? album, BangumiRelationItem? item, BangumiSubjectDetails? source)
    {
        sourceBody.Children.Clear(); rawBody.Children.Clear();
        sourceBody.Children.Add(Text(14, "作品关系与简介 · Bangumi"));
        if (item is not null) sourceBody.Children.Add(Link("Bangumi 原条目", item.Url, isMusic: false));
        if (album is not null)
        {
            sourceBody.Children.Add(Text(14, "发行版、曲目与制作人员 · MusicBrainz"));
            sourceBody.Children.Add(Link("MusicBrainz 发行版", album.Url, isMusic: true));
            if (album.Release.ReleaseGroupId is { } group) sourceBody.Children.Add(Link("MusicBrainz 专辑组", new MusicBrainzReference(MusicBrainzEntityKind.ReleaseGroup, group).Url, isMusic: true));
            coverCredit.Text = album.CoverUrl is not null && !coverFallback ? "封面 · Cover Art Archive" : "封面 · 沿用 Bangumi";
            sourceBody.Children.Add(coverCredit);
        }
        rawBody.Children.Add(Text(14, "Bangumi 原始关系 · " + (item?.Relation ?? "未标明")));
        if (relationEvidence.Length > 0) rawBody.Children.Add(Text(14, relationEvidence));
        if (source?.MusicFacts is { } facts)
        {
            if (facts.CatalogNumbers.Length > 0) rawBody.Children.Add(Text(14, "Bangumi 目录编号 · " + string.Join(" / ", facts.CatalogNumbers)));
            foreach (var reference in facts.ExternalIds) rawBody.Children.Add(Link("来源提供的 MusicBrainz " + reference.Kind, reference.Url, true));
        }
        if (album is not null && model?.Source is { } bridge) rawBody.Children.Add(Text(14, string.Join(" · ", MusicBrainzMatching.Assess(bridge, album.Release).Evidence)));
        sourceBody.Children.Add(raw); sourceBody.Children.Add(refresh);
    }
    private HyperlinkButton Link(string name, string url, bool isMusic)
    {
        var link = new HyperlinkButton { Content = name, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += async (_, _) =>
        {
            try
            {
                if (isMusic)
                { if (!MusicBrainzReference.TryParse(url, out _) || !await Windows.System.Launcher.LaunchUriAsync(new Uri(url))) throw new InvalidOperationException(); }
                else if (bangumiLink is not null) await bangumiLink(url);
            }
            catch { if (active && !disposed) { status.Text = "未能打开来源网页，请稍后重试。"; status.Visibility = Visibility.Visible; } }
        }; return link;
    }
    private void SetCover(string? url, string? fallback)
    {
        if (requestedCover == url && coverKey is not null) return;
        requestedCover = url; coverFallback = false;
        var key = registry?.Register(url, "musicbrainz-album-v1"); if (coverKey == key) return;
        coverKey = key; cover.SourceKey = key; var version = ++generation;
        if (url is null || url == fallback) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await cover.PendingImage;
            if (!disposed && active && generation == version && !cover.HasImage && cover.SourceStatus is not ("loading" or "not-started"))
            {
                coverFallback = true; coverCredit.Text = "封面 · 沿用 Bangumi";
                coverKey = registry?.Register(fallback, "bangumi-music-fallback"); cover.SourceKey = coverKey;
            }
        });
    }
    private static string? Country(string? value) => value switch { "JP" => "日本", "US" => "美国", "DE" => "德国", "XW" => "全球", _ => value };
    internal static string Credit(MusicBrainzCredit value)
    {
        var role = value.Role switch { "composer" => "作曲", "lyricist" => "作词", "arranger" => "编曲", "producer" => "制作", "engineer" => "工程", "mix" => "混音", "mastering" => "母带", "vocal" => "演唱", "instrument" => "演奏", "conductor" => "指挥", _ => value.Role };
        return role + " · " + value.Name;
    }
    private static TextBlock Text(double size, string value = "") => new() { FontSize = size, Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Button(string value) => new() { Content = value, MinHeight = 32, CornerRadius = new(6), HorizontalAlignment = HorizontalAlignment.Left };
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    internal Task RefreshAsync() => model?.RefreshAsync() ?? Task.CompletedTask;
    internal void Cancel() => model?.Cancel();
    internal Task PreviewForFixture(string id) => model?.PreviewAsync(id) ?? Task.CompletedTask;
    internal Task ConfirmForFixture() => model?.ConfirmAsync() ?? Task.CompletedTask;
    internal Task EditionsForFixture() => model?.ChooseEditionAsync() ?? Task.CompletedTask;
    internal void TracksForFixture(int? position)
    {
        var target = discs.Children.OfType<Expander>().FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == "MusicDisc-" + (position ?? 1));
        if (target is null) return; target.IsExpanded = true; target.StartBringIntoView();
    }
    internal object Evidence => new
    {
        state = model?.State.ToString(), subjectId = model?.Item?.Id, title = title.Text, status = status.Text, model?.SelectionReason,
        releaseId = model?.Displayed?.Album.Release.Id, releaseGroupId = model?.Displayed?.Album.Release.ReleaseGroupId,
        date = model?.Displayed?.Album.Release.Date, labels = model?.Displayed?.Album.Release.Labels,
        discs = model?.Displayed?.Album.Discs.Select(x => new { x.Position, x.Title, x.Format, tracks = x.Tracks.Length,
            recordings = x.Tracks.Count(t => t.RecordingId is not null), personnel = x.Tracks.Sum(t => t.Credits.Length) }).ToArray(),
        fromCache = model?.Displayed?.FromCache, stale = model?.Displayed?.IsStale,
        candidateIds = model?.Match?.Search.Candidates.Select(x => x.Id).ToArray(), canConfirm = model?.CanConfirm,
        cover.SourceStatus, cover.HasImage, coverUrl = model?.Displayed?.Album.CoverUrl, width = ActualWidth, titleWidth = title.ActualWidth,
        scrollOffset = scroll.VerticalOffset, visibleDiscs = discs.Children.OfType<Expander>().Where(x => x.IsExpanded).Select(x => AutomationProperties.GetAutomationId(x)).ToArray(),
        coverFallback, trackScrollers = CountScrollers(tracksSection), realizedTracks = discs.Children.OfType<Expander>().Select(x => x.Content).OfType<ItemsRepeater>()
            .Select(x => new { count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(x),
                rowHeights = Enumerable.Range(0, Math.Min(5, Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(x)))
                    .Select(n => (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(x, n) as FrameworkElement)?.ActualHeight).ToArray() }).ToArray()
    };
    private static int CountScrollers(DependencyObject root)
    {
        int count = root is ScrollViewer ? 1 : 0;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountScrollers(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return count;
    }
    public void Deactivate() { active = false; generation++; startPending = false; model?.Deactivate(); cover.SourceKey = null; coverKey = null; requestedCover = null; ring.IsActive = false; }
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); } cover.Pipeline = null; }
    private const string TrackTemplate = """
    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:views="using:TheOrder.WinUI.Views"><Grid Padding="0,7" ColumnSpacing="10">
      <Grid.ColumnDefinitions><ColumnDefinition Width="32"/><ColumnDefinition Width="*"/><ColumnDefinition Width="52"/></Grid.ColumnDefinitions>
      <TextBlock Text="{Binding Number}" VerticalAlignment="Top" Opacity="0.7"/>
      <views:TagFlowPanel Grid.Column="1" HorizontalSpacing="14" VerticalSpacing="4" MaximumItemWidth="700" CenterInlineItems="False">
        <TextBlock Text="{Binding Title}" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
        <TextBlock Text="{Binding Artist}" FontSize="13" Opacity="0.75" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
        <TextBlock Text="{Binding Credits}" FontSize="13" Opacity="0.75" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
      </views:TagFlowPanel>
      <TextBlock Grid.Column="2" Text="{Binding Duration}" HorizontalAlignment="Right" Opacity="0.7"/>
    </Grid></DataTemplate>
    """;
}
public sealed class MusicTrackRow(MusicBrainzTrack track)
{
    public string Number => track.Number;
    public string Title => track.Title;
    public string Artist => track.Artist;
    public string Duration => track.LengthMs is { } duration ? TimeSpan.FromMilliseconds(duration).ToString(duration >= 3600000 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "";
    public string Credits => string.Join(" · ", track.Credits.Select(MusicBrainzAlbumView.Credit));
}
