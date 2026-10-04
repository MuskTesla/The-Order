using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    public static readonly DependencyProperty SourceKeyProperty = DependencyProperty.Register(nameof(SourceKey), typeof(string), typeof(CoverPresenter), new PropertyMetadata(null, OnSourceChanged));
    public string? SourceKey { get => (string?)GetValue(SourceKeyProperty); set => SetValue(SourceKeyProperty, value); }
    public int MinimumEdge { get; set; } = 16;
    public CoverPresenter()
    {
        Children.Add(placeholder); Children.Add(image);
        Loaded += (_, _) => { loaded = true; Start(); };
        Unloaded += (_, _) => { loaded = false; Reset(); };
        SizeChanged += (_, _) => { if (loaded) Start(); };
    }
    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((CoverPresenter)sender).Start();
    private void Reset() { request.Clear(); image.Source = null; placeholder.Visibility = Visibility.Visible; }
    private void Start()
    {
        Reset(); if (!loaded || SourceKey is null) return;
        DependencyObject? ancestor = this;
        while (ancestor is not null && ancestor is not IImageHost) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is not IImageHost host) return;
        var key = SourceKey; var lease = request.Begin(key);
        var edge = Math.Clamp((int)Math.Ceiling(Math.Max(MinimumEdge, Math.Max(ActualWidth, ActualHeight) * (XamlRoot?.RasterizationScale ?? 1)) / 64) * 64, 64, 1600);
        _ = LoadAsync(host.Images, key, edge, lease.Generation, lease.Cancellation);
    }
    private async Task LoadAsync(IImagePipeline pipeline, string key, int edge, long version, CancellationToken cancellation)
    {
        try
        {
            var result = await pipeline.LoadAsync(key, edge, cancellation);
            if (!loaded || !request.IsCurrent(key, version) || cancellation.IsCancellationRequested || SourceKey != key || result.Pixels is not { } pixels) return;
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(pixels.Bgra);
            bitmap.Invalidate(); image.Source = bitmap; placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { if (request.IsCurrent(key, version)) { image.Source = null; placeholder.Visibility = Visibility.Visible; } }
    }
}
