using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Recognition;

// Keep the Electron oracle intact, then apply the native review safety policy explicitly.
// Missing edition information is not proof that an FD/remake is the original work.
public sealed class ReviewSafeRanking : IRecognitionRanker
{
    private readonly EvidenceRanking compatibility = new();
    public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, IEnumerable<WorkMetadata> works,
        IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases = null, CancellationToken cancellation = default)
    {
        var ranked = compatibility.Rank(evidence, works, releases, cancellation);
        return [.. ranked.Select(candidate =>
        {
            cancellation.ThrowIfCancellationRequested();
            var canonical = new[] { candidate.Work.Title, candidate.Work.OriginalTitle }
                .Concat((candidate.Work.TitleEvidence ?? []).Where(t => !t.IsMachine && t.Kind is "official" or "original" or "latin").Select(t => t.Text)).Where(t => t.Length > 0).ToArray();
            bool unresolved = evidence.Titles.Where(t => t.Source != "sidecar" && t.Source != "readme-heading")
                .Any(local => canonical.Any(remote => { var comparison = TitleMatching.Compare(local.Value, remote); return comparison.Version == "unspecified" && comparison.Score >= .5 && (TitleMatching.HasVersion(local.Value) || TitleMatching.HasVersion(remote)); })
                    && !canonical.Any(remote => { var comparison = TitleMatching.Compare(local.Value, remote); return comparison.Exact && comparison.Version == "same"; }));
            return !unresolved || candidate.Vetoed ? candidate : candidate with {
                Vetoed = true, VersionNeedsConfirmation = true, HighConfidence = false, EvidenceLevel = 0,
                Reason = "版本身份尚未确认；请核对后重新识别，或明确手动关联。"
            };
        }).OrderBy(c => c.Vetoed)]; // Stable within each group; score remains the legacy score.
    }
}
