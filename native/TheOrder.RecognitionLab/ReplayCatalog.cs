using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.RecognitionLab;

// Deterministic, offline stand-in for the NextMoe catalog built from the VNDB dump.
// It reproduces the interface shape (search cards, refs lookup, details, releases) and counts
// requests. Its search ranking is an approximation of an unknown remote search index.
public sealed class ReplayCatalog
{
    public sealed record Work(string VndbId, WorkMetadata Metadata, ImmutableArray<ReleaseIdentity> Releases, string[] Names, int Votes,
        ImmutableArray<(string Relation, string Target)> Relations);
    private readonly Dictionary<string, Work> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> byRef = new(StringComparer.Ordinal);
    private readonly List<(string Compact, string[] Tokens, string Id)> names = [];
    public IReadOnlyDictionary<string, Work> Works => byId;
    public CatalogView View(IEnumerable<string>? hiddenVndbIds = null) => new(this, [.. hiddenVndbIds ?? []]);

    public ReplayCatalog(string path)
    {
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(stream);
        foreach (var w in document.RootElement.GetProperty("works").EnumerateArray())
        {
            string vid = w.GetProperty("id").GetString()!;
            var work = Map(w);
            byId[work.Metadata.Id] = work;
            foreach (var r in work.Metadata.References) if (r.Url is null || r.Source is "vndb" or "steam" or "bangumi") (byRef.TryGetValue(r.Source + ":" + r.Id, out var l) ? l : byRef[r.Source + ":" + r.Id] = []).Add(work.Metadata.Id);
            foreach (var n in work.Names) { var c = Compact(n); if (c.Length > 0) names.Add((c, Tokens(n), work.Metadata.Id)); }
        }
    }
    private static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Work Map(JsonElement w)
    {
        string vid = S(w, "id")!, id = vid[1..], olang = S(w, "olang") ?? "ja";
        var titles = w.GetProperty("titles").EnumerateArray().Select(t => (Lang: S(t, "lang")!, Official: t.GetProperty("official").GetBoolean(), Title: S(t, "title")!, Latin: S(t, "latin"))).ToArray();
        string original = titles.FirstOrDefault(t => t.Lang == olang).Title ?? titles.FirstOrDefault().Title ?? "作品 " + id;
        string title = titles.FirstOrDefault(t => t.Lang == "zh-Hans" && t.Official).Title ?? titles.FirstOrDefault(t => t.Lang == "zh-Hant" && t.Official).Title ?? original;
        var evidence = new List<TitleEvidence>();
        var latin = titles.FirstOrDefault(t => t.Lang == olang).Latin;
        if (latin is { Length: > 0 }) evidence.Add(new(latin, "latin", false, null));
        foreach (var t in titles)
        {
            evidence.Add(new(t.Title, t.Official ? "official" : "alias", false, t.Lang));
            if (t.Latin is { Length: > 0 }) evidence.Add(new(t.Latin, t.Official ? "official" : "alias", false, t.Lang));
        }
        var aliases = w.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!).Where(a => a.Length is > 0 and < 500).ToArray();
        foreach (var a in aliases) evidence.Add(new(a, "alias", false, null));
        var refs = new List<WorkReference> { new("vndb", vid, "https://vndb.org/" + vid) };
        refs.AddRange(w.GetProperty("steam").EnumerateArray().Select(s => new WorkReference("steam", s.GetString()!, null)));
        refs.AddRange(w.GetProperty("bangumi").EnumerateArray().Select(s => new WorkReference("bangumi", s.GetString()!, null)));
        var releases = ImmutableArray.CreateBuilder<ReleaseIdentity>();
        string? date = null;
        foreach (var r in w.GetProperty("releases").EnumerateArray())
        {
            var rtitles = r.GetProperty("titles").EnumerateArray().Select(t => (Lang: S(t, "lang")!, Title: S(t, "title") ?? "", Mtl: t.GetProperty("mtl").GetBoolean())).ToArray();
            string rtitle = rtitles.FirstOrDefault(t => t.Lang == olang && t.Title.Length > 0).Title ?? rtitles.FirstOrDefault(t => t.Title.Length > 0).Title ?? "";
            bool patch = r.GetProperty("patch").GetBoolean(); string rtype = S(r, "rtype") ?? "";
            string kind = patch ? "patch" : rtype == "trial" ? "trial" : "default";
            string? released = S(r, "released") is { Length: 8 } d && !d.EndsWith("99", StringComparison.Ordinal) ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : null;
            if (kind == "default" && released is not null) date ??= released;
            releases.Add(new(S(r, "id")!, id, rtitle, rtitle, kind, released));
        }
        var developers = w.GetProperty("developers").EnumerateArray().Select(d => d.GetString()!).ToImmutableArray();
        var metadata = new WorkMetadata(id, title, original,
            [.. evidence.Select(e => e.Text).Concat(aliases).Where(s => s != title && s != original).Distinct(StringComparer.Ordinal)],
            [.. evidence.DistinctBy(e => (e.Text, e.Kind))], null, null, null, "", date, null, developers, [], [], [], [.. refs],
            null, null, "2026-10-09T00:00:00.000Z");
        var names = evidence.Select(e => e.Text).Append(title).Append(original).Distinct(StringComparer.Ordinal).ToArray();
        var relations = w.GetProperty("relations").EnumerateArray().Select(r => (S(r, "relation")!, S(r, "vid")!)).ToImmutableArray();
        return new(vid, metadata, releases.ToImmutable(), names, w.GetProperty("votes").GetInt32(), relations);
    }

    internal static string Compact(string value)
    {
        var b = new StringBuilder();
        foreach (var r in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes()) if (Rune.IsLetterOrDigit(r)) b.Append(r.ToString());
        return b.ToString();
    }
    private static string[] Tokens(string value)
    {
        var b = new StringBuilder();
        foreach (var r in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes()) b.Append(Rune.IsLetterOrDigit(r) ? r.ToString() : " ");
        return b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
    private static HashSet<string> Grams(string s)
    {
        var runes = s.EnumerateRunes().Select(r => r.ToString()).ToArray(); var set = new HashSet<string>(StringComparer.Ordinal);
        int n = runes.Any(r => r.Length == 1 && r[0] < 0x2E80) ? 3 : 2;
        if (runes.Length < n) { set.Add(string.Concat(runes)); return set; }
        for (int i = 0; i + n <= runes.Length; i++) set.Add(string.Concat(runes.Skip(i).Take(n)));
        return set;
    }

    // Approximate title search over work titles and aliases: exact, prefix, containment, all-token
    // and n-gram similarity, popularity as tie breaker. Release titles are not searchable.
    public IReadOnlyList<string> Search(string query, HashSet<string> hidden, int limit = 20)
    {
        var q = Compact(query); if (q.Length == 0) return [];
        var qTokens = Tokens(query); var qGrams = Grams(q);
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (c, tokens, id) in names)
        {
            if (hidden.Count > 0 && hidden.Contains(byId[id].VndbId)) continue;
            double s = 0;
            if (c == q) s = 100;
            else if (c.StartsWith(q, StringComparison.Ordinal)) s = 80 + 10.0 * q.Length / c.Length;
            else if (q.Length >= 2 && c.Contains(q, StringComparison.Ordinal)) s = 60 + 10.0 * q.Length / c.Length;
            else if (q.StartsWith(c, StringComparison.Ordinal) && c.Length >= 3) s = 55 + 10.0 * c.Length / q.Length;
            else if (qTokens.Length > 1 && qTokens.All(t => tokens.Contains(t))) s = 50;
            else
            {
                if (Math.Abs(c.Length - q.Length) > Math.Max(c.Length, q.Length) / 2) continue;
                var g = Grams(c); int common = g.Count(qGrams.Contains); double sim = 2.0 * common / (g.Count + qGrams.Count);
                if (sim >= .45) s = 40 * sim;
            }
            if (s > 0 && (!best.TryGetValue(id, out var old) || s > old)) best[id] = s;
        }
        return [.. best.OrderByDescending(p => p.Value + Math.Log10(1 + byId[p.Key].Votes) * .5).ThenBy(p => p.Key, StringComparer.Ordinal).Take(limit).Select(p => p.Key)];
    }

    public static string Format(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    internal bool TryRef(string r, out List<string> ids) => byRef.TryGetValue(r, out ids!);
    internal bool Has(string id) => byId.ContainsKey(id);
    internal Work Get(string id) => byId[id];
}

internal static class PipeExtension { public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value); }

public sealed class CatalogView(ReplayCatalog source, HashSet<string> hidden) : IRecognitionCatalog
{
    private readonly ReplayCatalog data = source;
    public ConcurrentDictionary<string, int> Requests { get; } = new();
    // Every work id returned by search or refs lookup: the retrieval pool before ranking.
    public ConcurrentDictionary<string, byte> Seen { get; } = new();
    private T Track<T>(T works) where T : IEnumerable<WorkMetadata> { foreach (var w in works) Seen.TryAdd("v" + w.Id, 0); return works; }
    private bool Visible(string id) => data.Has(id) && !hidden.Contains(data.Get(id).VndbId);
    private void Count(string kind) => Requests.AddOrUpdate(kind, 1, (_, n) => n + 1);
    public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Count("search"); Count("search-cards");
        return Task.FromResult(new OnlinePage(Track<ImmutableArray<WorkMetadata>>([.. data.Search(query, hidden).Select(id => data.Get(id).Metadata)]), null));
    }
    public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Count("details");
        return Visible(id) ? Task.FromResult(data.Get(id).Metadata) : throw new OnlineException(OnlineFailure.NotFound);
    }
    public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Count("refs");
        return Task.FromResult(references.SelectMany(r => data.TryRef(r, out var ids) ? ids : []).Distinct(StringComparer.Ordinal).Where(Visible)
            .OrderBy(id => id, StringComparer.Ordinal).Select(id => data.Get(id).Metadata).ToImmutableArray().Pipe(Track));
    }
    public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Count("releases");
        return Visible(id) ? Task.FromResult(data.Get(id).Releases) : throw new OnlineException(OnlineFailure.NotFound);
    }
    public Task TestConnectionAsync(CancellationToken cancellation) => Task.CompletedTask;
    public void ClearCache() { }
}
