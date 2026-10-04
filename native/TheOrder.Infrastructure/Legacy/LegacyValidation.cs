using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheOrder.Infrastructure.Legacy;

// Read-only structural validation. No repair, deduplication, path access, image decode or writeback.
internal static class LegacyValidation
{
    private static void Fail(string field) => throw new InvalidDataException($"旧版书架字段 {field} 缺失或无效。原文件未修改。");
    private static JsonElement Field(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) { Fail(name); return default; }
        return value;
    }
    private static string Text(JsonElement e, string name, int max = 1000, bool empty = true)
    {
        if (e.ValueKind != JsonValueKind.String) { Fail(name); return ""; }
        var s = LegacyJson.String(e);
        if (s.Length > max || s.Contains('\0') || !empty && string.IsNullOrWhiteSpace(s)) Fail(name);
        return s;
    }
    private static string S(JsonElement p, string n, int max = 1000, bool empty = true) => Text(Field(p, n), n, max, empty);
    private static void NullableText(JsonElement p, string n, int max = 1000) { var v = Field(p, n); if (v.ValueKind != JsonValueKind.Null) Text(v, n, max); }
    private static double Number(JsonElement e, string n, double max = 1e12)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var v) || !double.IsFinite(v) || v < 0 || v > max) { Fail(n); return 0; }
        return v;
    }
    private static double N(JsonElement p, string n, double max = 1e12) => Number(Field(p, n), n, max);
    private static void Boolean(JsonElement e, string n) { if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail(n); }
    private static JsonElement.ArrayEnumerator Array(JsonElement p, string n, int max)
    {
        var e = Field(p, n);
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() > max) Fail(n);
        return e.EnumerateArray();
    }
    private static void Strings(JsonElement p, string n, int max = 200) { foreach (var e in Array(p, n, max)) Text(e, n, 200, false); }
    private static DateTimeOffset Date(JsonElement p, string n)
    {
        if (!DateTimeOffset.TryParse(S(p, n, 100, false), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) Fail(n);
        return d;
    }
    private static void Url(JsonElement p, string n, bool optional = false)
    {
        if (optional && !p.TryGetProperty(n, out _)) return;
        var e = Field(p, n);
        if (e.ValueKind == JsonValueKind.Null) return;
        var s = Text(e, n, 8192);
        if (s.Length == 0) return;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) Fail(n);
    }
    private static string Id(JsonElement p, string n) => S(p, n, 300, false).Trim(LegacyLibraryCodec.Whitespace);
    private static void Unique(IEnumerable<string> values, string n, IEqualityComparer<string>? comparer = null)
    {
        var seen = new HashSet<string>(comparer);
        foreach (var s in values) if (!seen.Add(s)) Fail(n);
    }
    private static string PathValue(JsonElement p, string n)
    {
        var s = S(p, n, 32768, false);
        return LegacyPathIdentity.Create(s);
    }

    public static void Validate(JsonElement root, CancellationToken cancellationToken)
    {
        if (S(root, "format") != "kun-gal-manager") Fail("format");
        if (N(root, "version") != 1) Fail("version");
        var games = Array(root, "games", 10000).ToArray();
        var paths = new List<string>();
        Unique(games.Select(g => Id(g, "id")), "games.id");
        foreach (var g in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            S(g, "name", 2000, false);
            if (g.TryGetProperty("nameIsCustom", out var custom)) Boolean(custom, "nameIsCustom");
            if (S(g, "status") is not ("planned" or "playing" or "completed" or "paused" or "dropped")) Fail("status");
            Boolean(Field(g, "favorite"), "favorite");
            if (Field(g, "personalRating").ValueKind != JsonValueKind.Null) N(g, "personalRating", 10);
            Strings(g, "categories"); Strings(g, "customTags"); S(g, "notes", 200000);
            var total = Math.Floor(N(g, "totalPlaySeconds")); N(g, "playCount", 1e8);
            Date(g, "addedAt"); if (Field(g, "lastPlayedAt").ValueKind != JsonValueKind.Null) Date(g, "lastPlayedAt");
            var installs = Array(g, "installations", 100).ToArray();
            Unique(installs.Select(i => Id(i, "id")), "installations.id");
            foreach (var i in installs)
            {
                var folder = PathValue(i, "folderPath"); paths.Add(folder);
                if (Field(i, "executablePath").ValueKind != JsonValueKind.Null)
                {
                    var exe = PathValue(i, "executablePath");
                    if (!LegacyPathIdentity.Contains(folder, exe) || !exe.EndsWith(".exe", StringComparison.Ordinal)) Fail("executablePath");
                }
                foreach (var a in Array(i, "arguments", 100)) Text(a, "arguments", 8192);
            }
            if (Field(g, "preferredInstallationId").ValueKind != JsonValueKind.Null && !installs.Any(i => Id(i, "id") == Id(g, "preferredInstallationId"))) Fail("preferredInstallationId");
            var sessions = Array(g, "sessions", 100000).ToArray();
            Unique(sessions.Select(s => Id(s, "id")), "sessions.id");
            double sum = 0;
            foreach (var s in sessions)
            {
                if (Date(s, "endedAt") < Date(s, "startedAt")) Fail("endedAt");
                sum += Math.Floor(N(s, "seconds", 366 * 86400));
            }
            if (total < sum) Fail("totalPlaySeconds");
            var metadata = Field(g, "metadata");
            if (metadata.ValueKind != JsonValueKind.Null) Metadata(metadata);
            if (g.TryGetProperty("coverChoice", out var choice))
            {
                var workId = S(choice, "workId", 20, false);
                if (!Regex.IsMatch(workId, "^[0-9]{1,20}$") || metadata.ValueKind == JsonValueKind.Null || Id(metadata, "id") != workId) Fail("coverChoice.workId");
                Cover(Field(choice, "cover"));
                if (g.TryGetProperty("localCover", out _)) Fail("localCover + coverChoice");
            }
            if (g.TryGetProperty("localCover", out var local)) ValidateLocalCover(local);
            if (g.TryGetProperty("nonPortraitCoverDisplay", out var mode) && mode.ValueKind != JsonValueKind.Null && Text(mode, "nonPortraitCoverDisplay") is not ("blur" or "plain" or "crop")) Fail("nonPortraitCoverDisplay");
        }
        Unique(paths, "installations.folderPath", StringComparer.Ordinal);
    }

    private static void Metadata(JsonElement m)
    {
        Id(m, "id"); S(m, "title", 2000, false); S(m, "originalTitle", 2000); Strings(m, "aliases");
        S(m, "description", 200000); NullableText(m, "releaseDate", 100); NullableText(m, "releaseDatePrecision", 100);
        Strings(m, "developers"); Strings(m, "tags", 1000); NullableText(m, "contentRating", 200); Date(m, "fetchedAt");
        Url(m, "coverUrl", true); Url(m, "bannerUrl", true); Url(m, "webUrl", true);
        foreach (var r in Array(m, "ratings", 30)) { S(r, "source", 100, false); N(r, "score", 100000); N(r, "scale", 100000); if (Field(r, "votes").ValueKind != JsonValueKind.Null) N(r, "votes"); }
        foreach (var c in Array(m, "characters", 500)) { S(c, "id", 300); S(c, "name", 2000); S(c, "originalName", 2000); Url(c, "imageUrl", true); S(c, "role", 200); Strings(c, "voiceActors"); }
        foreach (var r in Array(m, "references", 1000)) { S(r, "source", 100, false); Id(r, "id"); Url(r, "url", true); }
        if (m.TryGetProperty("covers", out _)) foreach (var c in Array(m, "covers", 500)) Cover(c);
        if (m.TryGetProperty("titleEvidence", out _)) foreach (var t in Array(m, "titleEvidence", 500)) { S(t, "text", 500, false); S(t, "kind", 100, false); Boolean(Field(t, "isMachine"), "isMachine"); NullableText(t, "language", 100); }
    }

    private static void Cover(JsonElement c)
    {
        if (!Regex.IsMatch(S(c, "id", 20, false), "^[0-9]{1,20}$")) Fail("covers.id");
        Url(c, "url"); if (!S(c, "url", 8192).StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Fail("covers.url");
        foreach (var n in new[] { "width", "height", "votes" })
        {
            if (Field(c, n).ValueKind == JsonValueKind.Null) continue;
            var value = N(c, n, n == "votes" ? 9007199254740991 : 65535);
            if (value != Math.Floor(value) || n != "votes" && value < 1) Fail(n);
        }
        var sexual = Field(c, "sexual");
        if (sexual.ValueKind != JsonValueKind.Null && Text(sexual, "sexual") is not ("safe" or "suggestive" or "explicit")) Fail("sexual");
    }

    private static void ValidateLocalCover(JsonElement local)
    {
        const string prefix = "data:image/jpeg;base64,";
        var data = S(local, "dataUrl", prefix.Length + 174764, false);
        if (!data.StartsWith(prefix, StringComparison.Ordinal)) Fail("localCover.dataUrl");
        var encoded = data[prefix.Length..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { Fail("localCover.base64"); return; }
        if (bytes.Length > 128 * 1024 || Convert.ToBase64String(bytes) != encoded || bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217) Fail("localCover.jpeg");
        var width = N(local, "width", 1600); var height = N(local, "height", 1600);
        if (width < 1 || height < 1 || width != Math.Floor(width) || height != Math.Floor(height)) Fail("localCover.dimensions");
        int position = 2, actualWidth = 0, actualHeight = 0; bool scan = false;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position++] != 255) Fail("localCover.jpeg");
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position + 3 > bytes.Length) Fail("localCover.jpeg");
            var marker = bytes[position++];
            var length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || position + length > bytes.Length || marker == 0 || marker is >= 208 and <= 216) Fail("localCover.jpeg");
            if (marker is 192 or 193 or 194)
            {
                if (length < 8) Fail("localCover.jpeg");
                actualHeight = (bytes[position + 3] << 8) | bytes[position + 4]; actualWidth = (bytes[position + 5] << 8) | bytes[position + 6];
            }
            if (marker == 218) { scan = true; break; }
            position += length;
        }
        if (!scan || actualWidth != width || actualHeight != height) Fail("localCover.dimensions");
        // Phase 1 stores no new image and displays a placeholder. Full native codec/orientation validation is a later gate.
    }
}
