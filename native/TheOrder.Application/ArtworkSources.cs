using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Application;

// Separate hero art from the user's poster choice. Never ranks covers by votes,
// fetches speculative URLs, or borrows artwork from another catalog work.
public static class ArtworkSources
{
    public static string? Landscape(WorkMetadata? work)
    {
        if (work is null) return null;
        if (!string.IsNullOrWhiteSpace(work.BannerUrl)) return work.BannerUrl;
        return work.Covers?.Where(c => !string.IsNullOrWhiteSpace(c.Url) && c.Width is >= 640 && c.Height is > 0
                && (double)c.Width.Value / c.Height.Value is >= 1.4 and <= 3.6)
            .OrderBy(c => Math.Abs((double)c.Width!.Value / c.Height!.Value - 16d / 9))
            .ThenByDescending(c => Math.Min(c.Width!.Value, 1920))
            .ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Url).FirstOrDefault();
    }
    public static string? Key(LibraryEntry entry) => Landscape(entry.Metadata) is { } url
        ? ImageSourceKeys.Remote(url, entry.Metadata?.FetchedAt ?? "") : ImageSourceKeys.For(entry);
    public static string Description(LibraryEntry entry) => !string.IsNullOrWhiteSpace(entry.Metadata?.BannerUrl)
        ? UiText.Get("当前作品横幅") : Landscape(entry.Metadata) is not null ? UiText.Get("当前作品的横向候选图") : UiText.Get("封面背景 · 无独立横图");
}
