using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TheOrder.WinUI.Views;

internal static class HeroTitleLayout
{
    // Fit the actual localized title, not a character-count estimate. The artwork keeps its aspect.
    internal static void Fit(TextBlock title, Border plate, double frameWidth, bool compact, bool dense = false)
    {
        var outer = compact ? 8d : dense ? 12d : 28d;
        var inner = compact ? 8d : dense ? 16d : 24d;
        var preferredFont = compact ? 20d : dense ? 28d : 32d;
        var readableFont = compact ? 18d : dense ? 24d : 28d;
        var minimumFont = compact ? 16d : 24d;
        var available = Math.Max(1, frameWidth - 2 * outer);
        var probe = new TextBlock { Text = title.Text, FontSize = preferredFont, FontFamily = title.FontFamily,
            FontWeight = title.FontWeight, FontStyle = title.FontStyle, FontStretch = title.FontStretch,
            CharacterSpacing = title.CharacterSpacing, TextWrapping = TextWrapping.NoWrap };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = probe.DesiredSize.Width;
        var normal = compact ? available : Math.Min(620, Math.Min(available, frameWidth * .72));
        // Long titles may use the whole frame before falling below a comfortable font size.
        plate.MaxWidth = Math.Min(available, Math.Max(normal, natural * readableFont / preferredFont + 2 * inner));
        plate.Padding = compact ? new Thickness(inner) : new Thickness(inner, dense ? 10 : 18, inner, dense ? 10 : 18);
        title.FontSize = natural > 0 ? Math.Clamp(preferredFont * Math.Max(1, plate.MaxWidth - 2 * inner) / natural, minimumFont, preferredFont) : preferredFont;
        title.TextWrapping = TextWrapping.NoWrap;
        title.MaxLines = 1;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        // Extremely long titles retain a readable minimum and expose their full text on hover/focus.
        ToolTipService.SetToolTip(title, title.Text);
    }
}
