using System.Collections.Immutable;

namespace TheOrder.Application;

// Import/startup authorizes this bounded policy, not replacement of an existing relation.
// Confidence is the existing ranking score, not a calibrated correctness probability.
// HighConfidence is the ranker's uniqueness judgement against the other candidates and homonyms.
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
        // Only a unique top identity is applied. Several eligible candidates are a tie for the
        // user to resolve, never a choice by score order or catalog id.
        return result.Candidates.FirstOrDefault() is { HighConfidence: true } top && Eligible(top) ? top : null;
    }
}
