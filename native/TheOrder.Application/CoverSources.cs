using TheOrder.Domain;

namespace TheOrder.Application;

// Selection is independent of transport. Phase 3B only resolves local payloads;
// a selected remote URL remains data and is never fetched by the offline client.
public static class CoverSources
{
    public static string? Selected(LibraryEntry game, bool includeExplicit = true)
    {
        if (game.LocalCover is { } local) return local.DataUrl;
        var work = game.Metadata;
        var choice = work is not null && game.CoverChoice?.WorkId == work.Id ? game.CoverChoice.Cover : null;
        string? Permitted(string? url) => !includeExplicit && work?.Covers?.Any(c => c.Url == url && c.Sexual == "explicit") == true ? null : url;
        if (choice is not null && (includeExplicit || choice.Sexual != "explicit") && Permitted(choice.Url) is { Length: > 0 } selected) return selected;
        if (!includeExplicit && choice?.Sexual == "explicit" && choice.Url == work?.CoverUrl) return null;
        return work is null ? null : Permitted(CoverQuality.Select(work.Covers ?? [], work.CoverUrl, includeExplicit));
    }
}
