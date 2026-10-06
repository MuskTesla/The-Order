using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace TheOrder.WinUI.Materials;

/// <summary>
/// Development Gallery only: a public-API, live internal-content geometric lens.
/// Nested rounded clips vary magnification near the edge. This is a bounded,
/// piecewise approximation, not a custom optical shader or desktop capture.
/// The source subtree must never include the lens or its stable foreground.
/// </summary>
internal sealed class RefractionLens : IDisposable
{
    private const int Bands = 18;
    private readonly FrameworkElement source;
    private readonly FrameworkElement host;
    private readonly List<SpriteVisual> visuals = [];
    private readonly List<CompositionSurfaceBrush> brushes = [];
    private readonly List<CompositionRoundedRectangleGeometry> geometries = [];
    private readonly List<CompositionGeometricClip> clips = [];
    private CompositionVisualSurface? surface;
    private ContainerVisual? root;
    private bool disposed;
    private double strength = 1;

    internal RefractionLens(FrameworkElement source, FrameworkElement host)
    {
        this.source = source;
        this.host = host;
        try
        {
            // The XAML-owned compositor owns its device recovery. Do not dispose it.
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            surface = compositor.CreateVisualSurface();
            surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source);
            root = compositor.CreateContainerVisual();
            for (var i = 0; i < Bands; i++)
            {
                var brush = compositor.CreateSurfaceBrush(surface);
                brushes.Add(brush);
                brush.Stretch = CompositionStretch.None;
                brush.HorizontalAlignmentRatio = 0;
                brush.VerticalAlignmentRatio = 0;
                brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
                var geometry = compositor.CreateRoundedRectangleGeometry();
                geometries.Add(geometry);
                var clip = compositor.CreateGeometricClip(geometry);
                clips.Add(clip);
                var visual = compositor.CreateSpriteVisual();
                visuals.Add(visual);
                visual.Brush = brush;
                visual.Clip = clip;
                root.Children.InsertAtTop(visual);
            }
            ElementCompositionPreview.SetElementChildVisual(host, root);
            Update(1);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int SurfaceCount => disposed ? 0 : surface is null ? 0 : 1;
    internal int BrushCount => disposed ? 0 : brushes.Count;
    internal int VisualCount => disposed ? 0 : visuals.Count + (root is null ? 0 : 1);
    internal int ClipCount => disposed ? 0 : clips.Count;
    internal int GeometryCount => disposed ? 0 : geometries.Count;

    internal void Update(double newStrength)
    {
        if (disposed || root is null || surface is null) return;
        strength = Math.Clamp(newStrength, 0, 1.5);
        var width = (float)host.ActualWidth;
        var height = (float)host.ActualHeight;
        if (width <= 0 || height <= 0 || source.ActualWidth <= 0 || source.ActualHeight <= 0) return;
        var offset = host.TransformToVisual(source).TransformPoint(new Point(0, 0));
        root.Size = new Vector2(width, height);
        surface.SourceOffset = Vector2.Zero;
        surface.SourceSize = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
        var center = new Vector2((float)offset.X + width / 2, (float)offset.Y + height / 2);
        for (var i = 0; i < Bands; i++)
        {
            var inset = i * .8f;
            var distance = i / (float)(Bands - 1);
            // A quiet center and a stronger perimeter. Each band samples live pixels.
            var magnification = 1 + (float)strength * (.012f + .085f * MathF.Pow(1 - distance, 2));
            brushes[i].TransformMatrix = new Matrix3x2(magnification, 0, 0, magnification,
                width / 2 - magnification * center.X, height / 2 - magnification * center.Y);
            visuals[i].Size = root.Size;
            geometries[i].Offset = new Vector2(inset);
            geometries[i].Size = new Vector2(Math.Max(1, width - inset * 2), Math.Max(1, height - inset * 2));
            geometries[i].CornerRadius = new Vector2(Math.Max(2, 20 - inset));
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Remove the child tree before releasing sampled surface and brush resources.
        try { ElementCompositionPreview.SetElementChildVisual(host, null); } catch (Exception) { }
        if (root is not null)
        {
            try { root.Children.RemoveAll(); } catch (Exception) { }
        }
        foreach (var visual in visuals)
        {
            try { visual.Brush = null; visual.Clip = null; } catch (Exception) { }
            try { visual.Dispose(); } catch (Exception) { }
        }
        foreach (var brush in brushes)
        {
            try { brush.Surface = null; } catch (Exception) { }
            try { brush.Dispose(); } catch (Exception) { }
        }
        foreach (var clip in clips) { try { clip.Dispose(); } catch (Exception) { } }
        foreach (var geometry in geometries) { try { geometry.Dispose(); } catch (Exception) { } }
        if (surface is not null)
        {
            try { surface.SourceVisual = null; } catch (Exception) { }
            try { surface.Dispose(); } catch (Exception) { }
        }
        try { root?.Dispose(); } catch (Exception) { }
        root = null;
        surface = null;
        visuals.Clear(); brushes.Clear(); geometries.Clear(); clips.Clear();
    }
}
