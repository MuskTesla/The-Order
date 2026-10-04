using System.Collections.Immutable;

namespace TheOrder.Application;

public enum RecognitionReviewStatus { NeedsReview, NeedsRecheck, Stale }
public enum ReviewInitializationState { Uninitialized, Initializing, Ready, Failed }
public sealed record HistoricalRecognitionCandidate(string Id, string Title, string OriginalTitle, double Confidence, string Reason);
public sealed record RecognitionReview(Guid Id, string GameId, string Title, DateTimeOffset CreatedAt,
    RecognitionReviewStatus Status, string AlgorithmVersion, RecognitionResult? Result,
    ImmutableArray<HistoricalRecognitionCandidate> HistoricalCandidates, string? HistoricalFolder = null,
    string? HistoricalFingerprint = null, string? Message = null);
public sealed record RecognitionReviewDocuments(byte[]? Native, byte[]? Legacy);
public sealed class ReviewCapacityException : IOException
{
    public ReviewCapacityException() : base("ReviewCapacity：核对队列容量不足，请先处理有效建议；已有建议保持不变。") { }
}
public interface IRecognitionReviewStorage
{
    Task<RecognitionReviewDocuments> ReadAsync(CancellationToken cancellation);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation);
}
public interface IRecognitionReviewCodec
{
    ImmutableArray<RecognitionReview> Read(RecognitionReviewDocuments documents);
    byte[] Write(ImmutableArray<RecognitionReview> reviews);
    ImmutableArray<RecognitionReview> PrepareAdmission(ImmutableArray<RecognitionReview> reviews);
}
