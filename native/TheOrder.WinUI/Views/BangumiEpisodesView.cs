using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// Composition with an Expander avoids inheriting its theme target type. The
// containing detail page owns scrolling; episode rows do not create ScrollViewers.
public sealed class BangumiEpisodesView : Grid, IDisposable
{
    private readonly Expander section = new() { Header = "分集目录 · Bangumi", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 10 }, rows = new() { Spacing = 6 };
    private readonly TextBlock notice = Text(""), provenance = Text("保留 Bangumi 原始章节类型与同类排序；目录项数可能与作品集数不同。"), filterNotice = Text("", 12);
    private readonly TagFlowPanel filters = new() { HorizontalSpacing = 6, VerticalSpacing = 6 };
    private readonly Button refresh = Button("刷新目录"), more = Button("加载下一页"), cancel = Button("取消读取"), retry = Button("重试");
    private readonly HyperlinkButton source = new() { Content = "在 Bangumi 查看分集目录 ↗", Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
    private BangumiEpisodesViewModel? model;
    private BangumiEpisode[]? shown;
    private int? shownType;
    private readonly Dictionary<int, Action> expandEpisode = [];
    private readonly HashSet<int> expandedEpisodes = [];
    private readonly List<HyperlinkButton> episodeLinks = [];
    private Task expanding = Task.CompletedTask;
    private bool disposed;

    public BangumiEpisodesView()
    {
        AutomationProperties.SetAutomationId(this, "BangumiEpisodes");
        AutomationProperties.SetAutomationId(section, "BangumiEpisodesExpander");
        var actions = new TagFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 6, Children = { refresh, cancel, retry } };
        body.Children.Add(provenance); body.Children.Add(notice); body.Children.Add(actions); body.Children.Add(filters); body.Children.Add(filterNotice); body.Children.Add(rows); body.Children.Add(more); body.Children.Add(source);
        section.Content = body; Children.Add(section); Visibility = Visibility.Collapsed;
        section.Expanding += async (_, _) => { if (model is not null) { expanding = model.ExpandAsync(); await expanding; } };
        section.Collapsed += (_, _) => model?.Cancel();
        refresh.Click += async (_, _) => { if (model is not null) await model.RefreshAsync(); };
        more.Click += async (_, _) => { if (model is not null) await model.MoreAsync(); };
        retry.Click += async (_, _) => { if (model is not null) await model.RefreshAsync(); };
        cancel.Click += (_, _) => model?.Cancel();
        source.Click += async (_, _) => { if (model?.SubjectId is { } id) await OpenSourceAsync(new Uri("https://bgm.tv/subject/" + id + "/ep")); };
    }
    public void Configure(IBangumiEpisodesReader reader)
    {
        if (disposed) return;
        if (model is not null) { model.PropertyChanged -= ModelChanged; model.Dispose(); }
        model = new(reader); model.PropertyChanged += ModelChanged; shown = null; Render();
    }
    public void SetSource(string? verifiedBangumiId)
    {
        if (disposed) return;
        section.IsExpanded = false; shown = null; model?.SetSource(verifiedBangumiId); Render();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => Render();
    private void Render()
    {
        if (disposed) return;
        Visibility = model?.SubjectId is not null ? Visibility.Visible : Visibility.Collapsed;
        section.Header = model?.Result is not null ? $"分集目录 · Bangumi · {model.Episodes.Length} / {model.Total} 项" : "分集目录 · Bangumi";
        notice.Text = model?.Message ?? ""; notice.Visibility = notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        refresh.IsEnabled = retry.IsEnabled = model?.CanLoad == true;
        cancel.Visibility = model?.IsBusy == true ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = model?.State is BangumiEpisodesState.Failed or BangumiEpisodesState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = model?.CanMore == true ? Visibility.Visible : Visibility.Collapsed; more.IsEnabled = model?.CanMore == true;
        source.IsEnabled = model?.SubjectId is not null;
        var displayed = model?.VisibleEpisodes ?? [];
        filterNotice.Text = model?.HasUnloadedEpisodes == true
            ? $"已载入 {model.Episodes.Length} / {model.Total} 项；筛选仅作用于已载入章节。"
            : model?.Result is not null ? $"显示 {displayed.Length} / {model.Episodes.Length} 项" : "";
        filterNotice.Visibility = filterNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(shown, model?.Episodes) && shownType == model?.SelectedType) return;
        shown = model?.Episodes; shownType = model?.SelectedType; rows.Children.Clear(); filters.Children.Clear();
        expandEpisode.Clear(); expandedEpisodes.Clear(); episodeLinks.Clear();
        foreach (var filter in model?.TypeFilters ?? [])
        {
            var chip = new ToggleButton { Content = $"{filter.Caption} {filter.Count}", IsChecked = filter.Type == model?.SelectedType,
                CornerRadius = new(6), Padding = new(10, 5, 10, 5) };
            AutomationProperties.SetAutomationId(chip, "BangumiEpisodeFilter-" + (filter.Type?.ToString() ?? "all"));
            AutomationProperties.SetName(chip, filter.Caption + "，已载入 " + filter.Count + " 项");
            chip.Click += (_, _) => { if (model?.SelectedType == filter.Type) chip.IsChecked = true; else model?.SetType(filter.Type); };
            filters.Children.Add(chip);
        }
        if (displayed.Length == 0 && model?.Result is not null && model.Episodes.Length > 0)
            rows.Children.Add(Text(model.HasUnloadedEpisodes ? "已载入的章节中没有此类型；可继续加载下一页。" : "当前目录没有此类型章节。"));
        int? group = null; var multipleTypes = (model?.TypeFilters.Length ?? 0) > 2;
        foreach (var episode in displayed)
        {
            if (shownType is null && multipleTypes && group != episode.Type)
            {
                var header = Text(BangumiEpisodeDisplay.TypeName(episode.Type), 14); header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                header.Margin = new(0, 6, 0, 2); rows.Children.Add(header); group = episode.Type;
            }
            var name = episode.Title.Length > 0 ? episode.Title : "暂无标题";
            var content = new StackPanel { Spacing = 4 };
            var original = Link(BangumiEpisodeDisplay.TypeName(episode.Type) + " " + BangumiEpisodeDisplay.Number(episode) + " · " + name + " ↗");
            original.FontSize = 13;
            AutomationProperties.SetAutomationId(original, "BangumiEpisodeSource-" + episode.Id);
            AutomationProperties.SetName(original, name + "，在 Bangumi 查看本集");
            ToolTipService.SetToolTip(original, "在 Bangumi 查看本集");
            original.Click += async (_, _) => await OpenSourceAsync(BangumiEpisodeDisplay.EpisodeUri(episode.Id));
            content.Children.Add(original); episodeLinks.Add(original);
            var facts = new[] { episode.AirDate, episode.Duration.Length > 0 ? "时长 · " + episode.Duration : null }.Where(x => !string.IsNullOrEmpty(x));
            var factsText = string.Join(" · ", facts); if (factsText.Length > 0) content.Children.Add(Text(factsText, 12));
            if (episode.Summary.Length > 0 || episode.OriginalTitle.Length > 0 && episode.OriginalTitle != episode.Title)
            {
                var detail = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
                if (episode.OriginalTitle.Length > 0 && episode.OriginalTitle != episode.Title) detail.Children.Add(Text(episode.OriginalTitle, 12));
                if (episode.Summary.Length > 0) { detail.Children.Add(Text("章节说明（原文）", 12)); detail.Children.Add(Text(episode.Summary)); }
                var toggle = Link(episode.Summary.Length > 0 ? "章节说明与原文" : "原标题");
                var toggleLabel = (TextBlock)toggle.Content;
                AutomationProperties.SetAutomationId(toggle, "BangumiEpisodeDescription-" + episode.Id);
                void Expand() { detail.Visibility = Visibility.Visible; toggleLabel.Text = "收起"; expandedEpisodes.Add(episode.Id); }
                toggle.Click += (_, _) =>
                {
                    if (detail.Visibility == Visibility.Collapsed) Expand();
                    else { detail.Visibility = Visibility.Collapsed; toggleLabel.Text = episode.Summary.Length > 0 ? "章节说明与原文" : "原标题"; expandedEpisodes.Remove(episode.Id); }
                };
                expandEpisode[episode.Id] = Expand;
                content.Children.Add(toggle); content.Children.Add(detail);
            }
            rows.Children.Add(new Border { Child = content, Padding = new(10, 7, 10, 7), CornerRadius = new(6), BorderThickness = new(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        }
    }
    private async Task OpenSourceAsync(Uri uri)
    {
        var current = model; var id = current?.SubjectId; var version = current?.Version;
        if (current is null || id is null || disposed) return;
        try { if (!await Windows.System.Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException(); }
        catch
        {
            if (!disposed && ReferenceEquals(model, current) && model.SubjectId == id && model.Version == version)
            { notice.Text = "Bangumi 暂时无法打开，请稍后重试。"; notice.Visibility = Visibility.Visible; }
        }
    }
    private static HyperlinkButton Link(string value) => new() { Content = new TextBlock { Text = value, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap },
        Padding = new(0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
    private static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Button(string value) => new() { Content = value, Padding = new(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
    public async Task ExpandAsync()
    {
        var current = model; var id = current?.SubjectId;
        if (current is null || id is null) return;
        // Reveal loading/cancel UI immediately, rather than making a jump action
        // appear unresponsive until the remote directory finishes loading.
        section.IsExpanded = true;
        await expanding;
    }
    internal Task VisualExpandAsync() => ExpandAsync();
    public bool FilterForFixture(int? type)
    {
        if (disposed || model is null || !BangumiEpisodeDisplay.ValidType(type)) return false;
        model.SetType(type); return true;
    }
    public bool ExpandEpisodeForFixture(int episodeId)
    {
        if (disposed || !expandEpisode.TryGetValue(episodeId, out var expand)) return false;
        expand(); return true;
    }
    public object Inspect() => new { SubjectId = model?.SubjectId, Expanded = section.IsExpanded, State = model?.State.ToString(), Count = model?.Episodes.Length ?? 0, Total = model?.Total ?? 0, Message = model?.Message, CanMore = model?.CanMore ?? false,
        SelectedType = model?.SelectedType, DisplayedCount = model?.VisibleEpisodes.Length ?? 0, HasUnloaded = model?.HasUnloadedEpisodes ?? false,
        Types = model?.TypeFilters, ExpandedEpisodes = expandedEpisodes.Order().ToArray(), VisibleEpisodes = model?.VisibleEpisodes.Select(x => new { x.Id, x.Type, x.Sort }).ToArray(),
        Links = episodeLinks.Select(InteractiveCardEvidence.Hit).ToArray() };
    public void Dispose() { if (disposed) return; disposed = true; if (model is not null) { model.PropertyChanged -= ModelChanged; model.Dispose(); } rows.Children.Clear(); filters.Children.Clear(); expandEpisode.Clear(); expandedEpisodes.Clear(); episodeLinks.Clear(); shown = null; }
}
