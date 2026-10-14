using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// All content participates in the host's single outer ScrollViewer.
public sealed class BookPublicationView : StackPanel, IDisposable
{
    private readonly Expander section = Expand("书籍与出版资料"), raw = Expand("Bangumi 原始字段"), relations = Expand("系列、单行本与篇章 · Bangumi"), ndl = Expand("查找独立出版记录 · NDL");
    private readonly StackPanel content = new() { Spacing = 14 }, facts = new() { Spacing = 5 }, rawBody = new() { Spacing = 5 }, searchBody = new() { Spacing = 10 }, savedBody = new() { Spacing = 8 }, previewBody = new() { Spacing = 6 };
    private readonly TagFlowPanel relationCards = Flow(), candidateCards = Flow(), actions = Flow();
    private readonly TextBlock heading = Text(20), identity = Text(13), incoming = Text(13), message = Text(14), relationNotice = Text(13), searchNotice = Text(13), confirmation = Text(13);
    private readonly TextBox queryTitle = new() { PlaceholderText = "原标题（可修改）", MaxLength = 200, HorizontalAlignment = HorizontalAlignment.Stretch }, queryIsbn = new() { PlaceholderText = "ISBN（有编号时优先精确检索；清空可按标题查找）", MaxLength = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button back = Button("返回上一本"), cancel = Button("取消读取"), reloadSaved = Button("重读已保存记录"), refreshRelations = Button("重读关系"), search = Button("查找出版候选"), next = Button("下一页候选"), confirm = Button("已核对，确认这条出版记录"), refreshPreview = Button("刷新此出版记录");
    private BookDetailsViewModel? model;
    private string? shownSource;
    private BangumiRelationsLoadResult? shownRelations;
    private BookPublication[]? shownCandidates;
    private BookPublicationBinding[]? shownBindings;
    private BookPublicationResult? shownPreview;
    private bool syncing, disposed;
    private long renderVersion;

    public BookPublicationView()
    {
        Spacing = 10; Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "BookPublication"); AutomationProperties.SetAutomationId(section, "BookExpand");
        AutomationProperties.SetAutomationId(relations, "BookRelations"); AutomationProperties.SetAutomationId(ndl, "BookSearchSection");
        AutomationProperties.SetAutomationId(queryTitle, "BookSearchTitle"); AutomationProperties.SetAutomationId(queryIsbn, "BookSearchIsbn");
        AutomationProperties.SetAutomationId(search, "BookSearch"); AutomationProperties.SetAutomationId(confirm, "BookConfirm");
        AutomationProperties.SetAutomationId(message, "BookStatus"); AutomationProperties.SetAutomationId(back, "BookBack");
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; identity.Opacity = incoming.Opacity = .75;
        actions.Children.Add(back); actions.Children.Add(cancel); actions.Children.Add(reloadSaved);
        content.Children.Add(heading); content.Children.Add(identity); content.Children.Add(incoming); content.Children.Add(actions); content.Children.Add(message); content.Children.Add(facts);
        raw.Content = rawBody; content.Children.Add(raw);
        var relationBody = new StackPanel { Spacing = 10, Children = { relationNotice, refreshRelations, relationCards } };
        relations.Content = relationBody; content.Children.Add(relations); content.Children.Add(savedBody);
        searchBody.Children.Add(Text(13, "检索出版记录，核对卷次与版本后保存关联。"));
        searchBody.Children.Add(Text(13, "原标题")); searchBody.Children.Add(queryTitle); searchBody.Children.Add(Text(13, "ISBN")); searchBody.Children.Add(queryIsbn); searchBody.Children.Add(search);
        searchBody.Children.Add(searchNotice); searchBody.Children.Add(candidateCards); searchBody.Children.Add(next);
        ndl.Content = searchBody; content.Children.Add(ndl); content.Children.Add(previewBody); content.Children.Add(confirmation);
        var publicationActions = Flow(); publicationActions.Children.Add(confirm); publicationActions.Children.Add(refreshPreview); content.Children.Add(publicationActions);
        section.Content = content; Children.Add(section);
        section.Expanding += async (_, _) => { if (!syncing && model is not null) await model.ExpandAsync(); };
        section.Collapsed += (_, _) => { if (!syncing) model?.Collapse(); };
        relations.Expanding += async (_, _) => { if (!syncing && model is not null) await model.LoadRelationsAsync(); };
        back.Click += (_, _) => GoBack(); cancel.Click += (_, _) => model?.Cancel();
        reloadSaved.Click += async (_, _) => { if (model is not null) await model.ReloadBindingsAsync(); };
        refreshRelations.Click += async (_, _) => { if (model is not null) await model.LoadRelationsAsync(true); };
        search.Click += async (_, _) => await SearchAsync(); queryTitle.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SearchAsync(); } };
        queryIsbn.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SearchAsync(); } };
        next.Click += async (_, _) => await NextAsync();
        confirm.Click += async (_, _) => { if (model is not null) await model.ConfirmAsync(); };
        refreshPreview.Click += async (_, _) => { if (model?.Preview is { } preview) await model.PreviewAsync(preview.Publication.Id, true); };
        foreach (var panel in new[] { relationCards, candidateCards }) panel.SizeChanged += (_, _) => Clamp(panel);
        Unloaded += (_, _) => { if (!IsLoaded) Deactivate(); };
    }
    public void Configure(IBangumiSubjectDetailsReader subjects, IBookBibliographyReader books, IBangumiBookRelationsReader bookRelations)
    {
        if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); }
        model = new(subjects, books, bookRelations); model.PropertyChanged += StateChanged; Render();
    }
    public void SetSource(BangumiSubjectDetails? bangumi, AniListDetails? currentAniList, bool bridgeTrusted) => model?.SetSource(bangumi, currentAniList, bridgeTrusted);
    private void StateChanged(object? sender, PropertyChangedEventArgs e) { if (!disposed) Render(); }
    private void Render()
    {
        renderVersion++; var source = model?.Source;
        Visibility = Visible(source is not null); if (model is null || source is null) return;
        syncing = true; section.IsExpanded = model.Expanded; syncing = false;
        heading.Text = source.Title; identity.Text = source.Identity.Provider + " · " + LevelName(source.Identity.Level) + " · " + source.Identity.Id;
        incoming.Text = model.Incoming; incoming.Visibility = Visible(incoming.Text.Length > 0);
        message.Text = model.Message; message.Visibility = Visible(message.Text.Length > 0);
        back.Visibility = Visible(model.CanBack); cancel.Visibility = Visible(model.IsBusy); back.IsEnabled = model.CanBack;
        reloadSaved.IsEnabled = model.CanLoad;
        if (shownSource != source.Fingerprint)
        {
            shownSource = source.Fingerprint; queryTitle.Text = model.DefaultTitle.Length <= 200 ? model.DefaultTitle : model.DefaultTitle[..200]; queryIsbn.Text = model.DefaultIsbn;
            syncing = true; raw.IsExpanded = false; relations.IsExpanded = model.Relations is not null; ndl.IsExpanded = model.Search is not null; syncing = false;
            shownRelations = null; shownCandidates = null; shownBindings = null; shownPreview = null;
            relationCards.Children.Clear(); candidateCards.Children.Clear(); savedBody.Children.Clear(); previewBody.Children.Clear();
            RenderFacts();
        }
        relations.Visibility = Visible(source.Identity.Provider == "Bangumi");
        relations.IsEnabled = ndl.IsEnabled = model.CanLoad;
        relationNotice.Text = model.Relations is { } r ? $"{r.Snapshot.Items.Count(x => x.Kind == BangumiRelationKind.Book)} 个书籍关联 · 原始方向：当前条目 → 关联条目" + Cache(r.FromCache, r.IsStale, r.Warning)
            : "展开后读取当前书籍的关系。标签原样保留，不按标题猜测卷次。";
        if (!ReferenceEquals(shownRelations, model.Relations)) { shownRelations = model.Relations; RenderRelations(); }
        if (!ReferenceEquals(shownCandidates, model.Candidates)) { shownCandidates = model.Candidates; RenderCandidates(); }
        if (!ReferenceEquals(shownBindings, model.Bindings)) { shownBindings = model.Bindings; RenderSaved(); }
        if (!ReferenceEquals(shownPreview, model.Preview)) { shownPreview = model.Preview; RenderPreview(); }
        foreach (var button in relationCards.Children.OfType<Button>().Concat(candidateCards.Children.OfType<Button>()).Concat(savedBody.Children.OfType<StackPanel>().SelectMany(x => x.Children.OfType<Button>()))) button.IsEnabled = model.CanLoad;
        search.IsEnabled = queryTitle.IsEnabled = queryIsbn.IsEnabled = refreshRelations.IsEnabled = model.CanLoad;
        next.Visibility = Visible(model.Search?.Page.NextStart is not null); next.IsEnabled = model.CanLoad;
        searchNotice.Text = model.Search is { } result ? $"已显示 {model.Candidates.Length} 条 / 原站共 {result.Page.Total} 条" + Cache(result.FromCache, result.IsStale, result.Warning) : "尚未检索；有 ISBN 时优先精确检索。";
        confirm.Visibility = refreshPreview.Visibility = confirmation.Visibility = Visible(model.Preview is not null);
        confirm.IsEnabled = model.CanConfirm; refreshPreview.IsEnabled = model.CanLoad;
        confirmation.Text = model.PreviewIsBound ? "已保存此 NDL 编号的关联。" : model.ConfirmationBlock;
    }
    private void RenderFacts()
    {
        facts.Children.Clear(); rawBody.Children.Clear(); if (model?.Source is not { } source) return;
        Fact(facts, "原标题", source.OriginalTitle); Fact(facts, "形式", source.Format);
        if (model.Bangumi is { } bg)
        {
            var b = bg.BookFacts; Fact(facts, "发行日期 · Bangumi", bg.ReleaseDate);
            if (b is not null)
            {
                Fact(facts, "原作 / 作者 · Bangumi", b.Authors); Fact(facts, "作画 / 插画 · Bangumi", b.Illustrators); Fact(facts, "出版社 · Bangumi", b.Publishers); Fact(facts, "刊物 · Bangumi", b.Magazines);
                Fact(facts, "文库 / 丛书 · Bangumi", b.Imprints); Fact(facts, "卷次原文 · Bangumi", b.Volume); Fact(facts, "原站册数 · Bangumi", b.Volumes is > 0 ? b.Volumes.ToString() : null); Fact(facts, "原站话数 · Bangumi", b.Chapters is > 0 ? b.Chapters.ToString() : null); Fact(facts, "ISBN · Bangumi", b.Isbns);
                foreach (var item in b.RawFacts) Fact(rawBody, item.Name, item.Values);
            }
            facts.Children.Add(SourceLink("打开 Bangumi 原始书籍资料", bg.Url, false));
        }
        else
        {
            Fact(facts, "人员与职责 · AniList", model.AniList?.Staff.Select(x => x.Role + " · " + (x.Person.NativeName.Length > 0 ? x.Person.NativeName : x.Person.Name)).ToArray() ?? []);
            if (model.AniList is { } ani) { Fact(facts, "开始日期 · AniList", ani.Media.StartDate); Fact(facts, "原站册数 · AniList", ani.Media.Volumes?.ToString()); facts.Children.Add(SourceLink("打开 AniList 当前书籍", ani.Media.Url, false)); }
            facts.Children.Add(Text(13, "当前 AniList 书籍未提供明确的 Bangumi 对应编号；不借用上一部作品的书籍关系。"));
        }
        raw.Visibility = Visible(rawBody.Children.Count > 0);
    }
    private void RenderRelations()
    {
        relationCards.Children.Clear();
        foreach (var item in model?.Relations?.Snapshot.Items.Where(x => x.Kind == BangumiRelationKind.Book) ?? [])
        {
            var button = Card(item.Title, "→ " + item.Relation + (item.OriginalTitle.Length > 0 && item.OriginalTitle != item.Title ? " · " + item.OriginalTitle : ""));
            AutomationProperties.SetAutomationId(button, "BookOpen-" + item.Id);
            button.Click += async (_, _) => await OpenRelatedVisualAsync(item.Id);
            relationCards.Children.Add(button);
        }
        Clamp(relationCards);
    }
    private void RenderCandidates()
    {
        candidateCards.Children.Clear();
        foreach (var item in model?.Candidates ?? [])
        {
            var description = string.Join(" · ", new[] { item.IsAggregate ? "集合记录 · 不可确认" : "独立出版记录", item.Volume, item.PublicationDate, string.Join(" / ", item.Publishers), item.Isbns.FirstOrDefault(), "NDL " + item.Id }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var button = Card(item.Title, description); AutomationProperties.SetAutomationId(button, "BookPreview-" + item.Id);
            button.Click += async (_, _) => await PreviewVisualAsync(item.Id); candidateCards.Children.Add(button);
        }
        Clamp(candidateCards);
    }
    private void RenderSaved()
    {
        savedBody.Children.Clear(); if (model?.Bindings.Length is not > 0) return;
        savedBody.Children.Add(Text(16, "已确认的独立出版记录"));
        foreach (var binding in model.Bindings)
        {
            var row = new StackPanel { Spacing = 5 }; row.Children.Add(Text(13, $"NDL {binding.PublicationId} · {binding.ConfirmedAtUtc.ToLocalTime():yyyy-MM-dd}"));
            var load = Button("打开这条已保存记录"); var forget = Button("解除此关联");
            AutomationProperties.SetAutomationId(load, "BookSaved-" + binding.PublicationId); AutomationProperties.SetAutomationId(forget, "BookForget-" + binding.PublicationId);
            load.Click += async (_, _) => await PreviewVisualAsync(binding.PublicationId);
            forget.Click += async (_, _) => { if (model is not null) await model.ForgetAsync(binding.PublicationId); };
            row.Children.Add(load); row.Children.Add(forget); savedBody.Children.Add(row);
        }
    }
    private void RenderPreview()
    {
        previewBody.Children.Clear(); if (model?.Preview is not { } result) return;
        var p = result.Publication; previewBody.Children.Add(Text(20, "出版记录预览 · NDL")); previewBody.Children.Add(Text(18, p.Title));
        Fact(previewBody, "编号", p.Id); Fact(previewBody, "记录层级", p.IsAggregate ? "集合 / 汇总记录" : "独立出版物"); Fact(previewBody, "卷次原文", p.Volume);
        Fact(previewBody, "ISBN", p.Isbns); Fact(previewBody, "套装 ISBN", p.SetIsbns); Fact(previewBody, "出版日期原文", p.PublicationDate); Fact(previewBody, "版次", p.Edition);
        Fact(previewBody, "语言", p.Languages); Fact(previewBody, "出版社", p.Publishers); Fact(previewBody, "文库 / 丛书", p.BibliographicSeries); Fact(previewBody, "责任说明原文", p.CreatorStatements);
        previewBody.Children.Add(Text(12, "NDL · " + p.License + Cache(result.FromCache, result.IsStale, result.Warning)));
        previewBody.Children.Add(SourceLink("打开这条 NDL 原始记录", p.Url, true));
    }
    private HyperlinkButton SourceLink(string text, string url, bool isNdl)
    {
        var link = new HyperlinkButton { Content = text, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += async (_, _) =>
        {
            var expected = model?.Source?.Fingerprint; var version = model?.Version;
            try
            {
                bool valid = isNdl ? model?.Preview?.Publication is { } p && BookValidation.NdlId(p.Id) && url == p.Url
                    : model?.Bangumi is { } b ? url == b.Url : model?.AniList is { } a && url == a.Media.Url;
                if (!valid || !await Windows.System.Launcher.LaunchUriAsync(new Uri(url))) throw new InvalidOperationException();
            }
            catch { if (!disposed && model?.Expanded == true && model.Source?.Fingerprint == expected && model.Version == version) { message.Text = "原始来源暂时无法打开，请稍后重试。"; message.Visibility = Visibility.Visible; } }
        }; return link;
    }
    private Task SearchAsync() => model?.SearchAsync(queryTitle.Text, queryIsbn.Text) ?? Task.CompletedTask;
    private async Task PreviewVisualAsync(string id)
    {
        if (model is not { } current) return;
        var source = current.Source?.Fingerprint; var operation = current.PreviewAsync(id); var version = current.Version;
        await operation;
        bool Accept() => !disposed && ReferenceEquals(model, current) && current.Version == version
            && current.Expanded && current.Source?.Fingerprint == source && current.Preview?.Publication.Id == id;
        if (Accept()) Bring(previewBody, Accept);
    }
    private async Task OpenRelatedVisualAsync(string id)
    {
        if (model is not { } current) return;
        var operation = current.OpenRelatedAsync(id); var version = current.Version;
        await operation;
        if (!disposed && ReferenceEquals(model, current) && current.Version == version && current.Expanded
            && current.Source?.Identity is { Provider: "Bangumi" } source && source.Id == id) FocusHeading();
    }
    private async Task NextAsync()
    {
        if (model is null) return;
        var source = model.Source?.Fingerprint; var previousCount = model.Candidates.Length;
        var operation = model.NextAsync(); var generation = model.Version;
        await operation;
        if (disposed || model.Version != generation || model.Source?.Fingerprint != source || model.Candidates.Length <= previousCount) return;
        var version = renderVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (disposed || model.Version != generation || model.Source?.Fingerprint != source || version != renderVersion || candidateCards.Children.ElementAtOrDefault(previousCount) is not Button target) return;
            target.Focus(FocusState.Programmatic); target.StartBringIntoView();
        });
    }
    public bool GoBack() { var result = model?.GoBack() == true; if (result) FocusHeading(); return result; }
    private void Bring(FrameworkElement element, Func<bool>? accept = null)
    {
        var version = renderVersion;
        DispatcherQueue.TryEnqueue(() => { if (!disposed && version == renderVersion && element.IsLoaded && (accept is null || accept()))
        { UpdateLayout(); element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); } });
    }
    private void FocusHeading() { var version = renderVersion; DispatcherQueue.TryEnqueue(() => { if (!disposed && version == renderVersion) { section.StartBringIntoView(); if (back.Visibility == Visibility.Visible) back.Focus(FocusState.Programmatic); else section.Focus(FocusState.Programmatic); } }); }
    public void Cancel() => model?.Cancel();
    public void Deactivate() { model?.Deactivate(); shownSource = null; renderVersion++; }
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); } }
    internal async Task FixtureAsync(string action, string? id)
    {
        if (model?.Source is null) return;
        switch (action)
        {
            case "bookExpand": await model.ExpandAsync(); break;
            case "bookCollapse": model.Collapse(); break;
            case "bookRelations": await model.ExpandAsync(); syncing = true; relations.IsExpanded = true; syncing = false; await model.LoadRelationsAsync(); break;
            case "bookOpen" when id is not null && relations.IsExpanded: await model.OpenRelatedAsync(id); break;
            case "bookSearch": await model.ExpandAsync(); ndl.IsExpanded = true; await SearchAsync(); break;
            case "bookPreview" when id is not null: await model.PreviewAsync(id); break;
            case "bookConfirm": await model.ConfirmAsync(); break;
            case "bookForget" when id is not null: await model.ForgetAsync(id); break;
            case "bookReload": await model.ReloadBindingsAsync(); break;
            case "bookNext": await NextAsync(); break;
            case "bookBack": GoBack(); break;
            case "bookCancel": model.Cancel(); break;
            case "bookInspect" when id is "relations": Bring(relations); return;
            case "bookInspect" when id is "candidates": Bring(candidateCards); return;
            case "bookInspect" when id is "preview": Bring(previewBody); return;
            case "bookInspect" when id is "saved": Bring(savedBody); return;
        }
        if (action is "bookPreview" or "bookConfirm") Bring(previewBody);
        else if (action == "bookRelations") Bring(relations);
        else if (action == "bookSearch") Bring(candidateCards);
        else Bring(section);
    }
    internal object Evidence => new { source = model?.Source?.Identity, title = model?.Source?.Title, expanded = model?.Expanded, state = model?.State.ToString(), busy = model?.IsBusy,
        historyDepth = model?.HistoryDepth, status = model?.Message, incoming = model?.Incoming, canConfirm = model?.CanConfirm, confirmationBlock = model?.ConfirmationBlock,
        relationExpanded = relations.IsExpanded, relations = model?.Relations?.Snapshot.Items.Where(x => x.Kind == BangumiRelationKind.Book).Select(x => new { x.Id, x.Relation }).ToArray(),
        candidates = model?.Candidates.Select(x => new { x.Id, x.Title, x.Isbns, x.IsAggregate }).ToArray(), nextStart = model?.Search?.Page.NextStart,
        preview = model?.Preview?.Publication, saved = model?.Bindings.Select(x => x.PublicationId).ToArray(), bindingsLoaded = model?.BindingsLoaded,
        candidateHitTargets = candidateCards.Children.OfType<Button>().Select(InteractiveCardEvidence.Hit).ToArray(), width = ActualWidth };
    private static string LevelName(BookEntityLevel value) => value switch { BookEntityLevel.Series => "系列", BookEntityLevel.Volume => "单卷", BookEntityLevel.Publication => "出版物", BookEntityLevel.Aggregate => "集合", _ => "层级未注明" };
    private static string Cache(bool cached, bool stale, string? warning) => (stale ? " · 暂时显示上次资料" : "") + (string.IsNullOrWhiteSpace(warning) ? "" : " · " + warning);
    private static void Fact(StackPanel target, string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) target.Children.Add(Text(14, name + " · " + value)); }
    private static void Fact(StackPanel target, string name, string[] values) { if (values.Length > 0) Fact(target, name, string.Join(" / ", values)); }
    private static TextBlock Text(double size, string value = "") => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Button(string value) => new() { Content = value, CornerRadius = new(6), MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };
    private static Expander Expand(string value) => new() { Header = value, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static TagFlowPanel Flow() => new() { HorizontalSpacing = 10, VerticalSpacing = 8, MaximumItemWidth = 340, CenterInlineItems = false };
    private static Button Card(string title, string subtitle)
    {
        var text = new StackPanel { Spacing = 5, IsHitTestVisible = false, Children = { Text(15, title), Text(12, subtitle) } };
        return new() { Content = text, Width = 330, MaxWidth = 330, Tag = 330d, CornerRadius = new(6), Padding = new(10), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
    }
    private static void Clamp(TagFlowPanel panel) { if (panel.ActualWidth > 0) foreach (var child in panel.Children.OfType<FrameworkElement>()) if (child.Tag is double width) child.MaxWidth = Math.Min(width, panel.ActualWidth); }
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
