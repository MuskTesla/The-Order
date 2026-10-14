using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheOrder.Application;

public sealed record BangumiWatchDataset(string Repository, string Revision, DateTimeOffset UpdatedAtUtc,
    string Version, string Sha256, byte[] GzipJson)
{
    public string SourceUrl => "https://github.com/" + Repository + "/blob/" + Revision + "/dist/data.json";
    public string DataVersion => Version + " · " + Revision[..10];
    public byte[] Decode()
    {
        if (Repository is not ("bangumi-data/bangumi-data" or "wan0ge/bangumi-data") || Revision is not { Length: 40 }
            || !Revision.All(char.IsAsciiHexDigit) || Version is not { Length: > 0 and <= 80 } || Sha256 is not { Length: 64 } || !Sha256.All(char.IsAsciiHexDigit)
            || GzipJson is not { Length: > 0 and <= 4 * 1024 * 1024 }) throw new InvalidDataException("放送数据格式无效。");
        using var source = new MemoryStream(GzipJson, false); using var gzip = new GZipStream(source, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[64 * 1024]; int size;
        while ((size = gzip.Read(buffer)) > 0) { if (output.Length + size > 12 * 1024 * 1024) throw new InvalidDataException("放送数据超过大小限制。"); output.Write(buffer, 0, size); }
        var bytes = output.ToArray(); if (Convert.ToHexString(SHA256.HashData(bytes)) != Sha256.ToUpperInvariant()) throw new InvalidDataException("放送数据校验失败。"); return bytes;
    }
    public static BangumiWatchDataset Create(string repository, string revision, DateTimeOffset updated, string version, byte[] json)
    {
        if (json.Length is < 1 or > 12 * 1024 * 1024) throw new InvalidDataException("放送数据超过大小限制。");
        using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(json);
        return new(repository, revision, updated, version, Convert.ToHexString(SHA256.HashData(json)), output.ToArray());
    }
}
public sealed record BangumiWatchSnapshot(BangumiWatchDataset Primary, BangumiWatchDataset? Supplement, DateTimeOffset CheckedAtUtc, string? UpdateWarning = null);
public sealed record WatchDataQuery(AnimeWatchIdentity Identity, string? Format = null, string? StartDate = null);
public enum WatchDataMatch { Matched, MatchedNoBroadcast, NotRecorded, AmbiguousIdentity, IdentityConflict, GranularityConflict, MatchedFilteredBroadcast }
public sealed record WatchDataResult(WatchLink[] Links, WatchDataMatch Match, string? Message, string DataVersion,
    int? RecordOrdinal = null, string? ItemType = null, string? Begin = null);
public interface IBangumiWatchSnapshotStore
{
    Task<BangumiWatchSnapshot?> ReadAsync(CancellationToken cancellation);
    Task WriteAsync(BangumiWatchSnapshot value, CancellationToken cancellation);
}
public interface IBangumiWatchDataProvider
{
    Task<BangumiWatchSnapshot> BuiltInAsync(CancellationToken cancellation);
    Task<BangumiWatchSnapshot> UpdateAsync(BangumiWatchSnapshot previous, CancellationToken cancellation);
}
public interface IBangumiWatchLinksReader
{
    Task<WatchDataResult> ReadAsync(WatchDataQuery query, CancellationToken cancellation);
    Task<bool> UpdateIfDueAsync(CancellationToken cancellation);
}

// Exact identity index, with no title fields or fuzzy matching. An ambiguous ID
// cannot be rescued by the other ID, and a fork can only supplement a main row.
public sealed class BangumiWatchDataIndex
{
    private sealed record Site(string Key, string Id, string Url, string Platform, string[] Regions, string? Season, string? Comment);
    private sealed record Row(int Ordinal, AnimeWatchIdentity Identity, string Type, DateOnly? Date, Site[] Sites, int DeclaredBroadcasts);
    private sealed class Dataset
    {
        public readonly Dictionary<string, List<Row>> Bangumi = new(StringComparer.Ordinal);
        public readonly Dictionary<int, List<Row>> AniList = [];
        public readonly List<Row> Rows = [];
        public int InputItems, RejectedRows, RejectedSites;
    }
    private readonly Dataset primary, supplement;
    private readonly BangumiWatchSnapshot snapshot;
    private readonly HashSet<string> sharedUrls;
    private readonly HashSet<string> ambiguousSeasons;
    public int InputItems => primary.InputItems;
    public int IndexedItems => primary.Rows.Count;
    public int RejectedRows => primary.RejectedRows;
    public int RejectedSites => primary.RejectedSites;
    public int AmbiguousIdentityKeys => primary.Bangumi.Values.Count(x => x.Count > 1) + primary.AniList.Values.Count(x => x.Count > 1);
    public BangumiWatchDataIndex(BangumiWatchSnapshot value)
    {
        if (value.Primary is null || value.Primary.Repository != "bangumi-data/bangumi-data" || value.Supplement is not null && value.Supplement.Repository != "wan0ge/bangumi-data") throw new InvalidDataException("放送数据来源不一致。");
        snapshot = value; primary = Parse(value.Primary); supplement = value.Supplement is null ? new() : Parse(value.Supplement);
        sharedUrls = primary.Rows.SelectMany(r => r.Sites.Select(s => (s.Url, r.Ordinal))).Distinct()
            .GroupBy(x => x.Url, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        ambiguousSeasons = supplement.Rows.SelectMany(r => r.Sites.Where(s => s.Season is not null).Select(s => (Season: s.Season!, r.Ordinal)))
            .Distinct().GroupBy(x => x.Season).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
    }
    public WatchDataResult Find(WatchDataQuery query)
    {
        var row = Resolve(primary, query.Identity, out var match); var version = snapshot.Primary.DataVersion;
        if (row is null) return new([], match, match switch
        { WatchDataMatch.AmbiguousIdentity => "放送数据中的作品编号存在重复，暂未采用该映射。", WatchDataMatch.IdentityConflict => "放送数据的作品编号不一致，暂未采用该映射。", _ => null }, version);
        if (!Granularity(row, query)) return new([], WatchDataMatch.GranularityConflict, "放送数据的类型或首播季度与当前条目不一致，暂未采用该映射。", version, row.Ordinal, row.Type, row.Date?.ToString("yyyy-MM-dd"));
        var extra = Resolve(supplement, row.Identity, out _);
        if (extra is not null && (extra.Type != row.Type || row.Date is null || extra.Date != row.Date)) extra = null;
        var links = new List<WatchLink>();
        foreach (var site in row.Sites)
        {
            var url = site.Url; var kind = Purpose(url); var note = "放送站点收录；未核验当前版权或播放权限。";
            var sources = new List<WatchLinkEvidence> { new("bangumi-data", snapshot.Primary.SourceUrl, note, DataVersion: version) };
            if (sharedUrls.Contains(url)) { kind = WatchLinkKind.Information; note = "此平台链接被多个条目共用，可能为系列或合集页。"; }
            // A media_id is never a season_id. Only an explicit fork season_id,
            // for the exact same identity, format, date and media_id, may be used.
            var additional = extra?.Sites.Where(s => s.Key == site.Key && s.Id == site.Id && s.Season is not null).ToArray();
            if (site.Key.StartsWith("bilibili", StringComparison.Ordinal) && kind == WatchLinkKind.Information && !sharedUrls.Contains(url)
                && additional is { Length: 1 } && !ambiguousSeasons.Contains(additional[0].Season!))
            {
                url = "https://www.bilibili.com/bangumi/play/ss" + additional[0].Season; kind = WatchLinkKind.Playback;
                sources.Add(new("wan0ge/bangumi-data", snapshot.Supplement!.SourceUrl, "明确 season_id；已核对主库作品编号、类型、首播日期和 media_id。", DataVersion: snapshot.Supplement.DataVersion));
                sources.Add(new("Bilibili 作品页", site.Url, "主库 media_id 对应的作品页；编号未被用来推算 season_id。", DataVersion: version));
            }
            var link = new WatchLink(row.Identity, site.Platform, url, kind, site.Regions, WatchLinkVerification.ProviderListed, sources.ToArray(),
                note + (site.Comment is { Length: > 0 } comment ? " " + comment : ""));
            if (WatchLinks.Valid(link)) links.Add(link);
        }
        return new(WatchLinks.Merge(query.Identity, links, WatchLinkSort.SourceOrder), links.Count > 0 ? WatchDataMatch.Matched : row.DeclaredBroadcasts > 0 ? WatchDataMatch.MatchedFilteredBroadcast : WatchDataMatch.MatchedNoBroadcast,
            snapshot.UpdateWarning ?? (links.Count == 0 && row.DeclaredBroadcasts > 0 ? "上游收录了放送站点，但当前链接格式暂不支持，已保留其他来源。" : null), version, row.Ordinal, row.Type, row.Date?.ToString("yyyy-MM-dd"));
    }
    private static Row? Resolve(Dataset data, AnimeWatchIdentity id, out WatchDataMatch match)
    {
        match = WatchDataMatch.NotRecorded; if (!id.IsValid) { match = WatchDataMatch.IdentityConflict; return null; }
        var bg = id.BangumiId is not null ? data.Bangumi.GetValueOrDefault(id.BangumiId) : null;
        var ani = id.AniListId is { } a ? data.AniList.GetValueOrDefault(a) : null;
        if (bg?.Count > 1 || ani?.Count > 1) { match = WatchDataMatch.AmbiguousIdentity; return null; }
        var row = bg?.SingleOrDefault() ?? ani?.SingleOrDefault(); if (row is null) return null;
        if (bg is not null && ani is not null && bg[0].Ordinal != ani[0].Ordinal
            || row.Identity.BangumiId is not null && id.BangumiId is not null && row.Identity.BangumiId != id.BangumiId
            || row.Identity.AniListId is not null && id.AniListId is not null && row.Identity.AniListId != id.AniListId)
        { match = WatchDataMatch.IdentityConflict; return null; }
        match = WatchDataMatch.Matched; return row;
    }
    private static bool Granularity(Row row, WatchDataQuery query)
    {
        var type = query.Format?.ToUpperInvariant() switch { "TV" or "TV_SHORT" => "tv", "MOVIE" or "剧场版" => "movie", "OVA" => "ova", "ONA" or "WEB" => "web", _ => null };
        if (type is not null && type != row.Type) return false;
        if (row.Date is not { } date || query.StartDate is not { Length: >= 4 } expected) return true;
        if (!int.TryParse(expected.AsSpan(0, 4), out var year)) return false;
        if (DateOnly.TryParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) && Math.Abs(date.DayNumber - exact.DayNumber) <= 3) return true;
        if (year != date.Year) return false;
        return expected.Length < 7 || int.TryParse(expected.AsSpan(5, 2), out var month) && month is >= 1 and <= 12 && (month - 1) / 3 == (date.Month - 1) / 3;
    }
    private static Dataset Parse(BangumiWatchDataset blob)
    {
        using var document = JsonDocument.Parse(blob.Decode(), new JsonDocumentOptions { MaxDepth = 48 }); var root = document.RootElement;
        Unique(root); if (!root.TryGetProperty("siteMeta", out var metadata) || metadata.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 25000) throw new InvalidDataException("放送数据缺少完整目录。");
        Unique(metadata); var data = new Dataset { InputItems = items.GetArrayLength() }; int ordinal = 0;
        foreach (var item in items.EnumerateArray())
        {
            ordinal++; Unique(item); var type = Text(item, "type", 20);
            if (type is not ("tv" or "web" or "movie" or "ova") || !item.TryGetProperty("sites", out var sites) || sites.ValueKind != JsonValueKind.Array || sites.GetArrayLength() > 64) { data.RejectedRows++; continue; }
            int? ani = null; string? bg = null; bool conflict = false; var links = new List<Site>(); int broadcastCount = 0;
            foreach (var site in sites.EnumerateArray())
            {
                Unique(site); var key = Text(site, "site", 100); var id = Text(site, "id", 500);
                if (key == "bangumi" && PositiveId(id)) { if (bg is not null && bg != id) conflict = true; bg = id; }
                if (key == "aniList" && PositiveId(id) && int.TryParse(id, out var number)) { if (ani is not null && ani != number) conflict = true; ani = number; }
                if (key is null || id is null || !metadata.TryGetProperty(key, out var meta) || Text(meta, "type", 20) != "onair") continue;
                broadcastCount++;
                Unique(meta); var platform = Text(meta, "title", 100); var template = Text(meta, "urlTemplate", 2048); var overrideUrl = Text(site, "url", 2048);
                if (template is null || !template.Contains("{{id}}", StringComparison.Ordinal) || platform is null) { data.RejectedSites++; continue; }
                var url = overrideUrl ?? template.Replace("{{id}}", id, StringComparison.Ordinal);
                if (!Allowed(key, url)) { data.RejectedSites++; continue; }
                var regionOwner = site.TryGetProperty("regions", out _) ? site : meta;
                var regions = Regions(regionOwner); if (regions is null) { data.RejectedSites++; continue; }
                var season = Text(site, "season_id", 20); if (!PositiveId(season)) season = null;
                links.Add(new(key, id, url, platform, regions, season, Text(site, "comment", 800)));
            }
            if (conflict || ani is null && bg is null) { data.RejectedRows++; continue; }
            var date = DateTimeOffset.TryParse(Text(item, "begin", 100), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ? DateOnly.FromDateTime(instant.UtcDateTime) : (DateOnly?)null;
            var row = new Row(ordinal, new(ani, bg), type, date, links.ToArray(), broadcastCount); data.Rows.Add(row);
            if (bg is not null) { if (!data.Bangumi.TryGetValue(bg, out var values)) data.Bangumi[bg] = values = []; values.Add(row); }
            if (ani is { } ai) { if (!data.AniList.TryGetValue(ai, out var values)) data.AniList[ai] = values = []; values.Add(row); }
        }
        if (data.Rows.Count == 0) throw new InvalidDataException("放送数据没有可核对的作品编号。"); return data;
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) throw new InvalidDataException("放送数据包含重复或无效字段。");
    }
    private static string? Text(JsonElement value, string key, int cap) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s && s.Length <= cap && !s.Any(char.IsControl) ? s : null;
    private static bool PositiveId(string? value) => value is { Length: > 0 and <= 10 } && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && value == id.ToString(CultureInfo.InvariantCulture);
    private static string[]? Regions(JsonElement owner)
    {
        if (!owner.TryGetProperty("regions", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 64) return null;
        var output = new List<string>(); foreach (var r in value.EnumerateArray()) { if (r.ValueKind != JsonValueKind.String || r.GetString() is not { Length: 2 } s || !s.All(char.IsAsciiLetterUpper)) return null; output.Add(s); }
        return output.Distinct().ToArray();
    }
    private static WatchLinkKind Purpose(string url)
    {
        var uri = new Uri(url); var p = uri.AbsolutePath;
        return uri.Host switch
        {
            "www.bilibili.com" or "bilibili.com" => p.StartsWith("/bangumi/play/", StringComparison.Ordinal) ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "www.youtube.com" => p is "/watch" or "/playlist" ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "www.acfun.cn" => WatchLinkKind.Playback,
            "v.qq.com" => p.StartsWith("/x/cover/", StringComparison.Ordinal) || p.StartsWith("/x/page/", StringComparison.Ordinal) ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "www.iqiyi.com" => p.StartsWith("/v_", StringComparison.Ordinal) ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "v.youku.com" => WatchLinkKind.Playback,
            "anime.gamer.com.tw" => p == "/animeVideo.php" ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "abema.tv" => p.StartsWith("/video/episode/", StringComparison.Ordinal) ? WatchLinkKind.Playback : WatchLinkKind.Information,
            "www.netflix.com" => p.StartsWith("/watch/", StringComparison.Ordinal) ? WatchLinkKind.Playback : WatchLinkKind.Information,
            _ => WatchLinkKind.Information
        };
    }
    // Site keys AND domains/paths are allowlisted. Remote templates/URL overrides
    // cannot turn a download, news, arbitrary host or redirect endpoint into VOD.
    private static bool Allowed(string key, string url)
    {
        if (!WatchLinks.SafeUrl(url)) return false; var u = new Uri(url); var h = u.IdnHost; var p = u.AbsolutePath;
        return key switch
        {
            "bilibili" or "bilibili_hk_mo" or "bilibili_hk_mo_tw" or "bilibili_tw" => h is "www.bilibili.com" or "bilibili.com" && (p.StartsWith("/bangumi/media/md", StringComparison.Ordinal) && PositiveId(p[17..].TrimEnd('/')) || p.StartsWith("/bangumi/play/ss", StringComparison.Ordinal) && PositiveId(p[16..].TrimEnd('/')) || p.StartsWith("/bangumi/play/ep", StringComparison.Ordinal) && PositiveId(p[16..].TrimEnd('/'))),
            "acfun" => h == "www.acfun.cn" && p.StartsWith("/bangumi/aa", StringComparison.Ordinal),
            "youku" => h == "list.youku.com" && p.StartsWith("/show/id_", StringComparison.Ordinal) || h == "v.youku.com" && p.StartsWith("/v_show/id_", StringComparison.Ordinal),
            "qq" => h == "v.qq.com" && (p.StartsWith("/detail/", StringComparison.Ordinal) || p.StartsWith("/x/cover/", StringComparison.Ordinal) || p.StartsWith("/x/page/", StringComparison.Ordinal)),
            "iqiyi" => h == "www.iqiyi.com" && (p.StartsWith("/a_", StringComparison.Ordinal) || p.StartsWith("/v_", StringComparison.Ordinal)),
            "letv" => h == "www.le.com" && p.StartsWith("/comic/", StringComparison.Ordinal),
            "mgtv" => h == "www.mgtv.com" && (p.StartsWith("/h/", StringComparison.Ordinal) || p.StartsWith("/b/", StringComparison.Ordinal)),
            "nicovideo" => h == "ch.nicovideo.jp" && p.Length > 1 && !p.Contains("redirect", StringComparison.OrdinalIgnoreCase),
            "netflix" => h == "www.netflix.com" && (p.StartsWith("/title/", StringComparison.Ordinal) || p.StartsWith("/watch/", StringComparison.Ordinal)),
            "gamer" or "gamer_hk" => h == "acg.gamer.com.tw" && p == "/acgDetail.php" || h == "anime.gamer.com.tw" && p is "/animeRef.php" or "/animeVideo.php",
            "muse_hk" or "muse_tw" or "ani_one" or "ani_one_asia" or "youtube" or "tropics" or "mighty" => h == "www.youtube.com" && (p == "/playlist" && u.Query.Contains("list=", StringComparison.Ordinal) || p == "/watch" && u.Query.Contains("v=", StringComparison.Ordinal)),
            "viu" => h == "www.viu.com" && p.StartsWith("/ott/", StringComparison.Ordinal) && p.Contains("/vod/", StringComparison.Ordinal),
            "mytv" => h == "www.mytvsuper.com" && p.StartsWith("/tc/programme/", StringComparison.Ordinal),
            "disneyplus" => h == "www.disneyplus.com" && (p.StartsWith("/series/", StringComparison.Ordinal) || p.StartsWith("/movies/", StringComparison.Ordinal)),
            "abema" => h == "abema.tv" && (p.StartsWith("/video/title/", StringComparison.Ordinal) || p.StartsWith("/video/episode/", StringComparison.Ordinal)),
            "unext" => h == "video.unext.jp" && p.StartsWith("/title/", StringComparison.Ordinal),
            "crunchyroll" => h == "www.crunchyroll.com" && (p.Contains("/series/", StringComparison.Ordinal) || p.Contains("/watch/", StringComparison.Ordinal)),
            "danime" => h == "animestore.docomo.ne.jp" && p == "/animestore/ci_pc",
            "prime" => h is "www.amazon.co.jp" or "www.amazon.com" && p.StartsWith("/gp/video/detail/", StringComparison.Ordinal),
            _ => false
        };
    }
}
