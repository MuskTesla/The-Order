using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;

namespace TheOrder.RecognitionLab;

public sealed record EvidenceCase(string Id, string Name, string Folder, CaseEvidence Evidence, string Expected,
    ImmutableArray<string> Hidden, string Split, string Kind, ImmutableArray<string> Tags, string? Note = null);
public sealed record CaseEvidence(ImmutableArray<LocalTitleEvidence> Titles, ImmutableArray<ReferenceEvidence> References,
    ImmutableArray<string> Companies, ImmutableArray<string> Warnings, bool AuxiliaryDirectory);

// Replays recorded local evidence, so evaluation never touches game directories again.
public sealed class RecordedEvidenceReader(CaseEvidence evidence) : IRecognitionEvidenceReader
{
    public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation) =>
        Task.FromResult(new LocalEvidenceSnapshot(new RecognitionEvidence(evidence.Titles, evidence.References, evidence.Companies,
            evidence.Warnings, [], evidence.AuxiliaryDirectory, []), [], ["recorded"], "recorded") { IncludePe = includePe });
}

public static class Replay
{
    public sealed record Outcome(string Id, string Split, string Kind, string Expected, ImmutableArray<string> Tags, string? Top, double TopScore,
        bool ExpectedInCandidates, bool ExpectedInPool, string? Auto, string? AutoBeforeApplyCheck, string Verdict, double Milliseconds,
        IReadOnlyDictionary<string, int> Requests, ImmutableArray<string> Candidates, string? Reason);

    public delegate IRecognitionEngine EngineFactory(ITitleIndex index, IRecognitionEvidenceReader reader, IRecognitionCatalog catalog);
    public delegate RankedRecognitionCandidate? ApplyCheck(RecognitionResult result, RankedRecognitionCandidate selected, IRecognitionCatalog catalog);

    public static async Task<List<Outcome>> RunAsync(IReadOnlyList<EvidenceCase> cases, ReplayCatalog catalog, ITitleIndex index,
        EngineFactory factory, Func<RecognitionResult, RankedRecognitionCandidate?> select, ApplyCheck apply, int parallel = 1)
    {
        // Parallel runs are for accuracy only; latency is reported from sequential runs.
        var results = new Outcome[cases.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, cases.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (i, _) =>
        {
            var outcomes = new List<Outcome>();
            await RunOne(cases[i], outcomes);
            results[i] = outcomes[0];
        });
        return [.. results];

        async Task RunOne(EvidenceCase c, List<Outcome> outcomes)
        {
            var view = catalog.View(c.Hidden);
            var installation = new Installation("i0", c.Folder, null, []);
            var entry = Program.Entry(c.Name, installation);
            var snapshot = new RecognitionEntrySnapshot(entry, new(Guid.Empty, Guid.Empty, 0, 0)) { Mode = RecognitionMode.MissingMetadata };
            var engine = factory(index, new RecordedEvidenceReader(c.Evidence), view);
            var watch = Stopwatch.StartNew();
            if (c.Tags.Contains("evidence-failure"))
            {
                // Production: the evidence collector threw, so the whole recognition job failed.
                outcomes.Add(new(c.Id, c.Split, c.Kind, c.Expected, c.Tags, null, 0, false, false, null, null, c.Expected == "none" ? "abstain-correct" : "abstain",
                    0, new Dictionary<string, int>(), [], "evidence collection failed"));
                return;
            }
            var result = await engine.RecognizeAsync(snapshot, Guid.NewGuid(), true, true, _ => { }, CancellationToken.None);
            var chosen = select(result);
            var applied = chosen is null ? null : apply(result, chosen, view);
            watch.Stop();
            string V(string id) => "v" + id;
            var auto = applied is null ? null : V(applied.Work.Id);
            string verdict = c.Expected == "none"
                ? auto is null ? "abstain-correct" : "auto-wrong"
                : auto is null ? "abstain" : auto == c.Expected ? "auto-correct" : "auto-wrong";
            var poolIds = view.Seen.Keys.ToHashSet();
            outcomes.Add(new(c.Id, c.Split, c.Kind, c.Expected, c.Tags, result.Candidates.FirstOrDefault() is { } t ? V(t.Work.Id) : null,
                result.Candidates.FirstOrDefault()?.Confidence ?? 0, result.Candidates.Any(x => V(x.Work.Id) == c.Expected),
                poolIds.Contains(c.Expected), auto, chosen is null ? null : V(chosen.Work.Id), verdict, watch.Elapsed.TotalMilliseconds,
                view.Requests.ToDictionary(), [.. result.Candidates.Select(x => $"{V(x.Work.Id)}|{x.Confidence:0.000}|{(x.Vetoed ? "veto" : x.HighConfidence ? "high" : "-")}|{x.Reason}")],
                result.EnrichmentMessage));
        }
    }

    public static string Summary(IEnumerable<Outcome> outcomes)
    {
        var lines = new List<string>();
        foreach (var group in outcomes.GroupBy(o => (o.Kind, o.Split)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Split))
        {
            var all = group.ToList(); var positives = all.Where(o => o.Expected != "none").ToList(); var negatives = all.Where(o => o.Expected == "none").ToList();
            int autoCorrect = positives.Count(o => o.Verdict == "auto-correct"), autoWrong = all.Count(o => o.Verdict == "auto-wrong");
            int autos = all.Count(o => o.Auto is not null);
            lines.Add($"[{group.Key.Kind}/{group.Key.Split}] n={all.Count} (identifiable {positives.Count}, absent/none {negatives.Count})");
            lines.Add($"  top1 correct            {positives.Count(o => o.Top == o.Expected)}/{positives.Count}");
            lines.Add($"  expected in shown top5  {positives.Count(o => o.ExpectedInCandidates)}/{positives.Count}");
            lines.Add($"  auto coverage (correct) {autoCorrect}/{positives.Count}");
            lines.Add($"  auto precision          {autoCorrect}/{autos}");
            lines.Add($"  auto wrong              {autoWrong}/{all.Count}  (on absent/none: {negatives.Count(o => o.Verdict == "auto-wrong")}/{negatives.Count})");
            lines.Add($"  correct abstain on none {negatives.Count(o => o.Verdict == "abstain-correct")}/{negatives.Count}");
            var ms = all.Select(o => o.Milliseconds).Order().ToArray();
            lines.Add($"  latency ms p50/p95/max  {ms[ms.Length / 2]:0}/{ms[(int)(ms.Length * .95)]:0}/{ms[^1]:0}");
            var req = all.SelectMany(o => o.Requests).GroupBy(p => p.Key).Select(g => $"{g.Key}={(double)g.Sum(p => p.Value) / all.Count:0.0}");
            lines.Add($"  requests/case           {string.Join(' ', req)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static readonly JsonSerializerOptions Options = ReplayOptions();
    private static JsonSerializerOptions ReplayOptions()
    {
        // Research response caches may carry bounded transient identity facts. Shipping serializers
        // deliberately ignore them, preserving the existing library/review/online JSON contracts.
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(WorkMetadata)) return;
            var fact = info.Properties.Single(p => p.Name == nameof(WorkMetadata.CatalogIdentity));
            fact.Get = value => ((WorkMetadata)value).CatalogIdentity;
            var property = typeof(WorkMetadata).GetProperty(nameof(WorkMetadata.CatalogIdentity))!;
            fact.Set = (value, facts) => property.SetValue(value, facts);
            fact.ShouldSerialize = (_, _) => true;
        });
        return new(Program.Json) { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never, TypeInfoResolver = resolver };
    }
}
