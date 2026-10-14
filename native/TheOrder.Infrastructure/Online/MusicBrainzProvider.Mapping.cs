using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class MusicBrainzProvider
{
    public static MusicBrainzSearchResult MapSearch(JsonElement root, bool browse = false)
    {
        var total = Number(root, browse ? "release-count" : "count");
        if (total is null or < 0 || !root.TryGetProperty("releases", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 25) throw Invalid();
        var candidates = rows.EnumerateArray().Select(MapCandidate).ToArray();
        var result = new MusicBrainzSearchResult(candidates, total.Value, total > candidates.Length, null, null);
        if (!MusicBrainzValidation.Search(result)) throw Invalid(); return result;
    }
    public static MusicBrainzReleaseCandidate MapCandidate(JsonElement root)
    {
        var id = Id(root, "id", required: true)!;
        JsonElement group = default; root.TryGetProperty("release-group", out group);
        var labels = new List<MusicBrainzLabel>();
        if (root.TryGetProperty("label-info", out var info) && info.ValueKind != JsonValueKind.Null)
        {
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() > 64) throw Invalid();
            foreach (var row in info.EnumerateArray())
            {
                row.TryGetProperty("label", out var label);
                labels.Add(new(Text(label, "name", 500), OptionalText(row, "catalog-number", 200)));
            }
        }
        int? discs = null, tracks = Number(root, "track-count");
        string[] formats = [];
        if (root.TryGetProperty("media", out var media) && media.ValueKind != JsonValueKind.Null)
        {
            if (media.ValueKind != JsonValueKind.Array || media.GetArrayLength() > 50) throw Invalid();
            discs = media.GetArrayLength(); tracks = media.EnumerateArray().Sum(x => Number(x, "track-count") ?? 0);
            formats = media.EnumerateArray().Select(x => OptionalText(x, "format", 100)).OfType<string>().Distinct().ToArray();
        }
        var date = OptionalText(root, "date", 100); if (date is not null && !MusicBrainzValidation.Date(date)) throw Invalid();
        var value = new MusicBrainzReleaseCandidate(id, Id(group, "id"), Text(root, "title", 2000, required: true), Artist(root), date,
            OptionalText(root, "country", 100), Text(root, "status", 100), OptionalText(root, "barcode", 100), labels.ToArray(), discs, tracks)
        {
            Disambiguation = OptionalText(root, "disambiguation", 1000), Packaging = OptionalText(root, "packaging", 100),
            TextLanguage = TextRepresentation(root, "language"), TextScript = TextRepresentation(root, "script"),
            MediaFormats = formats, ReleaseEvents = ReleaseEvents(root)
        };
        if (!MusicBrainzValidation.Candidate(value)) throw Invalid(); return value;
    }
    public static MusicBrainzAlbum MapAlbum(string releaseId, JsonElement root, DateTimeOffset stamp)
    {
        var candidate = MapCandidate(root); if (candidate.Id != releaseId) throw Invalid();
        if (!root.TryGetProperty("media", out var rows) || rows.ValueKind != JsonValueKind.Array) throw Invalid();
        var discs = new List<MusicBrainzDisc>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() > 1000
                || Number(row, "track-count") != tracks.GetArrayLength()) throw Invalid();
            var songs = new List<MusicBrainzTrack>();
            foreach (var track in tracks.EnumerateArray())
            {
                track.TryGetProperty("recording", out var recording);
                var credits = Credits(recording).ToList();
                if (recording.ValueKind == JsonValueKind.Object && recording.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
                    foreach (var rel in relations.EnumerateArray().Take(256))
                        if (rel.ValueKind == JsonValueKind.Object && rel.TryGetProperty("work", out var work)) credits.AddRange(Credits(work));
                songs.Add(new(Number(track, "position") ?? 0, Text(track, "number", 100), Text(track, "title", 2000, required: true),
                    Artist(track), Number(track, "length") ?? Number(recording, "length"), Id(recording, "id"), credits.Distinct().Take(64).ToArray())
                { Works = Works(recording) });
            }
            discs.Add(new(Number(row, "position") ?? 0, Text(row, "title", 2000), Text(row, "format", 100), songs.OrderBy(x => x.Position).ToArray()));
        }
        root.TryGetProperty("release-group", out var group);
        var types = ArrayStrings(group, "secondary-types", 16, 100);
        var value = new MusicBrainzAlbum(candidate, OptionalText(group, "title", 2000), OptionalText(group, "primary-type", 100),
            types, discs.OrderBy(x => x.Position).ToArray(), Credits(root).Take(128).ToArray(), null, stamp);
        if (!MusicBrainzValidation.Album(value)) throw Invalid(); return value;
    }
    private static string? TextRepresentation(JsonElement root, string field)
    {
        if (!root.TryGetProperty("text-representation", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        return OptionalText(value, field, 20);
    }
    private static MusicBrainzReleaseEvent[] ReleaseEvents(JsonElement root)
    {
        if (!root.TryGetProperty("release-events", out var rows) || rows.ValueKind == JsonValueKind.Null) return [];
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256) throw Invalid();
        return rows.EnumerateArray().Select(row =>
        {
            if (row.ValueKind != JsonValueKind.Object) throw Invalid();
            string? country = null;
            if (row.TryGetProperty("area", out var area) && area.ValueKind != JsonValueKind.Null)
            {
                if (area.ValueKind != JsonValueKind.Object) throw Invalid();
                country = ArrayStrings(area, "iso-3166-1-codes", 8, 10).FirstOrDefault();
            }
            return new MusicBrainzReleaseEvent(OptionalText(row, "date", 100), country);
        }).ToArray();
    }
    private static MusicBrainzWork[] Works(JsonElement recording)
    {
        if (recording.ValueKind != JsonValueKind.Object || !recording.TryGetProperty("relations", out var rows)) return [];
        if (rows.ValueKind != JsonValueKind.Array) throw Invalid();
        var works = new List<MusicBrainzWork>();
        foreach (var row in rows.EnumerateArray().Take(256))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("work", out var work)) continue;
            if (work.ValueKind != JsonValueKind.Object) throw Invalid();
            // Existing partial relation snapshots can contain credits without an entity identity.
            // Keep those legacy credits, but never invent a Work ID from the Recording ID.
            if (!work.TryGetProperty("id", out _) && !work.TryGetProperty("title", out _)) continue;
            var languages = ArrayStrings(work, "languages", 16, 20);
            if (languages.Length == 0 && OptionalText(work, "language", 20) is { } language) languages = [language];
            var value = new MusicBrainzWork(Id(work, "id", required: true)!, Text(work, "title", 2000, required: true),
                OptionalText(work, "type", 100), languages, Credits(work).Distinct().Take(64).ToArray());
            if (!MusicBrainzValidation.Work(value)) throw Invalid();
            var prior = works.FirstOrDefault(x => x.Id == value.Id);
            if (prior is not null)
            {
                if (prior.Title != value.Title || prior.Type != value.Type || !prior.Languages.ToHashSet().SetEquals(value.Languages)
                    || !prior.Credits.ToHashSet().SetEquals(value.Credits)) throw Invalid();
                continue;
            }
            works.Add(value); if (works.Count > 16) throw Invalid();
        }
        return works.ToArray();
    }
    private static IEnumerable<MusicBrainzCredit> Credits(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("relations", out var rows) || rows.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in rows.EnumerateArray().Take(256))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("artist", out var artist) || artist.ValueKind != JsonValueKind.Object) continue;
            var role = Text(row, "type", 200); var name = Text(artist, "name", 500); var id = Id(artist, "id");
            if (role.Length == 0 || name.Length == 0) continue;
            var attributes = ArrayStrings(row, "attributes", 16, 100);
            if (attributes.Length > 0) role = (role + " · " + string.Join(", ", attributes));
            if (role.Length <= 200) yield return new(role, name, id);
        }
    }
    private static string Artist(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("artist-credit", out var rows)) return "";
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 64) throw Invalid();
        var result = string.Concat(rows.EnumerateArray().Select(x =>
        {
            var name = Text(x, "name", 500);
            if (name.Length == 0 && x.ValueKind == JsonValueKind.Object && x.TryGetProperty("artist", out var artist)) name = Text(artist, "name", 500);
            return name + Text(x, "joinphrase", 100);
        }));
        if (!MusicBrainzValidation.Text(result, 2000)) throw Invalid(); return result;
    }
    private static int? Number(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static string? Id(JsonElement root, string key, bool required = false)
    {
        var value = OptionalText(root, key, 100);
        if (value is null && !required) return null;
        if (!MusicBrainzReference.ValidId(value)) throw Invalid(); return value;
    }
    private static string Text(JsonElement root, string key, int max, bool required = false)
    {
        var value = OptionalText(root, key, max);
        if (required && string.IsNullOrWhiteSpace(value)) throw Invalid(); return value ?? "";
    }
    private static string? OptionalText(JsonElement root, string key, int max)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !MusicBrainzValidation.Text(value.GetString(), max)) throw Invalid();
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
    }
    private static string[] ArrayStrings(JsonElement root, string key, int count, int max)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > count) throw Invalid();
        return value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && MusicBrainzValidation.Text(x.GetString(), max, false) ? x.GetString()! : throw Invalid()).ToArray();
    }
}
