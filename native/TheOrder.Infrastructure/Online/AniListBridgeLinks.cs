using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// A canonical URL identifies its target, not its relationship to this subject.
// Accept only identity/link fields with a complete URL value. In particular,
// original-work, sequel and adaptation fields must never create a same-work ID.
public static class AniListBridgeLinks
{
    public sealed record Evidence(string[] Links, bool Ambiguous);
    public static string[] Map(JsonElement subject, BangumiRelationKind kind)
        => MapEvidence(subject, kind).Links;
    public static Evidence MapEvidence(JsonElement subject, BangumiRelationKind kind)
    {
        if (kind is not (BangumiRelationKind.Anime or BangumiRelationKind.Book)
            || !subject.TryGetProperty("infobox", out var box) || box.ValueKind != JsonValueKind.Array) return new([], false);
        var result = new HashSet<string>();
        var ambiguous = box.GetArrayLength() > 200;
        foreach (var field in box.EnumerateArray().Take(200))
        {
            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("key", out var key)
                || key.ValueKind != JsonValueKind.String || !IdentityField(key.GetString())
                || !field.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 30) ambiguous = true;
            IEnumerable<string> texts = value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! }
                : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Take(30).Where(x => x.ValueKind == JsonValueKind.Object
                    && (!x.TryGetProperty("k", out var label) || label.ValueKind == JsonValueKind.String
                        && (string.IsNullOrWhiteSpace(label.GetString()) || IdentityField(label.GetString())))
                    && x.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("v").GetString()!) : [];
            foreach (var text in texts)
            {
                if (text.Length > 4000) { ambiguous = true; continue; }
                if (AniListReference.TryParse(text.Trim(), out var reference))
                {
                    if (reference!.Kind == (kind == BangumiRelationKind.Anime ? AniListMediaKind.Anime : AniListMediaKind.Manga)) result.Add(reference.Url);
                    else ambiguous = true;
                }
                else if (key.GetString()?.Trim().ToLowerInvariant() is "anilist" or "myanimelist" or "mal") ambiguous = true;
            }
        }
        // Overflow cannot be silently truncated into apparently consistent IDs.
        return new(result.Order(StringComparer.Ordinal).Take(16).ToArray(), ambiguous || result.Count > 16);
    }
    private static bool IdentityField(string? key) => key?.Trim().ToLowerInvariant()
        is "anilist" or "myanimelist" or "mal" or "url" or "链接" or "外部链接";
}
