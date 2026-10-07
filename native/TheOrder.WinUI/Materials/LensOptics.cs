namespace TheOrder.WinUI.Materials;

// Test-only review settings. Pure bounds/projection shared with relevant tests.
internal readonly record struct LensOptics(float Refraction, float Dispersion, float EdgeWidth, float Highlight)
{
    internal static LensOptics Recommended => new(1, .75f, 18, .45f);
    internal LensOptics Bounded() => new(Bound(Refraction, 0, 1.5f, 1), Bound(Dispersion, 0, 2, .75f), Bound(EdgeWidth, 8, 22, 18), Bound(Highlight, 0, 1, .45f));
    private static float Bound(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    internal float Scale(float halfExtent, int channel)
    {
        var p = Bounded();
        if (halfExtent <= 0 || !float.IsFinite(halfExtent)) return 1;
        // Affine spectral approximation, not a physical ray tracer/SDF shader.
        // At the perimeter red/blue differ by at most 2 * Dispersion DIP.
        return Math.Max(1, 1 + p.Refraction * .035f + channel * p.Dispersion / halfExtent);
    }
    internal static float EdgeOpacity(float inwardFraction)
    {
        var t = Math.Clamp(inwardFraction, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }
}
