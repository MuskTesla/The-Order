using System.Collections.Immutable;

namespace TheOrder.Application;

// Geometry only: neither recognized strings nor source image content are stored.
public readonly record struct ArtworkTextRegion(double X, double Y, double Width, double Height, double Confidence);
public sealed record ArtworkTextDetection(bool Available, ImmutableArray<ArtworkTextRegion> Regions, string Status);
public interface IArtworkTextDetector : IAsyncDisposable
{
    Task<ArtworkTextDetection> AnalyzeAsync(RasterPixels pixels, CancellationToken cancellation);
}
public static class ArtworkTextRules
{
    public static bool ContainsOverlayText(ArtworkTextDetection result)
    {
        var regions = result.Regions.Where(r => double.IsFinite(r.Confidence) && r.Confidence >= .5 &&
            r.Width >= .012 && r.Height >= .008 && r.Width * r.Height >= .00015).ToArray();
        // A single small edge copyright stamp is not a dialogue box. Never exempt a menu row,
        // subtitles in the body, multiple labels, or a large logo; no work/title/URL exceptions.
        return regions.Length > 0 && !(regions.Length == 1 && regions[0] is { Y: >= .95, Width: <= .30, Height: <= .028 } stamp &&
            (stamp.X <= .03 || stamp.X + stamp.Width >= .95));
    }
}
