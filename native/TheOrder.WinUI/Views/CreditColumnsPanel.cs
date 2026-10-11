using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TheOrder.WinUI.Views;

// Bounded role cards occupy the shortest column. Long credit groups do not leave
// full-width blank rows, and a narrow window naturally becomes a single column.
internal sealed class CreditColumnsPanel : Panel
{
    private const double Gap = 12;
    private static int Columns(double width) => double.IsFinite(width) ? Math.Clamp((int)((width + Gap) / 300), 1, 3) : 1;
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 300;
        var columns = Columns(width); var cardWidth = Math.Max(1, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        foreach (var child in Children)
        {
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            var column = Array.IndexOf(heights, heights.Min());
            heights[column] += child.DesiredSize.Height + Gap;
        }
        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }
    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width); var cardWidth = Math.Max(1, (final.Width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        foreach (var child in Children)
        {
            var column = Array.IndexOf(heights, heights.Min());
            child.Arrange(new Rect(column * (cardWidth + Gap), heights[column], cardWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }
        return final;
    }
}
