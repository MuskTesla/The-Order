using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TheOrder.Domain;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

// The same read-only, current-version facts sit on the CG in Library and catalog details.
// Short landscapes place fact groups side by side to preserve the image overlay.
public sealed class CatalogFactsView : StackPanel
{
    private WorkMetadata? work;
    private string? displayedTitle;
    private bool compact;
    private bool dense;
    public bool Dense { get => dense; set { if (dense == value) return; dense = value; Spacing = value ? 3 : 5; Render(); } }
    private Brush? textForeground;
    public void SetTextForeground(Brush? brush)
    {
        textForeground = brush;
        foreach (var text in PlainTexts(this))
            if (brush is null) text.ClearValue(TextBlock.ForegroundProperty); else text.Foreground = brush;
    }
    private static IEnumerable<TextBlock> PlainTexts(Panel parent)
    {
        foreach (var child in parent.Children)
            if (child is TextBlock text) yield return text;
            else if (child is Panel panel) foreach (var nested in PlainTexts(panel)) yield return nested;
            else if (child is HyperlinkButton { Content: TextBlock label }) yield return label;
    }
    public Func<CatalogEntityRef, Task>? Browse { get; set; }
    public bool Compact
    {
        get => compact;
        set { if (compact == value) return; compact = value; Render(); }
    }
    public CatalogFactsView() { Spacing = 5; }
    public void SetWork(WorkMetadata? value, string? title = null)
    {
        work = value is null ? null : CatalogLanguage.Project(value); displayedTitle = title; Render();
    }
    private TextBlock Text(string value, double? size = null)
    {
        var text = new TextBlock
        {
        Text = value, FontSize = size ?? (compact ? 12 : dense ? 13 : 15), TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = compact ? 1 : 3,
        IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center
        };
        if (textForeground is not null) text.Foreground = textForeground;
        return text;
    }
    private UIElement Entity(CatalogEntityRef entity)
    {
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = entity.Name, FontSize = compact ? 12 : dense ? 13 : 15,
                MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            Padding = new Thickness(0, 0, 10, 0), MinHeight = 0, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, entity.Name + UiText.Get("，资料详情"));
        ToolTipService.SetToolTip(link, entity.Name);
        if (textForeground is not null && link.Content is TextBlock text) text.Foreground = textForeground;
        link.IsEnabled = TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(entity.Id);
        link.Click += async (_, _) => { if (Browse is not null) await Browse(entity); };
        return link;
    }
    private void Render()
    {
        Children.Clear(); Visibility = work is null ? Visibility.Collapsed : Visibility.Visible;
        if (work is null) return;
        var names = new[] { work.Title, work.OriginalTitle }.Where(n => !string.IsNullOrWhiteSpace(n) && n != displayedTitle).Distinct();
        var alternate = string.Join(" / ", names);
        if (!compact && alternate.Length > 0) Children.Add(Text(alternate, dense ? 12 : 14));
        var companies = work.Presentation?.Companies ?? [];
        // Fact groups may use the actual plate width. The old 260-DIP outer cap
        // wrapped engines into a tall mini-column and vertically centered dates
        // beside it. Long groups now wrap as a whole, with their tops aligned.
        var factFlow = compact || dense ? new TagFlowPanel { HorizontalSpacing = 16, VerticalSpacing = 3,
            MaximumItemWidth = double.PositiveInfinity, CenterInlineItems = false } : null;
        void AddFact(UIElement element) { if (factFlow is null) Children.Add(element); else factFlow.Children.Add(element); }
        var developerNames = work.Developers.IsEmpty ? companies.Select(c => c.Name).ToArray() : work.Developers.ToArray();
        if (developerNames.Length > 0)
        {
            var row = new TagFlowPanel(); row.Children.Add(Text(work.Developers.IsEmpty ? UiText.Get("公司 · ") : UiText.Get("制作 · ")));
            foreach (var name in developerNames.Take(compact ? 1 : 6))
            {
                var company = companies.FirstOrDefault(c => c.Name == name);
                row.Children.Add(company is null ? Text(name) : Entity(company));
            }
            ToolTipService.SetToolTip(row, string.Join(" / ", developerNames)); AddFact(row);
        }
        if (!string.IsNullOrWhiteSpace(work.ReleaseDate)) AddFact(Text(UiText.Get("发行 · ") + work.ReleaseDate));
        if (work.Presentation is { Engines.IsEmpty: false } p)
        {
            var engines = new TagFlowPanel(); engines.Children.Add(Text(UiText.Get("引擎 · ")));
            foreach (var engine in p.Engines.Take(compact ? 1 : 4)) engines.Children.Add(Entity(engine));
            ToolTipService.SetToolTip(engines, UiText.Get("引擎 · ") + string.Join(" / ", p.Engines.Select(e => e.Name)));
            AddFact(engines);
        }
        if (factFlow is { Children.Count: > 0 }) Children.Add(factFlow);
        if (!work.Ratings.IsEmpty)
        {
            var ratings = work.Ratings.OrderBy(r => r.Source is "curated" or "kungal" or "nextmoe" ? 0 : 1).ToArray();
            var scores = new TagFlowPanel { HorizontalSpacing = compact ? 12 : 18, VerticalSpacing = 4 };
            foreach (var rating in ratings.Take(compact ? 2 : 6))
            {
                var text = Text($"{CatalogContentView.SiteName(rating.Source)}  {rating.Score:0.#}", compact || dense ? 12 : 14);
                ToolTipService.SetToolTip(text, UiText.Format($"本版本评分 · {rating.Score:0.#}/{rating.Scale:0.#}") +
                    (rating.Votes is { } votes ? UiText.Format($"（{votes:0} 人）") : ""));
                scores.Children.Add(text);
            }
            Children.Add(scores);
        }
    }
    internal object Evidence => new { workId = work?.Id, compact, width = ActualWidth, height = ActualHeight,
        rows = Children.Count, currentVersionOnly = true,
        inlineRows = Children.OfType<TagFlowPanel>().Select(row => new { items = row.Children.OfType<FrameworkElement>().Select(e => new {
            kind = e.GetType().Name,
            text = e is TextBlock t ? t.Text : e is HyperlinkButton { Content: TextBlock c } ? c.Text : "",
            width = e.ActualWidth, height = e.ActualHeight,
            centerY = e.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point(0, e.ActualHeight / 2)).Y
        }).ToArray() }).ToArray(),
        foreground = (textForeground as SolidColorBrush)?.Color.ToString(),
        textColors = PlainTexts(this).Select(t => (t.Foreground as SolidColorBrush)?.Color.ToString()).Distinct().ToArray() };
}
