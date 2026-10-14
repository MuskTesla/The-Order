using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TheOrder.WinUI.Views;

// Read-only evidence, invoked solely by the existing isolated visual fixture.
internal static class InteractiveCardEvidence
{
    internal static object Hit(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
    {
        var content = button.Content as UIElement;
        var root = button.XamlRoot?.Content;
        var label = Label(content);
        bool? buttonAtText = null;
        if (root is not null && label is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            var point = label.TransformToVisual(root).TransformPoint(new(Math.Min(8, label.ActualWidth / 2), label.ActualHeight / 2));
            if (point.X >= 0 && point.Y >= 0 && point.X < ((FrameworkElement)root).ActualWidth && point.Y < ((FrameworkElement)root).ActualHeight)
                buttonAtText = VisualTreeHelper.FindElementsInHostCoordinates(point, root).Contains(button);
        }
        return new { id = AutomationProperties.GetAutomationId(button), contentAcceptsPointer = content?.IsHitTestVisible, buttonAtText, button.IsEnabled };
    }
    private static TextBlock? Label(DependencyObject? value)
    {
        if (value is TextBlock text) return text;
        if (value is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) if (Label(VisualTreeHelper.GetChild(value, i)) is { } found) return found;
        return null;
    }
}
