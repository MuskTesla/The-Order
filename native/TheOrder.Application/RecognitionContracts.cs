using TheOrder.Application;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record LocalTitleEvidence(string Value,string Source);
public sealed record ReferenceEvidence(string Value,string Source,string? Via=null);
public sealed record TitleIndexProof(string ObservedTitle,string Source,ImmutableArray<string> RecordNames,ImmutableArray<string> References);
// A local title whose exact index key belongs to more than one catalog identity.
public sealed record TitleHomonym(string ObservedTitle,string Source,ImmutableArray<string> Identities);
public sealed record RecognitionEvidence(ImmutableArray<LocalTitleEvidence> Titles,ImmutableArray<ReferenceEvidence> References,
    ImmutableArray<string> Companies,ImmutableArray<string> Warnings,ImmutableArray<string> Fingerprints,bool AuxiliaryDirectory,
    ImmutableArray<TitleIndexProof> IndexProofs)
{
    // Added by recognition v2. Documents written before it deserialize to an empty list.
    public ImmutableArray<TitleHomonym> Homonyms { get; init; } = [];
}
public sealed record ReleaseIdentity(string Id,string WorkId,string Title,string OriginalTitle,string Kind,string? Date);
public sealed record CandidateExplanation(string Source,string ObservedTitle,string MatchedTitle,double Similarity,bool Exact,string Version,string? Conflict);
public sealed record RankedRecognitionCandidate(WorkMetadata Work,double Confidence,string Reason,bool HighConfidence,
    bool Vetoed,ImmutableArray<string> Conflicts,int EvidenceLevel,ImmutableArray<string> MatchedReferences,
    ImmutableArray<CandidateExplanation> Evidence)
{
    public bool VersionNeedsConfirmation { get; init; }
}
public interface IRecognitionRanker
{
    ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence,IEnumerable<WorkMetadata> works,
        IReadOnlyDictionary<string,ImmutableArray<ReleaseIdentity>>? releases=null,CancellationToken cancellation=default);
}
public sealed record TitleIndexRecord(ImmutableArray<string> Names,ImmutableArray<string> References);
public sealed record IndexCandidate(int Row,TitleIndexRecord Record,string ObservedTitle,bool Exact);
public interface ITitleIndex
{
    Task<ImmutableArray<IndexCandidate>> FindAsync(ImmutableArray<string> names,CancellationToken cancellation);
    // Exact catalog membership, never a fuzzy title match or an automatic metadata identity.
    Task<bool> ContainsReferenceAsync(string reference, CancellationToken cancellation) => Task.FromResult(false);
    // Bounded near-title retrieval only. These rows are never exact identity/reference proof.
    Task<ImmutableArray<IndexCandidate>> FindSimilarAsync(ImmutableArray<string> names, CancellationToken cancellation) => Task.FromResult(ImmutableArray<IndexCandidate>.Empty);
    Task<ImmutableArray<string>> FindReadmeTitlesAsync(ImmutableArray<string> headings, CancellationToken cancellation) => Task.FromResult(ImmutableArray<string>.Empty);
}
public sealed record ParsedLocalEvidence(ImmutableArray<LocalTitleEvidence> Titles, ImmutableArray<ReferenceEvidence> References,
    ImmutableArray<string> Companies, ImmutableArray<string> Warnings);
public interface ILocalEvidenceParser
{
    ParsedLocalEvidence Parse(string kind, string name, ReadOnlyMemory<byte> bytes, bool truncated);
}
public sealed record LocalEvidenceSnapshot(RecognitionEvidence Evidence, ImmutableArray<PathObservation> Installations,
    ImmutableArray<string> Observations, string Fingerprint)
{
    public bool IncludePe { get; init; }
}
public interface IRecognitionEvidenceReader
{
    Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation);
}
public sealed record ExecutableEvidence(ParsedLocalEvidence Evidence, string Fingerprint, bool RejectExecutable = false)
{
    public string EvidenceDigest { get; init; } = "pe-evidence-v2:missing";
}
public interface IExecutableEvidenceParser
{
    bool IsUsefulExecutable(string name);
    bool IsUsefulTitle(string title);
    ExecutableEvidence Read(Stream stream, string basename, CancellationToken cancellation);
}
public interface IRecognitionCatalog : IOnlineCatalog
{
    Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation);
    Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation);
}
public enum RecognitionJobState { Queued, CollectingEvidence, Matching, Enriching, NeedsReview, Completed, Cancelled, Failed, Stale }
public enum RecognitionMode { MissingMetadata, Reidentify }
public sealed record RecognitionIdentity(RecognitionEvidence Evidence, ImmutableArray<IndexCandidate> LocalCandidates,
    ImmutableArray<string> Queries, ImmutableArray<string> References);
public sealed record RecognitionResult(Guid Generation, RecognitionEntrySnapshot Input, LocalEvidenceSnapshot Local,
    RecognitionIdentity Identity, ImmutableArray<RankedRecognitionCandidate> Candidates, string AlgorithmVersion,
    string SourceFingerprint, bool EnrichmentUnavailable, string? EnrichmentMessage, long? AuthGeneration)
{
    // At most the two enriched works' release responses. Transient competitor context is needed
    // for the automatic commit recheck; restored reviews must be re-identified, never reapplied.
    [JsonIgnore]
    public ImmutableDictionary<string, ImmutableArray<ReleaseIdentity>> ReleaseContext { get; init; } = ImmutableDictionary<string, ImmutableArray<ReleaseIdentity>>.Empty;
    public RecognitionResult WithoutEnrichment() => this with { Candidates = [], AuthGeneration = null,
        ReleaseContext = ImmutableDictionary<string, ImmutableArray<ReleaseIdentity>>.Empty,
        EnrichmentUnavailable = true, EnrichmentMessage = UiText.Get("凭据已变化，在线候选已撤销；本地识别结果保留。") };
}
public interface IRecognitionEngine
{
    Task<RecognitionResult> RecognizeAsync(RecognitionEntrySnapshot input, Guid generation, bool online, bool includePe,
        Action<RecognitionJobState> progress, CancellationToken cancellation);
}
