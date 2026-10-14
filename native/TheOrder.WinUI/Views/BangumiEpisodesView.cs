using System.Globalization;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// Composition with an Expander avoids inheriting its theme target type. The
// containing detail page owns scrolling; episode rows do not create ScrollViewers.
public sealed class BangumiEpisodesView : Grid, IDisposable
{
    private readonly Expander section = new() { Header = "章节目录 · Bangumi", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 10 }, rows = new() { Spacing = 6 };
    private readonly TextBlock notice = Text(""), provenance = Text("保留 Bangumi 原始章节类型与排序；目录项数可能与作品集数不同。");
    private readonly Button refresh = Button("刷新目录"), more = Button("加载下一页"), cancel = Button("取消读取"), retry = Button("重试");
    private readonly HyperlinkButton source = new() { Content = "在 Bangumi 查看章节目录 ↗", Padding = new(0), HorizontalAlignment = HorizontalAlignment.Left };
    private BangumiEpisodesViewModel? model;
    private BangumiEpisode[]? shown;
    private bool disposed;

    public BangumiEpisodesView()
    {
        AutomationProperties.SetAutomationId(this, "BangumiEpisodes");
        AutomationProperties.SetAutomationId(section, "BangumiEpisodesExpander");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, cancel, retry } };
        body.Children.Add(provenance); body.Children.Add(notice); body.Children.Add(actions); body.Children.Add(rows); body.Children.Add(more); body.Children.Add(source);
        section.Content = body; Children.Add(section); Visibility = Visibility.Collapsed;
        section.Expanding += async (_, _) => { if (model is not null) await model.ExpandAsync(); };
        section.Collapsed += (_, _) => model?.Cancel();
        refresh.Click += async (_, _) => { if (model is not null) await model.RefreshAsync(); };
        more.Click += async (_, _) => { if (model is not null) await model.MoreAsync(); };
        retry.Click += async (_, _) => { if (model is not null) await model.RefreshAsync(); };
        cancel.Click += (_, _) => model?.Cancel();
        source.Click += async (_, _) =>
        {
            if (model?.SubjectId is not { } id) return;
            try { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://bgm.tv/subject/" + id + "/ep"))) throw new InvalidOperationException(); }
            catch { notice.Text = "Bangumi 暂时无法打开，请稍后重试。"; notice.Visibility = Visibility.Visible; }
        };
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
        section.Header = model?.Result is not null ? $"章节目录 · Bangumi · {model.Episodes.Length} / {model.Total} 项" : "章节目录 · Bangumi";
        notice.Text = model?.Message ?? ""; notice.Visibility = notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        refresh.IsEnabled = retry.IsEnabled = model?.CanLoad == true;
        cancel.Visibility = model?.IsBusy == true ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = model?.State is BangumiEpisodesState.Failed or BangumiEpisodesState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = model?.CanMore == true ? Visibility.Visible : Visibility.Collapsed; more.IsEnabled = model?.CanMore == true;
        source.IsEnabled = model?.SubjectId is not null;
        if (ReferenceEquals(shown, model?.Episodes)) return;
        shown = model?.Episodes; rows.Children.Clear();
        foreach (var episode in shown ?? [])
        {
            var name = episode.Title.Length > 0 ? episode.Title : "暂无标题";
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(Text(TypeName(episode.Type) + " " + episode.Sort.ToString("0.############################", CultureInfo.InvariantCulture) + " · " + name));
            var facts = new[] { episode.AirDate, episode.Duration.Length > 0 ? "时长 · " + episode.Duration : null }.Where(x => !string.IsNullOrEmpty(x));
            var factsText = string.Join(" · ", facts); if (factsText.Length > 0) content.Children.Add(Text(factsText, 12));
            if (episode.Summary.Length > 0 || episode.OriginalTitle.Length > 0 && episode.OriginalTitle != episode.Title)
            {
                var detail = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
                if (episode.OriginalTitle.Length > 0 && episode.OriginalTitle != episode.Title) detail.Children.Add(Text(episode.OriginalTitle, 12));
                if (episode.Summary.Length > 0) detail.Children.Add(Text(episode.Summary));
                var toggle = new HyperlinkButton { Content = "原文与简介", Padding = new(0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
                toggle.Click += (_, _) => { var opened = detail.Visibility == Visibility.Collapsed; detail.Visibility = opened ? Visibility.Visible : Visibility.Collapsed; toggle.Content = opened ? "收起" : "原文与简介"; };
                content.Children.Add(toggle); content.Children.Add(detail);
            }
            rows.Children.Add(new Border { Child = content, Padding = new(10, 7, 10, 7), CornerRadius = new(6), BorderThickness = new(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        }
    }
    private static string TypeName(int type) => type switch { 0 => "本篇", 1 => "特别篇", 2 => "OP", 3 => "ED", 4 => "预告 / 宣传", 5 => "MAD", 6 => "其他", _ => "未知类型" };
    private static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Button(string value) => new() { Content = value, Padding = new(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
    public async Task VisualExpandAsync()
    {
        var current = model; var id = current?.SubjectId;
        if (current is null || id is null) return;
        var operation = current.ExpandAsync(); var version = current.Version;
        await operation;
        if (!disposed && ReferenceEquals(model, current) && current.SubjectId == id && current.Version == version) section.IsExpanded = true;
    }
    public object Inspect() => new { SubjectId = model?.SubjectId, Expanded = section.IsExpanded, State = model?.State.ToString(), Count = model?.Episodes.Length ?? 0, Total = model?.Total ?? 0, Message = model?.Message, CanMore = model?.CanMore ?? false };
    public void Dispose() { if (disposed) return; disposed = true; if (model is not null) { model.PropertyChanged -= ModelChanged; model.Dispose(); } rows.Children.Clear(); shown = null; }
}
