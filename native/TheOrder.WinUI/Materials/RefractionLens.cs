using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace TheOrder.WinUI.Materials;

// Test-only continuous alpha transition between two live affine samples.
// Quiet 1:1 center, magnified edge, one rounded perimeter. Cross-fade can soften
// fine lines; this is an approximation, not a displacement shader. Source trees
// exclude the output and stable foreground. No timer or capture loop.
internal sealed class RefractionLens : IDisposable
{
    private readonly FrameworkElement source, host;
    private readonly MaterialResources resources = new();
    private CompositionVisualSurface? surface, maskSurface;
    private ContainerVisual? root, maskRoot;
    private CompositionSurfaceBrush? centerBrush, edgeBrush;
    private CompositionRoundedRectangleGeometry? perimeter;
    private readonly List<SpriteVisual> maskVisuals = [];
    private bool disposed;
    private (float W, float H, float SW, float SH, float X, float Y, float Strength)? last;
    internal long UpdateCount { get; private set; }
    internal int CleanupFailures => resources.CleanupFailures;
    internal int RemainingResources => resources.Remaining;
    internal int SurfaceCount => disposed ? 0 : 2;
    internal int BrushCount => disposed ? 0 : 8;
    internal int VisualCount => disposed ? 0 : 8;
    internal int ClipCount => disposed ? 0 : 1;
    internal int GeometryCount => disposed ? 0 : 1;

    internal RefractionLens(FrameworkElement source, FrameworkElement host, bool failAfterSource = false)
    {
        this.source = source; this.host = host;
        try
        {
            var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
            surface = resources.Own(c.CreateVisualSurface());
            surface.SourceVisual = ElementCompositionPreview.GetElementVisual(source);
            if (failAfterSource) throw new InvalidOperationException("Test partial material initialization failure");
            maskRoot = resources.Own(c.CreateContainerVisual());
            for (var i = 0; i < 4; i++)
            {
                var gradient = resources.Own(c.CreateLinearGradientBrush());
                gradient.StartPoint = Vector2.Zero;
                gradient.EndPoint = i < 2 ? Vector2.UnitX : Vector2.UnitY;
                gradient.ColorStops.Add(resources.Own(c.CreateColorGradientStop(0, Color.FromArgb(255, 255, 255, 255))));
                gradient.ColorStops.Add(resources.Own(c.CreateColorGradientStop(.12f, Color.FromArgb(255, 255, 255, 255))));
                gradient.ColorStops.Add(resources.Own(c.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255))));
                if (i is 1 or 3) (gradient.StartPoint, gradient.EndPoint) = (gradient.EndPoint, gradient.StartPoint);
                var v = resources.Own(c.CreateSpriteVisual()); v.Brush = gradient;
                maskVisuals.Add(v); maskRoot.Children.InsertAtTop(v);
            }
            maskSurface = resources.Own(c.CreateVisualSurface()); maskSurface.SourceVisual = maskRoot;
            centerBrush = resources.Own(c.CreateSurfaceBrush(surface));
            edgeBrush = resources.Own(c.CreateSurfaceBrush(surface));
            foreach (var brush in new[] { centerBrush, edgeBrush })
            {
                brush.Stretch = CompositionStretch.None;
                brush.HorizontalAlignmentRatio = 0; brush.VerticalAlignmentRatio = 0;
                brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            }
            var mask = resources.Own(c.CreateSurfaceBrush(maskSurface)); mask.Stretch = CompositionStretch.Fill;
            var masked = resources.Own(c.CreateMaskBrush()); masked.Source = edgeBrush; masked.Mask = mask;
            root = resources.Own(c.CreateContainerVisual());
            perimeter = resources.Own(c.CreateRoundedRectangleGeometry());
            perimeter.CornerRadius = new Vector2(19);
            root.Clip = resources.Own(c.CreateGeometricClip(perimeter));
            var center = resources.Own(c.CreateSpriteVisual()); center.Brush = centerBrush;
            var edge = resources.Own(c.CreateSpriteVisual()); edge.Brush = masked;
            root.Children.InsertAtBottom(center); root.Children.InsertAtTop(edge);
            ElementCompositionPreview.SetElementChildVisual(host, root);
            Update(1);
        }
        catch { Dispose(); throw; }
    }

    internal void Update(double strength)
    {
        if (disposed || root is null || surface is null || maskRoot is null || maskSurface is null) return;
        var w = (float)host.ActualWidth; var h = (float)host.ActualHeight;
        var sw = (float)source.ActualWidth; var sh = (float)source.ActualHeight;
        if (w <= 0 || h <= 0 || sw <= 0 || sh <= 0) return;
        var p = host.TransformToVisual(source).TransformPoint(new Point());
        var current = (w, h, sw, sh, (float)p.X, (float)p.Y, (float)Math.Clamp(strength, 0, 1.5));
        if (last == current) return;
        last = current; UpdateCount++;
        root.Size = maskRoot.Size = new(w, h); perimeter!.Size = root.Size;
        surface.SourceSize = new(sw, sh); surface.SourceOffset = Vector2.Zero;
        maskSurface.SourceSize = root.Size;
        var edgeWidth = Math.Min(24, Math.Min(w, h) / 3);
        for (var i = 0; i < 4; i++)
        {
            maskVisuals[i].Size = i < 2 ? new(edgeWidth, h) : new(w, edgeWidth);
            maskVisuals[i].Offset = i == 1 ? new(w - edgeWidth, 0, 0) : i == 3 ? new(0, h - edgeWidth, 0) : Vector3.Zero;
        }
        foreach (SpriteVisual child in root.Children) child.Size = root.Size;
        centerBrush!.TransformMatrix = Matrix3x2.CreateTranslation(-(float)p.X, -(float)p.Y);
        var magnification = 1 + current.Item7 * .026f;
        edgeBrush!.TransformMatrix = new(magnification, 0, 0, magnification,
            w / 2 - magnification * ((float)p.X + w / 2), h / 2 - magnification * ((float)p.Y + h / 2));
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            try { ElementCompositionPreview.SetElementChildVisual(host, null); }
            catch (Exception) { /* Close failures below remain observable. */ }
        }
        resources.Dispose();
        if (resources.Remaining == 0)
        {
            surface = maskSurface = null; root = maskRoot = null;
            centerBrush = edgeBrush = null; perimeter = null; maskVisuals.Clear();
        }
    }
}
