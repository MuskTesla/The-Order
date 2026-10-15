using System.Globalization;
using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed record BangumiEpisodeTypeFilter(int? Type, string Caption, int Count);

// A projection of already loaded source rows. It does not infer a viewing
// timeline, renumber specials, fetch missing pages, or identify staff names.
public static class BangumiEpisodeDisplay
{
    public static bool ValidType(int? type) => type is null or >= 0 and <= 6;
    public static string TypeName(int type) => type switch
    {
        0 => "本篇", 1 => "特别篇", 2 => "OP", 3 => "ED", 4 => "预告 / 宣传", 5 => "MAD", 6 => "其他", _ => "未知类型"
    };
    public static BangumiEpisodeTypeFilter[] Filters(BangumiEpisode[] loaded) =>
        [new(null, "全部", loaded.Length), .. loaded.GroupBy(x => x.Type).OrderBy(x => x.Key)
            .Select(x => new BangumiEpisodeTypeFilter(x.Key, TypeName(x.Key), x.Count()))];
    public static BangumiEpisode[] Project(BangumiEpisode[] loaded, int? type)
    {
        if (!ValidType(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return loaded.Where(x => type is null || x.Type == type).OrderBy(x => x.Type).ThenBy(x => x.Sort).ThenBy(x => x.Id).ToArray();
    }
    public static string Number(BangumiEpisode row) => row.Sort.ToString("0.############################", CultureInfo.InvariantCulture);
    public static Uri EpisodeUri(int episodeId) => episodeId > 0
        ? new Uri("https://bgm.tv/ep/" + episodeId.ToString(CultureInfo.InvariantCulture))
        : throw new ArgumentOutOfRangeException(nameof(episodeId));
}
