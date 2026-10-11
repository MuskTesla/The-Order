using TheOrder.Domain;

namespace TheOrder.Application;

// A catalog's cover gallery also contains discs, back scans, books and console
// boxes. Pixels/votes/aspect ratio cannot establish that any of those is a game
// front. Only exact-work primary VNDB art and Steam library posters may replace
// the catalog's declared primary image automatically. Personal choices bypass this.
public static class CoverQuality
{
    public static string? Select(IEnumerable<WorkCover> covers, string? current, bool includeExplicit = true)
    {
        var rows = covers.Take(500).Where(c => includeExplicit || c.Sexual != "explicit").ToArray();
        var known = rows.FirstOrDefault(c => c.Url == current);
        var best = rows.Where(IsVerifiedGameCover)
            .OrderByDescending(Score).ThenByDescending(c => c.Url == current).FirstOrDefault();
        // Legacy automatic selections may be a huge disc/back scan: their size
        // must never prevent recovery to the exact work's primary cover.
        return best is not null && (known is null || !IsVerifiedGameCover(known) || Score(best) > Score(known) * 1.18)
            ? best.Url : current;
    }
    public static bool IsVerifiedGameCover(WorkCover cover)
    {
        if (cover.Width is not > 0 || cover.Height is not > 0 || cover.Width > 65535 || cover.Height > 65535 ||
            (long)cover.Width * cover.Height > 40_000_000 || cover.Width / (double)cover.Height is < .4 or > 1.8 ||
            !Uri.TryCreate(cover.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (cover.Id.StartsWith("vndb:cv", StringComparison.Ordinal))
        {
            var number = cover.Id[7..];
            return Digits(number) && uri.Host == "t.vndb.org" && uri.AbsolutePath == "/cv/" + number.PadLeft(2, '0')[^2..] + "/" + number + ".jpg";
        }
        if (cover.Id.StartsWith("steam:", StringComparison.Ordinal))
        {
            var id = cover.Id[6..];
            return Digits(id) && uri.Host == "shared.fastly.steamstatic.com" &&
                uri.AbsolutePath == $"/store_item_assets/steam/apps/{id}/library_600x900.jpg";
        }
        return false;
    }
    private static bool Digits(string value) => value.Length is > 0 and <= 12 && value.All(char.IsAsciiDigit);
    private static double Score(WorkCover cover)
    {
        if (cover.Width is not > 0 || cover.Height is not > 0) return 0;
        var usable = Math.Min(cover.Width.Value, cover.Height.Value * .75);
        var ratio = cover.Width.Value / (double)cover.Height.Value;
        var retained = Math.Min(ratio / .75, .75 / ratio);
        return usable * (.7 + .3 * retained);
    }
}
