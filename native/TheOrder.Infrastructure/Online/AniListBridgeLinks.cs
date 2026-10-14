using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// Only explicit canonical links published on the Bangumi subject qualify as
// cross-site evidence. Titles and unofficial mirror indexes never create IDs.
public static class AniListBridgeLinks
{
    public static string[] Map(JsonElement subject, BangumiRelationKind kind)
    {
        if (kind is not (BangumiRelationKind.Anime or BangumiRelationKind.Book)
            || !subject.TryGetProperty("infobox", out var box) || box.ValueKind != JsonValueKind.Array) return [];
        var result = new HashSet<string>();
        foreach (var field in box.EnumerateArray().Take(200))
        {
            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("value", out var value)) continue;
            IEnumerable<string> texts = value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! }
                : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Take(30).Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("v").GetString()!) : [];
            foreach (var text in texts.Where(x => x.Length <= 4000))
                foreach (Match match in Regex.Matches(text, @"https://[^\s<>\[\]""']+"))
                    if (AniListReference.TryParse(match.Value, out var reference) && reference!.Kind == (kind == BangumiRelationKind.Anime ? AniListMediaKind.Anime : AniListMediaKind.Manga)) result.Add(reference.Url);
        }
        return result.Take(16).Order(StringComparer.Ordinal).ToArray();
    }
}
