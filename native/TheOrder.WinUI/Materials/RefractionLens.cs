using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace TheOrder.WinUI.Materials;

// Test-only affine RGB dispersion approximation over ONE opaque live source.
// Continuous edge opacity: four straight sections + four radial corner sections.
// Center is 1:1, foreground excluded. No device, timer, second capture or shader loop.
internal sealed class RefractionLens : IDisposable
{
    private readonly FrameworkElement source, host;
    private readonly MaterialResources resources = new();
    private CompositionVisualSurface? surface, maskSurface;
    private ContainerVisual? root, maskRoot;
    private CompositionSurfaceBrush? centerBrush, redBrush, greenBrush, blueBrush;
    private CompositionMaskBrush? masked;
    private CompositionEffectBrush? dispersed;
    private CompositionRoundedRectangleGeometry? perimeter;
    private readonly List<SpriteVisual> maskVisuals = [];
    private readonly List<CompositionGradientBrush> gradients = [];
    private bool disposed;
    private (float W, float H, float SW, float SH, float X, float Y, LensOptics Optics)? last;
    internal long UpdateCount { get; private set; }
    internal int CleanupFailures => resources.CleanupFailures;
    internal int RemainingResources => resources.Remaining;
    internal int SurfaceCount => disposed ? 0 : 2;
    internal int BrushCount => disposed ? 0 : 15;
    internal int VisualCount => disposed ? 0 : 12;
    internal int EffectFactoryCount => disposed ? 0 : 1;
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
            for (var i = 0; i < 8; i++)
            {
                CompositionGradientBrush gradient;
                if (i < 4)
                {
                    var linear = resources.Own(c.CreateLinearGradientBrush());
                    linear.StartPoint = Vector2.Zero; linear.EndPoint = i < 2 ? Vector2.UnitX : Vector2.UnitY;
                    if (i is 1 or 3) (linear.StartPoint, linear.EndPoint) = (linear.EndPoint, linear.StartPoint);
                    gradient = linear;
                }
                else
                {
                    var radial = resources.Own(c.CreateRadialGradientBrush());
                    radial.EllipseCenter = i == 4 ? new(1, 1) : i == 5 ? new(0, 1) : i == 6 ? new(1, 0) : Vector2.Zero;
                    radial.GradientOriginOffset = Vector2.Zero; radial.EllipseRadius = Vector2.One;
                    gradient = radial;
                }
                for (var j = 0; j <= 6; j++)
                    gradient.ColorStops.Add(resources.Own(c.CreateColorGradientStop(j / 6f,
                        Color.FromArgb((byte)Math.Round(255 * LensOptics.EdgeOpacity(i < 4 ? j / 6f : 1 - j / 6f)), 255, 255, 255))));
                gradients.Add(gradient);
                var v = resources.Own(c.CreateSpriteVisual()); v.Brush = gradient;
                maskVisuals.Add(v); maskRoot.Children.InsertAtTop(v);
            }
            maskSurface = resources.Own(c.CreateVisualSurface()); maskSurface.SourceVisual = maskRoot;
            centerBrush = resources.Own(c.CreateSurfaceBrush(surface));
            redBrush = resources.Own(c.CreateSurfaceBrush(surface));
            greenBrush = resources.Own(c.CreateSurfaceBrush(surface));
            blueBrush = resources.Own(c.CreateSurfaceBrush(surface));
            foreach (var brush in new[] { centerBrush, redBrush, greenBrush, blueBrush })
            {
                brush.Stretch = CompositionStretch.None;
                brush.HorizontalAlignmentRatio = 0; brush.VerticalAlignmentRatio = 0;
                brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            }
            var mask = resources.Own(c.CreateSurfaceBrush(maskSurface)); mask.Stretch = CompositionStretch.Fill;
            var factory = resources.Own(ChannelEffect.CreateFactory(c));
            dispersed = resources.Own(factory.CreateBrush());
            dispersed.SetSourceParameter("red", redBrush); dispersed.SetSourceParameter("green", greenBrush); dispersed.SetSourceParameter("blue", blueBrush);
            masked = resources.Own(c.CreateMaskBrush()); masked.Source = greenBrush; masked.Mask = mask;
            root = resources.Own(c.CreateContainerVisual());
            perimeter = resources.Own(c.CreateRoundedRectangleGeometry());
            perimeter.CornerRadius = new Vector2(23);
            root.Clip = resources.Own(c.CreateGeometricClip(perimeter));
            var center = resources.Own(c.CreateSpriteVisual()); center.Brush = centerBrush;
            var edge = resources.Own(c.CreateSpriteVisual()); edge.Brush = masked;
            root.Children.InsertAtBottom(center); root.Children.InsertAtTop(edge);
            ElementCompositionPreview.SetElementChildVisual(host, root);
            Update(LensOptics.Recommended);
        }
        catch { Dispose(); throw; }
    }

    internal void Update(LensOptics optics)
    {
        if (disposed || root is null || surface is null || maskRoot is null || maskSurface is null) return;
        var w = (float)host.ActualWidth; var h = (float)host.ActualHeight;
        var sw = (float)source.ActualWidth; var sh = (float)source.ActualHeight;
        if (w <= 0 || h <= 0 || sw <= 0 || sh <= 0) return;
        var p = host.TransformToVisual(source).TransformPoint(new Point());
        optics = optics.Bounded();
        var current = (w, h, sw, sh, (float)p.X, (float)p.Y, optics);
        if (last == current) return;
        last = current; UpdateCount++;
        root.Size = maskRoot.Size = new(w, h); perimeter!.Size = root.Size;
        surface.SourceSize = new(sw, sh); surface.SourceOffset = Vector2.Zero;
        maskSurface.SourceSize = root.Size;
        const float radius = 23;
        var edgeWidth = Math.Min(optics.EdgeWidth, Math.Min(w, h) / 3);
        for (var i = 0; i < 8; i++)
        {
            maskVisuals[i].Size = i < 2 ? new(edgeWidth, h - 2 * radius) : i < 4 ? new(w - 2 * radius, edgeWidth) : new(radius, radius);
            maskVisuals[i].Offset = i switch {
                0 => new(0, radius, 0), 1 => new(w - edgeWidth, radius, 0),
                2 => new(radius, 0, 0), 3 => new(radius, h - edgeWidth, 0),
                4 => Vector3.Zero, 5 => new(w - radius, 0, 0),
                6 => new(0, h - radius, 0), _ => new(w - radius, h - radius, 0) };
            if (i >= 4) for (var j = 0; j <= 6; j++) gradients[i].ColorStops[j].Offset = 1 - edgeWidth / radius + edgeWidth / radius * j / 6;
        }
        foreach (SpriteVisual child in root.Children) child.Size = root.Size;
        centerBrush!.TransformMatrix = Matrix3x2.CreateTranslation(-(float)p.X, -(float)p.Y);
        SetSample(redBrush!, 1); SetSample(greenBrush!, 0); SetSample(blueBrush!, -1);
        masked!.Source = optics.Dispersion > 0 ? dispersed : greenBrush;
        void SetSample(CompositionSurfaceBrush brush, int channel)
        {
            var x = optics.Scale(w / 2, channel); var y = optics.Scale(h / 2, channel);
            brush.TransformMatrix = new(x, 0, 0, y, w / 2 - x * ((float)p.X + w / 2), h / 2 - y * ((float)p.Y + h / 2));
        }
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
            centerBrush = redBrush = greenBrush = blueBrush = null; masked = null; dispersed = null;
            perimeter = null; maskVisuals.Clear(); gradients.Clear();
        }
    }
}
