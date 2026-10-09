using System.Collections.Immutable;

namespace TheOrder.Application;

// Import/startup authorizes this bounded policy, not replacement of an existing relation.
// Confidence is the existing ranking score, not a calibrated correctness probability.
public static class AutomaticRecognitionPolicy
{
    public const double Threshold = .9;

    public static bool Eligible(RankedRecognitionCandidate candidate) =>
        double.IsFinite(candidate.Confidence) && candidate.Confidence > Threshold && candidate.Confidence <= 1
        && !candidate.Vetoed && !candidate.VersionNeedsConfirmation && candidate.Conflicts.IsDefaultOrEmpty;

    public static RankedRecognitionCandidate? Select(RecognitionResult result)
    {
        if (result.Input.Mode != RecognitionMode.MissingMetadata || result.Input.Entry.Metadata is not null
            || result.Input.Entry.Installations.IsEmpty || result.EnrichmentUnavailable) return null;
        return result.Candidates.Where(Eligible).OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Work.Id, StringComparer.Ordinal).FirstOrDefault();
    }
}
