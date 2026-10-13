using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TheOrder.WinUI.Views;

public sealed partial class LibraryPage
{
    internal async Task WaitForInitialArtworkAsync()
    {
        UpdateLayout();
        var pending = new List<Task>();
        void Visit(DependencyObject node)
        {
            if (node is CoverPresenter cover && cover.IsLoaded && cover.ActualWidth > 0 && cover.ActualHeight > 0)
            {
                var origin = cover.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (origin.Y < ActualHeight && origin.Y + cover.ActualHeight > 0 && origin.X < ActualWidth && origin.X + cover.ActualWidth > 0)
                    pending.Add(cover.PendingImage);
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(this);
        // A missing/slow remote image uses the existing placeholder. Offline startup must
        // not wait indefinitely for remote artwork, while cached/local first-screen images
        // normally settle under the loading surface.
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException) { }
    }
}
