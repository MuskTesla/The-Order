using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Legacy;

// Writable legacy boundary. The Phase 1 reader continues to preserve raw display values.
// Unknown properties are intentionally discarded, exactly as Electron parseBackup does.
public sealed class LegacyLibraryCodec : ILibraryCodec
{
    internal static readonly char[] Whitespace = ['\t', '\n', '\v', '\f', '\r', ' ', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new Utf16Writer() } };
    public ImmutableArray<LibraryEntry> Decode(ReadOnlyMemory<byte> document)
    {
        try
        {
            // Validate original lengths/types before normalization, then validate the canonical whole.
            var raw = LegacyLibraryReader.Parse(document);
            var normalized = raw.Select(Normalize).ToImmutableArray();
            return LegacyLibraryReader.ParseCanonical(Serialize(normalized, "2000-01-01T00:00:00.000Z"));
        }
        catch (JsonException e) { throw new InvalidDataException("旧版书架 JSON 无效。", e); }
    }
    public LibraryDocument Inspect(ReadOnlyMemory<byte> document, string exportedAt)
    {
        ImmutableArray<LibraryEntry> entries;
        try { entries = Decode(document); }
        catch (InvalidDataException e) { return new(LibraryAccess.InvalidInput, [], null, e.Message); }
        try { return new(LibraryAccess.ValidWritable, entries, Encode(entries, exportedAt)); }
        catch (LibraryWriteLimitException e) { return new(LibraryAccess.ValidReadOnly, entries, null, e.Message); }
    }
    private static byte[] Serialize(ImmutableArray<LibraryEntry> entries, string exportedAt)
        => JsonSerializer.SerializeToUtf8Bytes(new { format = "kun-gal-manager", version = 1, exportedAt, games = entries.Select(Project) }, Options);
    public byte[] Encode(ImmutableArray<LibraryEntry> entries, string exportedAt)
    {
        var bytes = Serialize(entries, exportedAt);
        if (bytes.Length > LegacyLibraryReader.MaximumBytes) throw new LibraryWriteLimitException();
        return bytes;
    }
    public string InstallationKey(string path) => LegacyPathIdentity.Create(path);
    public WorkMetadata NormalizeWork(WorkMetadata work) => Decode(Encode([new LibraryEntry("validation", "validation", false, work, [], null, "planned", null, false, [], [], "", 0, 0, [], "2000-01-01T00:00:00.000Z", null, null, null, new(false, null))], "2000-01-01T00:00:00.000Z"))[0].Metadata!;
    public string EvidenceIdentity(LibraryEntry e) => JsonSerializer.Serialize(new { e.Name, e.NameIsCustom, metadata = e.Metadata is null ? null : Metadata(e.Metadata), e.PreferredInstallationId, installations = e.Installations.Select(i => new { i.Id, i.Folder, i.Executable }) }, Options);
    public bool ProtectsName(LibraryEntry e) => e.NameIsCustom != false || LegacyTitleProtection.HasVersion(e.Name) || e.Metadata is not null && e.Name != e.Metadata.Title;

    public static PersonalPatch ReadPersonalPatch(JsonElement patch)
    {
        var allowed = new HashSet<string>(["name", "status", "personalRating", "favorite", "categories", "customTags", "notes", "preferredInstallationId"]);
        if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new InvalidDataException("修改包含不支持的字段。");
        Change<T> Read<T>(string name) => patch.TryGetProperty(name, out var v) ? new(true, v.Deserialize<T>(LegacyJson.Options)!) : default;
        return new() { Name = Read<string>("name"), Status = Read<string>("status"), Rating = Read<double?>("personalRating"), Favorite = Read<bool>("favorite"), Categories = Read<ImmutableArray<string>>("categories"), Tags = Read<ImmutableArray<string>>("customTags"), Notes = Read<string>("notes"), PreferredInstallationId = Read<string?>("preferredInstallationId") };
    }

    public static Dictionary<string, object?> Project(LibraryEntry e)
    {
        var d = new Dictionary<string, object?>();
        if (e.NameIsCustom is not null) d["nameIsCustom"] = e.NameIsCustom;
        d["id"] = e.Id; d["name"] = e.Name; d["metadata"] = e.Metadata is null ? null : Metadata(e.Metadata);
        d["installations"] = e.Installations.Select(i => { var location = new Dictionary<string, object?> { ["id"] = i.Id, ["folderPath"] = i.Folder, ["executablePath"] = i.Executable, ["arguments"] = i.Arguments }; if (i.WorkingDirectory is not null) location["workingDirectory"] = i.WorkingDirectory; return location; });
        d["preferredInstallationId"] = e.PreferredInstallationId; d["status"] = e.Status; d["personalRating"] = e.PersonalRating;
        d["favorite"] = e.Favorite; d["categories"] = e.Categories; d["customTags"] = e.CustomTags; d["notes"] = e.Notes;
        d["totalPlaySeconds"] = e.TotalPlaySeconds; d["playCount"] = e.PlayCount; d["sessions"] = e.Sessions.Select(s => new { s.Id, s.StartedAt, s.EndedAt, s.Seconds });
        d["addedAt"] = e.AddedAt; d["lastPlayedAt"] = e.LastPlayedAt;
        if (e.CoverChoice is not null) d["coverChoice"] = e.CoverChoice;
        if (e.LocalCover is not null) d["localCover"] = e.LocalCover;
        if (e.CoverDisplay.IsSpecified) d["nonPortraitCoverDisplay"] = e.CoverDisplay.Mode;
        return d;
    }
    private static Dictionary<string, object?> Metadata(WorkMetadata m)
    {
        var d = new Dictionary<string, object?> { ["id"] = m.Id, ["title"] = m.Title, ["originalTitle"] = m.OriginalTitle, ["aliases"] = m.Aliases };
        if (m.TitleEvidence is not null) d["titleEvidence"] = m.TitleEvidence;
        d["coverUrl"] = m.CoverUrl; d["bannerUrl"] = m.BannerUrl;
        if (m.Covers is not null) d["covers"] = m.Covers;
        d["description"] = m.Description; d["releaseDate"] = m.ReleaseDate; d["releaseDatePrecision"] = m.ReleaseDatePrecision;
        d["developers"] = m.Developers; d["tags"] = m.Tags; d["ratings"] = m.Ratings; d["characters"] = m.Characters;
        d["references"] = m.References.Select(r => { var x = new Dictionary<string, object?> { ["source"] = r.Source, ["id"] = r.Id }; if (r.Url is not null) x["url"] = r.Url; return x; });
        d["webUrl"] = m.WebUrl; d["contentRating"] = m.ContentRating; d["fetchedAt"] = m.FetchedAt;
        return d;
    }
    private static string T(string s) => s.Trim(Whitespace);
    private static string? N(string? s) => s is null ? null : T(s);
    private static string D(string s) => DateTimeOffset.Parse(T(s), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string? U(string? s) => s is null || s == "" ? null : new Uri(T(s)).AbsoluteUri;
    private static ImmutableArray<string> L(ImmutableArray<string> list) => [.. list.Select(T).Distinct(StringComparer.Ordinal)];
    private static WorkCover C(WorkCover c) => c with { Url = U(c.Url)! };
    private static LibraryEntry Normalize(LibraryEntry e) => e with
    {
        Id = T(e.Id), Name = T(e.Name), Notes = T(e.Notes), Categories = L(e.Categories), CustomTags = L(e.CustomTags),
        PreferredInstallationId = N(e.PreferredInstallationId), AddedAt = D(e.AddedAt), LastPlayedAt = e.LastPlayedAt is null ? null : D(e.LastPlayedAt),
        Installations = [.. e.Installations.Select(i => i with { Id = T(i.Id), Folder = LegacyPathIdentity.Normalize(i.Folder), Executable = i.Executable is null ? null : LegacyPathIdentity.Normalize(i.Executable) })],
        Sessions = [.. e.Sessions.Select(s => s with { Id = T(s.Id), StartedAt = D(s.StartedAt), EndedAt = D(s.EndedAt) })],
        CoverChoice = e.CoverChoice is null ? null : e.CoverChoice with { Cover = C(e.CoverChoice.Cover) },
        Metadata = e.Metadata is null ? null : Normalize(e.Metadata)
    };
    private static WorkMetadata Normalize(WorkMetadata m)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal); var urls = new HashSet<string>(StringComparer.Ordinal);
        return m with
        {
            Id = T(m.Id), Title = T(m.Title), OriginalTitle = T(m.OriginalTitle), Aliases = L(m.Aliases), Description = T(m.Description),
            TitleEvidence = m.TitleEvidence?.Select(t => t with { Text = T(t.Text), Kind = T(t.Kind), Language = N(t.Language) }).ToImmutableArray(),
            CoverUrl = U(m.CoverUrl), BannerUrl = U(m.BannerUrl), WebUrl = U(m.WebUrl), FetchedAt = D(m.FetchedAt),
            Covers = m.Covers?.Select(C).Where(c => { if (ids.Contains(c.Id) || urls.Contains(c.Url)) return false; ids.Add(c.Id); urls.Add(c.Url); return true; }).ToImmutableArray(),
            ReleaseDate = N(m.ReleaseDate), ReleaseDatePrecision = N(m.ReleaseDatePrecision), ContentRating = N(m.ContentRating), Developers = L(m.Developers), Tags = L(m.Tags),
            Ratings = [.. m.Ratings.Select(r => r with { Source = T(r.Source) })],
            Characters = [.. m.Characters.Select(c => c with { Id = T(c.Id), Name = T(c.Name), OriginalName = T(c.OriginalName), ImageUrl = U(c.ImageUrl), Role = T(c.Role), VoiceActors = L(c.VoiceActors) })],
            References = [.. m.References.Select(r => r with { Source = T(r.Source), Id = T(r.Id), Url = U(r.Url) })]
        };
    }
    private sealed class Utf16Writer : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // Match JSON.stringify: retain valid pairs as UTF-8; escape only lone surrogates.
            var b = new StringBuilder("\"");
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { b.Append(c).Append(value[++i]); continue; }
                var escape = c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f", '"' => "\\\"", '\\' => "\\\\", _ => null };
                if (escape is not null) b.Append(escape);
                else if (c < 32 || char.IsSurrogate(c)) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            writer.WriteRawValue(b.Append('"').ToString(), skipInputValidation: true);
        }
    }
}
