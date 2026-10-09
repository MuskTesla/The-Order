using System.Text.Json;
using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;

namespace TheOrder.RecognitionLab;

public static partial class Engines
{
    private static (Replay.EngineFactory, Func<RecognitionResult, RankedRecognitionCandidate?>, Replay.ApplyCheck) Extra(string name) => name switch
    {
        // Candidate as configured in Composition.cs, with the contextual application-time check
        // performed by RecognitionReviewService.ApplyAutomaticAsync.
        "v2" => ((index, reader, catalog) => new RecognitionEngine(index, reader, new ReleaseAwareRanking(), catalog),
            AutomaticRecognitionPolicy.Select, (r, s, c) => ContextApply(new ReleaseAwareRanking(), r, s, c)),
        // Ablation: v2 ranking with the previous "highest eligible candidate" policy (no uniqueness).
        "v2-highest" => ((index, reader, catalog) => new RecognitionEngine(index, reader, new ReleaseAwareRanking(), catalog),
            HighestEligible, (r, s, c) => SingleWorkApply(new ReleaseAwareRanking(), r, s, c)),
        // Ablation: v2 identity builder and policy with the old ranker.
        "v2-oldranker" => ((index, reader, catalog) => new RecognitionEngine(index, reader, new ReviewSafeRanking(), catalog),
            AutomaticRecognitionPolicy.Select, (r, s, c) => SingleWorkApply(new ReviewSafeRanking(), r, s, c)),
        _ => throw new ArgumentException("unknown engine " + name),
    };

    // AutomaticRecognitionPolicy.Select at baseline 5b574f0.
    private static RankedRecognitionCandidate? HighestEligible(RecognitionResult result) =>
        result.Input.Mode != RecognitionMode.MissingMetadata || result.Input.Entry.Metadata is not null || result.Input.Entry.Installations.IsEmpty || result.EnrichmentUnavailable ? null
        : result.Candidates.Where(AutomaticRecognitionPolicy.Eligible).OrderByDescending(c => c.Confidence).ThenBy(c => c.Work.Id, StringComparer.Ordinal).FirstOrDefault();

    private static RankedRecognitionCandidate? ContextApply(IRecognitionRanker ranker, RecognitionResult result, RankedRecognitionCandidate selected, IRecognitionCatalog catalog)
    {
        var work = catalog.DetailsAsync(selected.Work.Id, true, default).GetAwaiter().GetResult();
        if (work.Id != selected.Work.Id) return null;
        var releases = catalog.ReleasesAsync(work.Id, default).GetAwaiter().GetResult();
        var context = result.Candidates.Select(c => c.Work.Id == work.Id ? work : c.Work);
        var ranked = ranker.Rank(result.Identity.Evidence, context, result.ReleaseContext.SetItem(work.Id, releases));
        var current = ranked.FirstOrDefault(c => c.Work.Id == work.Id);
        return current is null || current.Vetoed || ranked[0].Work.Id != work.Id || !current.HighConfidence || !AutomaticRecognitionPolicy.Eligible(current) ? null : current;
    }
    // CPU cost of ranking each case's actual retrieval pool (the engine ranks it twice per job).
    public static async Task<int> RankBench(string casesPath, string catalogPath, string indexPath)
    {
        var cases = JsonSerializer.Deserialize<List<EvidenceCase>>(await File.ReadAllTextAsync(casesPath), Replay.Options)!;
        var catalog = new ReplayCatalog(catalogPath); var index = new TitleIndex(() => File.OpenRead(indexPath));
        var rankers = new (string Name, IRecognitionRanker Ranker)[] { ("ReviewSafeRanking", new ReviewSafeRanking()), ("ReleaseAwareRanking", new ReleaseAwareRanking()) };
        var totals = rankers.ToDictionary(r => r.Name, _ => new List<double>());
        foreach (var c in cases.Where(c => !c.Tags.Contains("evidence-failure")))
        {
            var view = catalog.View(c.Hidden);
            var entry = Program.Entry(c.Name, new("i0", c.Folder, null, []));
            var reader = new RecordedEvidenceReader(c.Evidence);
            var local = await reader.ReadAsync(entry, true, CancellationToken.None);
            var identity = await RecognitionIdentityBuilder.BuildAsync(entry, local.Evidence, index, CancellationToken.None);
            await new RecognitionEngine(index, reader, new ReleaseAwareRanking(), view).RecognizeAsync(new(entry, new(Guid.Empty, Guid.Empty, 0, 0)), Guid.NewGuid(), true, true, _ => { }, CancellationToken.None);
            var pool = view.Seen.Keys.Take(128).Select(v => catalog.Works[v[1..]]).ToArray();
            var releases = pool.Take(2).ToDictionary(w => w.Metadata.Id, w => w.Releases);
            foreach (var (name, ranker) in rankers)
            {
                ranker.Rank(identity.Evidence, pool.Select(w => w.Metadata), releases);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5; i++) ranker.Rank(identity.Evidence, pool.Select(w => w.Metadata), releases);
                totals[name].Add(watch.Elapsed.TotalMilliseconds / 5);
            }
        }
        foreach (var (name, ms) in totals) { var o = ms.Order().ToArray(); Console.WriteLine($"{name}: cases={o.Length} p50={o[o.Length / 2]:0.0}ms p95={o[(int)(o.Length * .95)]:0.0}ms max={o[^1]:0.0}ms"); }
        return 0;
    }


    // explain <cases.json> <case id> <index> <cache.json>: one case through cached live responses,
    // printing the decision and every step of the contextual commit check.
    public static async Task<int> Explain(string casesPath, string id, string indexPath, string cachePath)
    {
        var c = JsonSerializer.Deserialize<List<EvidenceCase>>(await File.ReadAllTextAsync(casesPath), Replay.Options)!.Single(x => x.Id == id);
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath))!);
        var (production, owner) = Live.Production(); using var _ = owner;
        var view = new Live.Recorder(production, cache, [.. c.Hidden]);
        var index = new TitleIndex(() => File.OpenRead(indexPath));
        var entry = Program.Entry(c.Name, new("i0", c.Folder, null, []));
        var previewLocal = await new RecordedEvidenceReader(c.Evidence).ReadAsync(entry, true, default);
        var previewIdentity = await RecognitionIdentityBuilder.BuildAsync(entry, previewLocal.Evidence, index, default);
        Console.WriteLine("queries: " + string.Join(" | ", previewIdentity.Queries));
        var preview = new Dictionary<string, WorkMetadata>(StringComparer.Ordinal);
        if (!previewIdentity.References.IsEmpty)
            foreach (var previewWork in await view.ResolveReferencesAsync(previewIdentity.References, default)) preview[previewWork.Id] = previewWork;
        foreach (var query in previewIdentity.Queries.Take(6))
            foreach (var previewWork in (await view.SearchAsync(query, null, default)).Items) preview[previewWork.Id] = previewWork;
        Console.WriteLine("preliminary:");
        foreach (var x in new ReleaseAwareRanking().Rank(previewIdentity.Evidence, preview.Values).Take(10))
            Console.WriteLine($"   {x.Work.Id} {x.Confidence:0.000} veto={x.Vetoed} vnc={x.VersionNeedsConfirmation} official={(x.Work.TitleEvidence ?? []).Any(t => !t.IsMachine && t.Kind is "official" or "original" or "latin")} {x.Work.Title} | {x.Reason}");
        var result = await new RecognitionEngine(index, new RecordedEvidenceReader(c.Evidence), new ReleaseAwareRanking(), view).RecognizeAsync(
            new(entry, new(Guid.Empty, Guid.Empty, 0, 0)) { Mode = RecognitionMode.MissingMetadata }, Guid.NewGuid(), true, true, _ => { }, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { result.Identity.Evidence.IndexProofs, result.Identity.Evidence.Homonyms, result.Identity.Queries, result.Identity.References }, Program.Json));
        void Show(string label, IEnumerable<RankedRecognitionCandidate> list) { Console.WriteLine(label); foreach (var x in list) Console.WriteLine($"   {x.Work.Id} {Live.Vndb(x.Work)} {x.Confidence:0.000} high={x.HighConfidence} veto={x.Vetoed} vnc={x.VersionNeedsConfirmation} lvl={x.EvidenceLevel} refs=[{string.Join(",", x.MatchedReferences)}] {x.Work.Title} | {x.Reason}"); }
        Show("result:", result.Candidates);
        Console.WriteLine("unavailable=" + result.EnrichmentUnavailable + " " + result.EnrichmentMessage);
        var chosen = AutomaticRecognitionPolicy.Select(result); Console.WriteLine("selected: " + chosen?.Work.Id);
        if (chosen is null) return 0;
        var work = await view.DetailsAsync(chosen.Work.Id, true, default); Console.WriteLine("details id: " + work.Id);
        var releases = await view.ReleasesAsync(work.Id, default); Console.WriteLine("releases: " + releases.Length);
        var ranked = new ReleaseAwareRanking().Rank(result.Identity.Evidence, result.Candidates.Select(x => x.Work.Id == work.Id ? work : x.Work), result.ReleaseContext.SetItem(work.Id, releases));
        Show("context re-rank:", ranked);
        return 0;
    }
}
