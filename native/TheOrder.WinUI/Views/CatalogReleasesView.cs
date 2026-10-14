using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

// One collapsed, read-only section inside the host's existing ScrollViewer.
public sealed class CatalogReleasesView : StackPanel, IDisposable
{
    private readonly Expander section = new() { Header = "发行与版本资料 · NextMoe", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 12 }, rows = new() { Spacing = 10 };
    private readonly TextBlock message = Text("展开后按需读取发行资料。", 14), provenance = Text("", 12);
    private readonly Button refresh = new() { Content = "刷新发行资料" }, next = new() { Content = "读取下一页" }, cancel = new() { Content = "取消读取" };
    private CatalogReleasesViewModel? model;
    private CatalogReleaseEdition[]? shownRows;
    private bool syncing, disposed;
    public Func<string, Task>? OpenLink { get; set; }

    public CatalogReleasesView()
    {
        Spacing = 10; Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "CatalogReleases"); AutomationProperties.SetAutomationId(section, "CatalogReleasesExpand");
        AutomationProperties.SetAutomationId(refresh, "CatalogReleasesRefresh"); AutomationProperties.SetAutomationId(next, "CatalogReleasesNext");
        AutomationProperties.SetAutomationId(cancel, "CatalogReleasesCancel"); AutomationProperties.SetAutomationId(message, "CatalogReleasesStatus");
        body.Children.Add(Text("发行记录包括本篇、体验版与补丁；按原站类型分别展示。", 13));
        var actions = new TagFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        actions.Children.Add(refresh); actions.Children.Add(cancel); body.Children.Add(actions); body.Children.Add(message);
        provenance.Opacity = .72; body.Children.Add(provenance); body.Children.Add(rows); body.Children.Add(next);
        section.Content = body; Children.Add(section);
        section.Expanding += async (_, _) => { if (!syncing && model is not null) await model.ExpandAsync(); };
        section.Collapsed += (_, _) => { if (!syncing) model?.Collapse(); };
        refresh.Click += async (_, _) => { if (model is not null) await model.RefreshAsync(); };
        next.Click += async (_, _) => { if (model is not null) await model.NextAsync(); };
        cancel.Click += (_, _) => model?.Cancel();
        Unloaded += (_, _) => { if (!IsLoaded) Deactivate(); };
    }
    public void Configure(ICatalogReleaseReader reader, Func<long>? authGeneration = null)
    {
        if (disposed) return;
        if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); }
        model = new(reader, authGeneration); model.PropertyChanged += StateChanged; shownRows = null; Render();
    }
    public void SetSource(string? workId) => model?.SetSource(workId);
    public void Deactivate() => model?.Deactivate();
    public Task ExpandAsync() => model?.ExpandAsync() ?? Task.CompletedTask;
    public void Collapse() => model?.Collapse();
    public Task RefreshAsync() => model?.RefreshAsync() ?? Task.CompletedTask;
    public Task NextAsync() => model?.NextAsync() ?? Task.CompletedTask;
    public void Cancel() => model?.Cancel();
    private void StateChanged(object? sender, PropertyChangedEventArgs e) { if (!disposed) Render(); }
    private void Render()
    {
        Visibility = model?.WorkId is null ? Visibility.Collapsed : Visibility.Visible;
        syncing = true; section.IsExpanded = model?.Expanded ?? false; syncing = false;
        if (model is null) return;
        message.Text = UiText.Get(model.Message.Length > 0 ? model.Message : "展开后按需读取发行资料。");
        refresh.IsEnabled = model.CanLoad; next.IsEnabled = model.CanNext;
        next.Visibility = model.NextCursor is not null && model.Items.Length < CatalogReleasesViewModel.MaximumRows ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = model.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        provenance.Text = model.FetchedAtUtc is not null ? "资料来自 NextMoe 作品目录；原始发行来源见各条记录。" : "";
        if (ReferenceEquals(shownRows, model.Items)) return;
        shownRows = model.Items; rows.Children.Clear();
        foreach (var row in model.Items.OrderBy(x => x.Kind == "default" ? 0 : x.Kind is "physical" or "digital" ? 1 : x.Kind == "trial" ? 2 : x.Kind == "patch" ? 3 : 4))
        {
            var card = new StackPanel { Spacing = 6 };
            var heading = Text(string.IsNullOrWhiteSpace(row.Title) ? "发行标题未收录" : row.Title, 16);
            heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; card.Children.Add(heading);
            if (!string.IsNullOrWhiteSpace(row.OriginalTitle) && row.OriginalTitle != row.Title) card.Children.Add(Text(row.OriginalTitle, 13));
            card.Children.Add(Text(UiText.Get(CatalogReleaseFacts.KindCaption(row.Kind)) + " · " + row.Kind + " · " + (row.Date ?? UiText.Get("日期未收录")), 13));
            card.Children.Add(Text(UiText.Get("语言 · ") + (row.Languages.Length > 0 ? string.Join(" / ", row.Languages) : UiText.Get("未收录")), 13));
            card.Children.Add(Text(UiText.Get("平台 · ") + (row.Platforms.Length > 0 ? string.Join(" / ", row.Platforms) : UiText.Get("未收录")), 13));
            var refs = new TagFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
            foreach (var reference in row.References)
            {
                var label = CatalogContentView.SiteName(reference.Source) + " · " + reference.Id;
                if (SafeSource(reference.Url))
                {
                    var link = new HyperlinkButton { Content = label, Padding = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
                    AutomationProperties.SetName(link, label + UiText.Get("，原始发行资料"));
                    link.Click += async (_, _) =>
                    {
                        var currentModel = model; var version = currentModel?.Version; var id = currentModel?.WorkId;
                        try { if (OpenLink is not null) await OpenLink(reference.Url!); }
                        catch { if (!disposed && ReferenceEquals(model, currentModel) && model?.Version == version && model?.WorkId == id) message.Text = "原始发行资料暂时无法打开，可稍后重试。"; }
                    };
                    refs.Children.Add(link);
                }
                else refs.Children.Add(Text(label, 12));
            }
            if (refs.Children.Count > 0) card.Children.Add(refs); else card.Children.Add(Text(UiText.Get("原始来源编号未收录"), 12));
            var identity = Text($"目录发行 {row.Id} · 所属作品 {row.WorkId}", 12); identity.Opacity = .72; card.Children.Add(identity);
            var border = new Border { Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(6),
                Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"], Child = card };
            AutomationProperties.SetAutomationId(border, "CatalogRelease-" + row.Id); rows.Children.Add(border);
        }
    }
    private static bool SafeSource(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Host is "vndb.org" or "bgm.tv" or "store.steampowered.com"
            or "erogamescape.dyndns.org" or "howlongtobeat.com" or "www.dlsite.com" or "www.getchu.com";
    private static TextBlock Text(string value, double size) => new() { Text = UiText.Get(value), FontSize = size,
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    internal object Evidence => new { workId = model?.WorkId, expanded = model?.Expanded, state = model?.State.ToString(),
        busy = model?.IsBusy, count = model?.Items.Length, next = model?.NextCursor, message = model?.Message,
        items = model?.Items.Select(row => new { row.Id, row.WorkId, row.Title, row.Kind, row.Date, row.Languages, row.Platforms,
            references = row.References.Select(reference => new { reference.Source, reference.Id }).ToArray() }).ToArray() };
    public void Dispose() { if (disposed) return; disposed = true; if (model is not null) { model.PropertyChanged -= StateChanged; model.Dispose(); } }
}
