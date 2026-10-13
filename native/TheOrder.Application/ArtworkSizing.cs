namespace TheOrder.Application;

public static class ArtworkSizing
{
    public const int MaximumEdge = 3840;
    // Only large artwork uses these buckets; covers keep their small thumbnail buckets.
    public static int Edge(double width, double height, double scale, int minimum = 1200)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale) || scale <= 0)
            return Math.Clamp(minimum, 512, MaximumEdge);
        var required = Math.Clamp(Math.Max(minimum, Math.Max(width, height) * scale), 512, MaximumEdge);
        foreach (var edge in new[] { 1200, 1600, 1920, 2560, MaximumEdge })
            if (edge >= required) return edge;
        return MaximumEdge;
    }
}
