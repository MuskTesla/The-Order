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
        var ranked = ranker.Rank(result.Identity.Evidence, context, new Dictionary<string, ImmutableArray<ReleaseIdentity>> { [work.Id] = releases });
        var current = ranked.FirstOrDefault(c => c.Work.Id == work.Id);
        return current is null || current.Vetoed || ranked[0].Work.Id != work.Id || !current.HighConfidence || !AutomaticRecognitionPolicy.Eligible(current) ? null : current;
    }
}
