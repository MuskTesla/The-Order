using TheOrder.Application;
using TheOrder.Infrastructure.Recognition;

namespace TheOrder.RecognitionLab;

// Named pipeline configurations compared by the replay harness. "baseline" is only meaningful in
// the frozen build of commit 5b574f0 (.artifacts/rnd/baseline-src); the current tree's shared
// identity builder and policy have changed.
public static partial class Engines
{
    public static (Replay.EngineFactory, Func<RecognitionResult, RankedRecognitionCandidate?>, Replay.ApplyCheck) Get(string name) => name switch
    {
        // Production as configured in Composition.cs, including the application-time single-work
        // re-rank performed by RecognitionReviewService at baseline.
        "baseline" => ((index, reader, catalog) => new RecognitionEngine(index, reader, new ReviewSafeRanking(), catalog),
            AutomaticRecognitionPolicy.Select, (r, s, c) => SingleWorkApply(new ReviewSafeRanking(), r, s, c)),
        _ => Extra(name),
    };

    internal static RankedRecognitionCandidate? SingleWorkApply(IRecognitionRanker ranker, RecognitionResult result, RankedRecognitionCandidate selected, IRecognitionCatalog catalog)
    {
        var work = catalog.DetailsAsync(selected.Work.Id, true, default).GetAwaiter().GetResult();
        if (work.Id != selected.Work.Id) return null;
        var releases = catalog.ReleasesAsync(work.Id, default).GetAwaiter().GetResult();
        var ranked = ranker.Rank(result.Identity.Evidence, [work], new Dictionary<string, System.Collections.Immutable.ImmutableArray<ReleaseIdentity>> { [work.Id] = releases });
        return ranked.IsEmpty || ranked[0].Vetoed || !AutomaticRecognitionPolicy.Eligible(ranked[0]) ? null : ranked[0];
    }
}
