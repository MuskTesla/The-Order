using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record CoverCandidate(WorkCover Cover, string Source, bool IsCatalogDefault);
public sealed record CoverAssessment(CoverCandidate Candidate, double Framing, double DetailPreserved,
    double Boundary, double Balance, double Resolution, double Score);
public sealed record CoverDecision(string Algorithm, string InputSignature, ImmutableArray<CoverAssessment> Assessments,
    string? SelectedUrl, bool Complete);

// A local bookshelf-composition heuristic, not a learned estimate of human taste.
// Only trusted game fronts enter it. No gallery-wide OCR/model/download pass.
public static class CoverAesthetics
{
    public const string Algorithm = "bookshelf-composition-v1";
    public const double Aspect = .75;
    public const int AnalysisEdge = 512;
    public const int MaximumCandidates = 3;
    public const double TieMargin = 4;

    public static WorkCover? CatalogDefault(WorkMetadata work) => work.CoverUrl is { Length: > 0 } url
        ? (work.Covers ?? []).FirstOrDefault(c => c.Url == url) ?? new("catalog:primary", url, null, null, null, null) : null;

    public static ImmutableArray<CoverCandidate> Candidates(WorkMetadata work, WorkCover? primary)
    {
        var rows = new List<CoverCandidate>();
        if (primary is not null) rows.Add(new(primary, "catalog", true));
        foreach (var source in new[] { "vndb:", "steam:" })
        {
            // A provider supplies one primary for this exact work. Ambiguous or
            // accumulated distinct primaries are not resolved by their size.
            var matches = (work.Covers ?? []).Take(500).Where(c => c.Id.StartsWith(source, StringComparison.Ordinal) && CoverQuality.IsVerifiedGameCover(c))
                .DistinctBy(c => c.Url).Take(2).ToArray();
            if (matches.Length == 1 && !rows.Any(c => c.Cover.Url == matches[0].Url))
                rows.Add(new(matches[0], source.TrimEnd(':'), false));
        }
        return [.. rows.Take(MaximumCandidates)];
    }

    public static string Signature(ImmutableArray<CoverCandidate> candidates) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Algorithm, Candidates = candidates })));

    public static CoverAssessment Assess(CoverCandidate candidate, RasterPixels pixels)
    {
        var crop = CoverCropping.Plan(pixels, Aspect);
        double retained = crop.Width * crop.Height / ((double)pixels.Width * pixels.Height);
        // Sample a bounded grid; use the same viewport as the displayed card.
        const int columns = 64, rows = 80;
        double total = 0, inside = 0, boundary = 0, cx = 0, cy = 0;
        double Pixel(int x, int y, int channel) => pixels.Bgra[(Math.Clamp(y, 0, pixels.Height - 1) * pixels.Width + Math.Clamp(x, 0, pixels.Width - 1)) * 4 + channel] / 255d;
        int dx = Math.Max(1, pixels.Width / columns), dy = Math.Max(1, pixels.Height / rows);
        for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
        {
            int sx = Math.Min(pixels.Width - 1, (int)((x + .5) * pixels.Width / columns));
            int sy = Math.Min(pixels.Height - 1, (int)((y + .5) * pixels.Height / rows));
            double energy = .004;
            for (int c = 0; c < 3; c++) energy += (Math.Abs(Pixel(sx + dx, sy, c) - Pixel(sx - dx, sy, c)) +
                Math.Abs(Pixel(sx, sy + dy, c) - Pixel(sx, sy - dy, c))) / 6;
            energy *= Pixel(sx, sy, 3);
            total += energy;
            double u = (sx - crop.X) / crop.Width, v = (sy - crop.Y) / crop.Height;
            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
            inside += energy; cx += u * energy; cy += v * energy;
            // Penalize busy NEW cut boundaries, not lettering at an untouched
            // source edge. This is detail preservation, not face recognition.
            if (crop.X > .5 && u < .04 || crop.X + crop.Width < pixels.Width - .5 && u > .96 ||
                crop.Y > .5 && v < .04 || crop.Y + crop.Height < pixels.Height - .5 && v > .96) boundary += energy;
        }
        double detail = total > 0 ? Math.Clamp(inside / total, 0, 1) : retained;
        double edge = inside > 0 ? 1 - Math.Clamp(boundary / inside * 4, 0, 1) : 1;
        // A broad central region includes thirds and asymmetric compositions.
        // Neither saturation nor edge quantity earns extra beauty points.
        double balance = inside <= 0 ? .5 : 1 - Math.Clamp(Math.Max(0, Math.Abs(cx / inside - .5) - .22) * 2 +
            Math.Max(0, Math.Abs(cy / inside - .5) - .28) * 2, 0, 1);
        int width = candidate.Cover.Width ?? pixels.Width, height = candidate.Cover.Height ?? pixels.Height;
        double usable = Math.Min(width, height * Aspect);
        // At ~360 physical card pixels, further resolution brings no selection
        // bonus. Originals remain untouched and are decoded for the actual DPI.
        double resolution = Math.Clamp(usable / 360, 0, 1);
        double score = 30 * retained + 25 * detail + 15 * edge + 10 * balance + 20 * resolution;
        return new(candidate, retained, detail, edge, balance, resolution, score);
    }

    public static CoverAssessment? Choose(IEnumerable<CoverAssessment> assessments, string? current)
    {
        var rows = assessments.Where(a => double.IsFinite(a.Score)).Take(MaximumCandidates).ToArray();
        var best = rows.OrderByDescending(a => a.Score).ThenBy(a => a.Candidate.Cover.Url, StringComparer.Ordinal).FirstOrDefault();
        if (best is null) return null;
        // Human curation is a useful prior when heuristic differences are small.
        var primary = rows.FirstOrDefault(a => a.Candidate.IsCatalogDefault);
        if (primary is not null && best.Score - primary.Score <= TieMargin) return primary;
        var previous = rows.FirstOrDefault(a => a.Candidate.Cover.Url == current);
        return previous is not null && best.Score - previous.Score <= 2 ? previous : best;
    }
}
