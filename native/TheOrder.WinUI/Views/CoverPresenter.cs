using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public interface IImageHost { IImagePipeline Images { get; } }
public sealed class CoverPresenter : Grid
{
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly FontIcon placeholder = new() { Glyph = "\uE8F1", FontSize = 32 };
    private readonly ImageRequestSlot request = new();
    private bool loaded;
    private string? requestedKey;
    private int requestedEdge;
    public static readonly DependencyProperty SourceKeyProperty = DependencyProperty.Register(nameof(SourceKey), typeof(string), typeof(CoverPresenter), new PropertyMetadata(null, OnSourceChanged));
    public string? SourceKey { get => (string?)GetValue(SourceKeyProperty); set => SetValue(SourceKeyProperty, value); }
    public int MinimumEdge { get; set; } = 16;
    // Only picker previews use containment. Existing Library/Details artwork is unchanged.
    public bool PreserveEntireImage { get => image.Stretch == Stretch.Uniform; set => image.Stretch = value ? Stretch.Uniform : Stretch.UniformToFill; }
    public CoverPresenter()
    {
        Children.Add(placeholder); Children.Add(image);
        Loaded += (_, _) => { loaded = true; Start(); };
        Unloaded += (_, _) => { loaded = false; Reset(); };
        SizeChanged += (_, _) => { if (loaded) Start(); };
    }
    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var presenter = (CoverPresenter)sender;
        // Invalidate the old generation immediately, including while layout is zero-sized.
        if ((string?)args.OldValue != (string?)args.NewValue) presenter.Reset();
        presenter.Start();
    }
    internal bool PerformanceSourceCurrent => image.Source is null || requestedKey == SourceKey;
    private void Reset() { requestedKey = null; requestedEdge = 0; request.Clear(); image.Source = null; placeholder.Visibility = Visibility.Visible; }
    private void Start()
    {
        if (!loaded || SourceKey is null) { Reset(); return; }
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        DependencyObject? ancestor = this;
        while (ancestor is not null && ancestor is not IImageHost) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is not IImageHost host) return;
        var key = SourceKey;
        var edge = ThumbnailSizing.Edge(ActualWidth, ActualHeight, XamlRoot?.RasterizationScale ?? 1, MinimumEdge);
        if (requestedKey == key && requestedEdge == edge) return;
        Reset(); requestedKey = key; requestedEdge = edge;
        var lease = request.Begin(key);
        _ = LoadAsync(host.Images, key, edge, lease.Generation, lease.Cancellation);
    }
    private async Task LoadAsync(IImagePipeline pipeline, string key, int edge, long version, CancellationToken cancellation)
    {
        try
        {
            var result = await pipeline.LoadAsync(key, edge, cancellation);
            if (!loaded || !request.IsCurrent(key, version) || cancellation.IsCancellationRequested || SourceKey != key || result.Pixels is not { } pixels) return;
            image.Source = DisplayBitmaps.Get(pipeline, pixels); placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { if (request.IsCurrent(key, version)) { image.Source = null; placeholder.Visibility = Visibility.Visible; } }
    }
}
