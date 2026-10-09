using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Recognition;

namespace TheOrder.RecognitionLab;

// Bounded live validation against the real NextMoe catalog through the production transport.
// The key is read from THEORDER_LAB_NEXTMOE_KEY at request time and never written anywhere.
// Responses are cached in a private file (catalog data only) so several engines share one fetch.
public static partial class Live
{
    private sealed class EnvironmentCredential : ICredentialProvider
    {
        public async Task<string?> ReadAsync(CancellationToken cancellation)
        {
            if (Environment.GetEnvironmentVariable("THEORDER_LAB_NEXTMOE_KEY") is { Length: > 0 } secret) return secret;
            if (Environment.GetEnvironmentVariable("THEORDER_LAB_TEST_CREDENTIAL_PROFILE") is not { Length: > 0 } directory) return null;
            var request = TheOrder.Platform.Windows.NativeProfilePaths.Select(NativeProfileKind.Development, "Test", directory);
            if (request.Kind != NativeProfileKind.Test) throw new InvalidOperationException("Test credential profile required");
            using var profile = TheOrder.Platform.Windows.NativeProfileStore.Open(request, new TheOrder.Infrastructure.Legacy.NativeLibraryCodec());
            return await new TheOrder.Platform.Windows.IsolatedCredentialStore(new NativeConfiguration(directory, false, profile)).ReadAsync(cancellation);
        }
    }

    // Throttled, caching, request-counting decorator. Hidden VNDB ids simulate a catalog that lacks a work.
    public sealed class Recorder(IRecognitionCatalog inner, ConcurrentDictionary<string, string> cache, HashSet<string> hidden) : IRecognitionCatalog
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Queue<DateTime> Sent = new();
        public static int Http;
        public Dictionary<string, int> Requests { get; } = [];
        public HashSet<string> Seen { get; } = [];
        private static async Task Throttle(int calls)
        {
            await Gate.WaitAsync();
            try
            {
                for (int i = 0; i < calls; i++)
                {
                    while (Sent.Count > 0 && DateTime.UtcNow - Sent.Peek() > TimeSpan.FromSeconds(61)) Sent.Dequeue();
                    if (Sent.Count >= 45) await Task.Delay(Sent.Peek().AddSeconds(61) - DateTime.UtcNow);
                    while (Sent.Count > 0 && DateTime.UtcNow - Sent.Peek() > TimeSpan.FromSeconds(61)) Sent.Dequeue();
                    Sent.Enqueue(DateTime.UtcNow); Interlocked.Increment(ref Http);
                }
            }
            finally { Gate.Release(); }
        }
        private async Task<T> Cached<T>(string kind, string key, int calls, Func<Task<T>> fetch)
        {
            Requests[kind] = Requests.GetValueOrDefault(kind) + 1;
            if (cache.TryGetValue(kind + "|" + key, out var json)) return JsonSerializer.Deserialize<T>(json, Replay.Options)!;
            if (Environment.GetEnvironmentVariable("THEORDER_LAB_REPLAY_ONLY") == "1")
                throw new InvalidOperationException("Replay cache missing " + kind + "|" + key);
            await Throttle(calls);
            var value = await fetch();
            cache[kind + "|" + key] = JsonSerializer.Serialize(value, Replay.Options);
            return value;
        }
        // The absence protocol may explicitly hide verified equivalent catalog records as well
        // as the original VNDB identity. An edition is never hidden merely by a similar title.
        private bool Visible(WorkMetadata w) => !hidden.Contains("nm:" + w.Id)
            && !w.References.Any(r => hidden.Contains(r.Source == "vndb" ? r.Id : r.Source + ":" + r.Id));
        private T Track<T>(T works) where T : IEnumerable<WorkMetadata> { foreach (var w in works) Seen.Add(w.Id); return works; }
        public async Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation)
        {
            var page = await Cached("search", query, 2, () => inner.SearchAsync(query, cursor, cancellation));
            return new(Track<ImmutableArray<WorkMetadata>>([.. page.Items.Where(Visible)]), null);
        }
        public async Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation)
        {
            var work = await Cached("details", id, 1, () => inner.DetailsAsync(id, false, cancellation));
            return Visible(work) ? work : throw new OnlineException(OnlineFailure.NotFound);
        }
        public async Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation)
        {
            var works = await Cached("refs", string.Join(",", references.Order(StringComparer.Ordinal)), 1, () => inner.ResolveReferencesAsync(references, cancellation));
            return Track<ImmutableArray<WorkMetadata>>([.. works.Where(Visible)]);
        }
        public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation) =>
            Cached("releases", id, 2, () => inner.ReleasesAsync(id, cancellation));
        public Task TestConnectionAsync(CancellationToken cancellation) => inner.TestConnectionAsync(cancellation);
        public void ClearCache() { }
    }

    public static (IRecognitionCatalog Catalog, IDisposable Owner) Production()
    {
        var network = new CatalogNetwork();
        var transport = new CatalogTransport(network.Client, new EnvironmentCredential(), new Uri("https://api.nextmoe.dev/v2/"));
        return (new NextMoeCatalog(transport), network);
    }

    // Stores the key in an isolated *Test* profile through the application's own profile store and
    // DPAPI credential store (the Settings page uses the same SaveAsync). Refuses anything but Test.
    public static async Task<int> SeedTestCredential(string directory)
    {
        var secret = Environment.GetEnvironmentVariable("THEORDER_LAB_NEXTMOE_KEY");
        if (string.IsNullOrEmpty(secret)) { Console.Error.WriteLine("THEORDER_LAB_NEXTMOE_KEY not set"); return 2; }
        var request = TheOrder.Platform.Windows.NativeProfilePaths.Select(NativeProfileKind.Development, "Test", directory);
        if (request.Kind != NativeProfileKind.Test) { Console.Error.WriteLine("not a Test profile: " + request.Kind); return 2; }
        using var profile = TheOrder.Platform.Windows.NativeProfileStore.Open(request, new TheOrder.Infrastructure.Legacy.NativeLibraryCodec());
        var store = new TheOrder.Platform.Windows.IsolatedCredentialStore(new NativeConfiguration(directory, false, profile));
        await store.SaveAsync(secret, CancellationToken.None);
        Console.WriteLine("stored in Test profile: " + await store.HasCredentialAsync(CancellationToken.None));
        return 0;
    }

    // Read-only summary of an isolated Test profile after the application has exited: library
    // entries with their linked work, and the recognition review queue (application codecs).
    public static int DumpTestProfile(string directory)
    {
        var request = TheOrder.Platform.Windows.NativeProfilePaths.Select(NativeProfileKind.Development, "Test", directory);
        if (request.Kind != NativeProfileKind.Test) return 2;
        using var profile = TheOrder.Platform.Windows.NativeProfileStore.Open(request, new TheOrder.Infrastructure.Legacy.NativeLibraryCodec());
        using var persistence = profile.CreateLibraryPersistence();
        var entries = persistence.ReadMain() is { } main ? new TheOrder.Infrastructure.Legacy.NativeLibraryCodec().Decode(main) : [];
        foreach (var e in entries)
            Console.WriteLine($"entry name={e.Name} installs={e.Installations.Length} work={(e.Metadata is { } m ? m.Id + " " + m.Title + " [" + string.Join(",", m.References.Select(r => r.Source + ":" + r.Id).Take(3)) + "]" : "none")} notes={e.Notes.Length}");
        var reviews = new TheOrder.Infrastructure.Recognition.RecognitionReviewCodec().Read(new(profile.ReadComponent("native-recognition.json"), null));
        foreach (var r in reviews)
            Console.WriteLine($"review title={r.Title} status={r.Status} version={r.AlgorithmVersion} candidates={string.Join(" / ", (r.Result?.Candidates ?? []).Take(3).Select(c => c.Work.Title + " " + c.Confidence.ToString("0.000") + (c.HighConfidence ? " high" : "")))}");
        return 0;
    }

    public static string? Vndb(WorkMetadata work) => work.References.FirstOrDefault(r => r.Source == "vndb" && r.Id.StartsWith('v'))?.Id;

    // live <cases.json> <index.json.gz> <cache.json> <out.json> <engine> [coverage]
    public static async Task<int> Run(string casesPath, string indexPath, string cachePath, string output, string engineName, bool coverageOnly)
    {
        if (Environment.GetEnvironmentVariable("THEORDER_LAB_REPLAY_ONLY") != "1" && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THEORDER_LAB_NEXTMOE_KEY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THEORDER_LAB_TEST_CREDENTIAL_PROFILE"))) { Console.Error.WriteLine("No configured credential source"); return 2; }
        var cases = JsonSerializer.Deserialize<List<EvidenceCase>>(await File.ReadAllTextAsync(casesPath), Replay.Options)!;
        var cache = File.Exists(cachePath) ? new ConcurrentDictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath))!) : new();
        var (production, owner) = Production();
        using var _ = owner;
        var index = new TitleIndex(() => File.OpenRead(indexPath));
        async Task Save() => await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache));

        // Ground-truth coverage, independent of the recognizer: does NextMoe hold a work with this VNDB id?
        var expected = cases.Select(c => c.Expected).Concat(cases.SelectMany(c => c.Hidden)).Where(e => e.StartsWith('v')).Distinct().Order(StringComparer.Ordinal).ToArray();
        var covered = new Dictionary<string, string>(StringComparer.Ordinal);
        var lookup = new Recorder(production, cache, []);
        foreach (var chunk in expected.Chunk(100))
            foreach (var w in await lookup.ResolveReferencesAsync([.. chunk.Select(v => "vndb:" + v)], CancellationToken.None))
                if (Vndb(w) is { } v) covered.TryAdd(v, w.Id);
        await Save();
        Console.Error.WriteLine($"coverage: {covered.Count}/{expected.Length} expected VNDB ids exist in NextMoe");
        if (coverageOnly) { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(covered, Program.Json)); return 0; }

        var (factory, select, apply) = Engines.Get(engineName);
        var outcomes = new List<object>(); int n = 0;
        foreach (var c in cases)
        {
            var view = new Recorder(production, cache, [.. c.Hidden]);
            var entry = Program.Entry(c.Name, new("i0", c.Folder, null, []));
            var watch = Stopwatch.StartNew(); string? auto = null, top = null, failure = null; var candidates = new List<string>();
            if (c.Tags.Contains("evidence-failure")) failure = "evidence collection failed";
            else
            {
                try
                {
                    var result = await factory(index, new RecordedEvidenceReader(c.Evidence), view).RecognizeAsync(
                        new(entry, new(Guid.Empty, Guid.Empty, 0, 0)) { Mode = RecognitionMode.MissingMetadata }, Guid.NewGuid(), true, true, _ => { }, CancellationToken.None);
                    if (result.EnrichmentUnavailable) failure = result.EnrichmentMessage;
                    top = result.Candidates.FirstOrDefault() is { } t ? Vndb(t.Work) ?? "nm:" + t.Work.Id : null;
                    candidates.AddRange(result.Candidates.Select(x => $"{Vndb(x.Work) ?? "nm:" + x.Work.Id}|{x.Confidence:0.000}|{(x.Vetoed ? "veto" : x.HighConfidence ? "high" : "-")}|{x.Work.Title}|{x.Reason}"));
                    if (select(result) is { } chosen && apply(result, chosen, view) is { } applied) auto = Vndb(applied.Work) ?? "nm:" + applied.Work.Id;
                }
                catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
            }
            // A work NextMoe does not hold is not identifiable there: the correct outcome is to abstain.
            string truth = c.Expected != "none" && c.Hidden.IsEmpty && covered.ContainsKey(c.Expected) ? c.Expected : "none";
            string verdict = truth == "none" ? auto is null ? "abstain-correct" : "auto-wrong" : auto is null ? "abstain" : auto == truth ? "auto-correct" : "auto-wrong";
            outcomes.Add(new { c.Id, c.Split, c.Kind, Expected = truth, Source = c.Expected, c.Tags, Top = top, Auto = auto, Verdict = verdict,
                ExpectedInPool = truth != "none" && covered.TryGetValue(truth, out var nm) && view.Seen.Contains(nm), Milliseconds = watch.Elapsed.TotalMilliseconds,
                view.Requests, Candidates = candidates, Failure = failure });
            if (++n % 10 == 0) { await Save(); Console.Error.WriteLine($"{n}/{cases.Count} http={Recorder.Http}"); }
        }
        await Save();
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(outcomes, Program.Json));
        Console.Error.WriteLine($"done http={Recorder.Http}");
        return 0;
    }
}
