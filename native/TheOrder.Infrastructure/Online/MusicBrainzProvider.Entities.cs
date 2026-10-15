using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class MusicBrainzProvider
{
    private sealed record EntityCacheEntry(long Sequence, MusicBrainzEntityDetail? Detail, int Bytes);
    private readonly object entityCacheGate = new();
    private readonly Dictionary<MusicBrainzReference, EntityCacheEntry> entityCache = [];
    private long entitySequence;
    private const int MaximumEntityCacheBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan EntityFreshness = TimeSpan.FromHours(24), EntityStaleLimit = TimeSpan.FromDays(14);

    public async Task<MusicBrainzEntityResult> LoadAsync(MusicBrainzReference reference, bool refresh, CancellationToken cancellation)
    {
        if (!MusicBrainzEntityValidation.Reference(reference)) throw new InvalidDataException("仅支持 MusicBrainz 录音、作品与音乐人编号。");
        cancellation.ThrowIfCancellationRequested();
        EntityCacheEntry? prior;
        lock (entityCacheGate)
        {
            entityCache.TryGetValue(reference, out prior);
            if (prior?.Detail is { } priorDetail && (!MusicBrainzEntityValidation.Detail(priorDetail) || Now < priorDetail.FetchedAtUtc
                || Now - priorDetail.FetchedAtUtc > EntityStaleLimit))
            { entityCache.Remove(reference); prior = null; }
            if (!refresh && prior?.Detail is { } fresh && Now - fresh.FetchedAtUtc <= EntityFreshness)
                return new(EntitySnapshot(fresh), true, false, TruncationWarning(fresh));
        }
        var sequence = Interlocked.Increment(ref entitySequence);
        try
        {
            var detail = await Operation(async token =>
            {
                var kind = reference.Kind switch { MusicBrainzEntityKind.Work => "work", MusicBrainzEntityKind.Artist => "artist", _ => "recording" };
                var includes = reference.Kind switch
                {
                    MusicBrainzEntityKind.Work => "aliases+artist-rels+recording-rels+work-rels",
                    MusicBrainzEntityKind.Artist => "aliases+artist-rels+work-rels",
                    _ => "artist-credits+isrcs+artist-rels+work-rels"
                };
                using var document = await GetJsonAsync(kind + "/" + reference.Id + "?fmt=json&inc=" + Uri.EscapeDataString(includes), token).ConfigureAwait(false);
                return MapEntity(reference, document.RootElement, Now);
            }, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(detail).Length;
            lock (entityCacheGate)
            {
                // A late request may be shown by its owner, but cannot overwrite a newer cached response.
                if (bytes <= 512 * 1024 && (!entityCache.TryGetValue(reference, out var current) || current.Sequence <= sequence))
                {
                    entityCache[reference] = new(sequence, EntitySnapshot(detail), bytes);
                    while (entityCache.Count > 32 || entityCache.Values.Sum(x => x.Bytes) > MaximumEntityCacheBytes)
                        entityCache.Remove(entityCache.MinBy(x => x.Value.Sequence).Key);
                }
            }
            return new(detail, false, false, TruncationWarning(detail));
        }
        catch (MusicBrainzException e) when (e.Failure is MusicBrainzFailure.Offline or MusicBrainzFailure.Timeout or MusicBrainzFailure.RateLimited or MusicBrainzFailure.Server)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (entityCacheGate)
                if (entityCache.TryGetValue(reference, out var current) && current.Detail is { } old
                    && Now >= old.FetchedAtUtc && Now - old.FetchedAtUtc <= EntityStaleLimit)
                    return new(EntitySnapshot(old), true, true, "暂时无法更新，正在显示之前读取的 MusicBrainz 资料。" + TruncationWarning(old));
            throw;
        }
        catch (MusicBrainzException)
        {
            lock (entityCacheGate)
                if (!entityCache.TryGetValue(reference, out var current) || current.Sequence <= sequence)
                {
                    // A bounded rejection marker prevents a slower old request resurrecting rejected data.
                    entityCache[reference] = new(sequence, null, 0);
                    while (entityCache.Count > 32) entityCache.Remove(entityCache.MinBy(x => x.Value.Sequence).Key);
                }
            throw;
        }
    }
    // Public DTOs contain mutable arrays for serialization compatibility. Cache ownership
    // must not escape through a network result, cache hit, or offline fallback.
    private static MusicBrainzEntityDetail EntitySnapshot(MusicBrainzEntityDetail detail) => detail with
    {
        Aliases = detail.Aliases.ToArray(),
        Facts = detail.Facts.ToArray(),
        Relations = detail.Relations.Select(relation => relation with { Attributes = relation.Attributes.ToArray() }).ToArray()
    };
    private static string? TruncationWarning(MusicBrainzEntityDetail detail) => detail.RelationsTruncated
        ? "关联资料较多，本页显示前 256 项；完整关系可在 MusicBrainz 查看。" : null;

    public static MusicBrainzEntityDetail MapEntity(MusicBrainzReference reference, JsonElement root, DateTimeOffset stamp)
    {
        if (!MusicBrainzEntityValidation.Reference(reference) || Id(root, "id", true) != reference.Id) throw Invalid();
        var title = Text(root, reference.Kind == MusicBrainzEntityKind.Artist ? "name" : "title", 2000, true);
        var facts = new List<MusicBrainzEntityFact>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) facts.Add(new(label, value)); }
        void AddDate(string label, string field)
        {
            var date = OptionalText(root, field, 100);
            if (date is not null && !MusicBrainzValidation.Date(date)) throw Invalid();
            Add(label, date);
        }
        string[] aliases = [];
        if (root.TryGetProperty("aliases", out var aliasRows) && aliasRows.ValueKind != JsonValueKind.Null)
        {
            if (aliasRows.ValueKind != JsonValueKind.Array || aliasRows.GetArrayLength() > 1000) throw Invalid();
            aliases = aliasRows.EnumerateArray().Select(x => Text(x, "name", 2000, true)).Distinct().Take(128).ToArray();
        }
        switch (reference.Kind)
        {
            case MusicBrainzEntityKind.Recording:
                Add("艺术家", Artist(root));
                var length = Number(root, "length");
                if (length is < 0 or > 86400000) throw Invalid();
                if (length is { } ms) Add("时长", TimeSpan.FromMilliseconds(ms).ToString(ms >= 3600000 ? @"h\:mm\:ss" : @"m\:ss"));
                AddDate("首次发行", "first-release-date");
                Add("ISRC", string.Join(" · ", ArrayStrings(root, "isrcs", 256, 20))); break;
            case MusicBrainzEntityKind.Work:
                var languages = ArrayStrings(root, "languages", 16, 20);
                if (languages.Length == 0 && OptionalText(root, "language", 20) is { } language) languages = [language];
                Add("语言", string.Join(" · ", languages));
                Add("ISWC", string.Join(" · ", ArrayStrings(root, "iswcs", 128, 30))); break;
            case MusicBrainzEntityKind.Artist:
                Add("排序名称", OptionalText(root, "sort-name", 2000));
                Add("国家 / 地区代码", OptionalText(root, "country", 100));
                Add("性别（来源）", OptionalText(root, "gender", 100));
                foreach (var (key, label) in new[] { ("area", "地区"), ("begin-area", "起始地区"), ("end-area", "结束地区") })
                    if (root.TryGetProperty(key, out var area) && area.ValueKind != JsonValueKind.Null) Add(label, OptionalText(area, "name", 1000));
                if (root.TryGetProperty("life-span", out var span) && span.ValueKind != JsonValueKind.Null)
                {
                    foreach (var (key, label) in new[] { ("begin", "起始日期"), ("end", "结束日期") })
                    {
                        var date = OptionalText(span, key, 100);
                        if (date is not null && !MusicBrainzValidation.Date(date)) throw Invalid();
                        Add(label, date);
                    }
                }
                break;
        }
        var relations = new List<MusicBrainzEntityRelation>(); var truncated = false;
        if (root.TryGetProperty("relations", out var rows) && rows.ValueKind != JsonValueKind.Null)
        {
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000) throw Invalid();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw Invalid();
                var targetType = Text(row, "target-type", 100);
                MusicBrainzEntityKind? targetKind = targetType switch { "work" => MusicBrainzEntityKind.Work, "artist" => MusicBrainzEntityKind.Artist, "recording" => MusicBrainzEntityKind.Recording, _ => null };
                if (targetKind is null) continue;
                if (!row.TryGetProperty(targetType, out var target) || target.ValueKind != JsonValueKind.Object) throw Invalid();
                var targetRef = new MusicBrainzReference(targetKind.Value, Id(target, "id", true)!);
                var relation = new MusicBrainzEntityRelation(Text(row, "type", 200, true), Text(row, "direction", 100, true),
                    ArrayStrings(row, "attributes", 32, 200), targetRef, Text(target, targetKind == MusicBrainzEntityKind.Artist ? "name" : "title", 2000, true),
                    OptionalText(row, "begin", 100), OptionalText(row, "end", 100));
                if (relations.Count < MusicBrainzEntityValidation.MaximumRelations) relations.Add(relation); else truncated = true;
            }
        }
        var detail = new MusicBrainzEntityDetail(reference, title, OptionalText(root, "type", 100),
            OptionalText(root, "disambiguation", 1000), aliases, facts.ToArray(), relations.ToArray(), truncated, stamp);
        if (!MusicBrainzEntityValidation.Detail(detail)) throw Invalid();
        return detail;
    }
}
