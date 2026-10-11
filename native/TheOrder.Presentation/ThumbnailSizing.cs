namespace TheOrder.Presentation;

public static class ThumbnailSizing
{
    // Stable buckets avoid a new decode for every layout pixel; DPI still controls actual raster density.
    public static int Edge(double width, double height, double scale, int minimum = 16)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale) || scale <= 0) return 64;
        double pixels = Math.Max(minimum, Math.Max(width, height) * scale);
        return (int)Math.Clamp(Math.Ceiling(pixels / 64) * 64, 64, 1600);
    }
}
