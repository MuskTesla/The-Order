using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

internal static class BangumiMusicFactsMapping
{
    internal static BangumiMusicFacts Map(JsonElement root)
    {
        var catalogues = new List<string>(); var artists = new List<string>(); var labels = new List<string>(); var aliases = new List<string>();
        var references = new List<MusicBrainzReference>(); string? barcode = null; int? discs = null;
        if (root.TryGetProperty("infobox", out var box) && box.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in box.EnumerateArray().Take(200))
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("value", out var v)) continue;
                var key = k.GetString(); var values = Values(v).Where(x => MusicBrainzValidation.Text(x, 2000, false)).ToArray();
                foreach (var value in values)
                {
                    switch (key)
                    {
                        case "别名": case "Alias": case "Aliases": aliases.Add(value); break;
                        case "商品编号": case "目录编号": case "唱片编号": case "品番": case "Catalog Number":
                            catalogues.AddRange(value.Split(['/', ';', '；', ',', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length <= 200)); break;
                        case "艺术家": case "Artist": case "Artists":
                            artists.AddRange(value.Split(['/', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length <= 500)); break;
                        case "厂牌": case "发行商": case "Label":
                            if (value.Length <= 500) labels.Add(value); break;
                        case "碟片数量": case "碟数": case "Disc Count":
                            if (int.TryParse(value, out var count) && count is > 0 and <= 50) discs = count; break;
                        case "条码": case "Barcode":
                            if (Regex.IsMatch(value, "^[0-9]{8,14}$")) barcode = value; break;
                    }
                    // Only explicit canonical MusicBrainz URLs provide external identity. UUIDs in prose do not.
                    if (MusicBrainzReference.TryParse(value, out var reference)) references.Add(reference!);
                }
            }
        }
        return new(catalogues.Distinct().Take(32).ToArray(), artists.Distinct().Take(64).ToArray(), labels.Distinct().Take(32).ToArray(),
            barcode, discs, references.Distinct().Take(16).ToArray()) { TitleAliases = aliases.Distinct().Take(16).ToArray() };
    }
    private static IEnumerable<string> Values(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { yield return value.GetString()!.Trim(); yield break; }
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in value.EnumerateArray().Take(64))
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String)
                yield return v.GetString()!.Trim();
    }
}
