using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Recognition;

public sealed class RecognitionReviewCodec : IRecognitionReviewCodec
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32, Converters = { new Utf16() } };
    private sealed class Utf16 : System.Text.Json.Serialization.JsonConverter<string>
    {
        private readonly Legacy.LegacyLibraryCodec.Utf16Writer writer = new();
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var value = JsonDocument.ParseValue(ref reader); return Legacy.LegacyJson.String(value.RootElement); }
        public override void Write(Utf8JsonWriter json, string value, JsonSerializerOptions options) => writer.Write(json, value, options);
    }
    private sealed record Envelope(int Version, ImmutableArray<RecognitionReview> Reviews);
    public ImmutableArray<RecognitionReview> Read(RecognitionReviewDocuments documents)
    {
        try
        {
            if (documents.Native is { } bytes)
            {
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("核对队列超过大小限制，原文件已保留。");
                var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new InvalidDataException("核对队列格式无效。");
                if (envelope.Version != 1) throw new InvalidDataException("核对队列版本不受支持。");
                Validate(envelope.Reviews);
                return [.. envelope.Reviews.Select(r => r with { Status = r.Status == RecognitionReviewStatus.Stale ? r.Status : RecognitionReviewStatus.NeedsRecheck,
                    Message = r.AlgorithmVersion == TitleMatching.AlgorithmVersion ? "已恢复历史建议；进程生命周期已改变，请重新识别后决定。" : "识别算法已改变，请重新识别。" })];
            }
            if (documents.Legacy is not { } legacy) return [];
            if (legacy.Length > MaximumBytes) throw new InvalidDataException("旧核对队列超过大小限制，原文件已保留。");
            using var document = JsonDocument.Parse(legacy, new() { MaxDepth = 32 }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("reviews", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000) throw new InvalidDataException("旧核对队列格式无效。");
            var reviews = ImmutableArray.CreateBuilder<RecognitionReview>(); var ids = new HashSet<Guid>();
            foreach (var row in rows.EnumerateArray())
            {
                string gameId = Text(row, "gameId", 300), title = Text(row, "name", 2000), folder = Text(row, "folderPath", 32768), reason = Text(row, "reason", 2000);
                if (gameId.Length == 0 || !row.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() > 5) throw new InvalidDataException("旧核对建议格式无效。");
                var candidates = ImmutableArray.CreateBuilder<HistoricalRecognitionCandidate>();
                foreach (var option in options.EnumerateArray())
                {
                    if (!option.TryGetProperty("work", out var work) || !option.TryGetProperty("confidence", out var score) || !score.TryGetDouble(out var confidence) || !double.IsFinite(confidence) || confidence is < 0 or > 1) throw new InvalidDataException("旧核对候选格式无效。");
                    candidates.Add(new(Text(work, "id", 300), Text(work, "title", 2000), Text(work, "originalTitle", 2000), confidence, Text(option, "reason", 2000)));
                }
                string legacyId = Text(row, "reviewId", 300);
                // Legacy IDs need not be UUIDs. Stable mapping is display identity only, never permission.
                var id = Guid.TryParse(legacyId, out var parsed) ? parsed : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(gameId + "\n" + legacyId + "\n" + reviews.Count))[..16]);
                if (!ids.Add(id)) throw new InvalidDataException("旧核对编号重复。");
                string? oldFingerprint = row.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object ? Text(source, "fingerprint", 128) : null;
                reviews.Add(new(id, gameId, title, DateTimeOffset.UnixEpoch, Text(row, "sourceStatus", 50) == "stale" ? RecognitionReviewStatus.Stale : RecognitionReviewStatus.NeedsRecheck,
                    "electron-legacy", null, candidates.ToImmutable(), folder, oldFingerprint, "旧版建议需要重新核对。" + reason));
            }
            return reviews.ToImmutable();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("核对队列损坏或格式无效，原文件已保留。", e); }
    }
    public byte[] Write(ImmutableArray<RecognitionReview> reviews)
    {
        Validate(reviews);
        // Historical provenance needs identity fields, not a second copy of personal notes, embedded
        // covers or play sessions. These fields never participate in the legacy source fingerprint.
        reviews = [.. reviews.Select(r => r.Result is { } result ? r with { Result = result with {
            Input = result.Input with { Entry = result.Input.Entry with { Notes = "", Categories = [], CustomTags = [], Sessions = [], LocalCover = null, CoverChoice = null } }
        } } : r)];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, reviews), Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("核对队列超过 16 MiB，未覆盖已保存建议。");
        return bytes;
    }
    private static string Text(JsonElement element, string key, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("核对记录应为对象。");
        if (!element.TryGetProperty(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("核对文本格式无效。");
        string text = Legacy.LegacyJson.String(value);
        if (text.Length > maximum || text.Contains('\0')) throw new InvalidDataException("核对文本超过限制。");
        return text;
    }
    private static void Validate(ImmutableArray<RecognitionReview> reviews)
    {
        if (reviews.IsDefault || reviews.Length > 10000 || reviews.Select(r => r?.Id).Distinct().Count() != reviews.Length) throw new InvalidDataException("核对队列超过上限或编号重复。");
        foreach (var review in reviews)
        {
            if (review is null || review.Id == Guid.Empty || review.GameId is not { Length: > 0 and <= 300 } || review.Title is not { Length: <= 2000 }
                || review.AlgorithmVersion is not { Length: > 0 and <= 100 } || !Enum.IsDefined(review.Status) || review.HistoricalCandidates.IsDefault || review.HistoricalCandidates.Length > 5) throw new InvalidDataException("核对建议格式无效。");
            if (review.Result is not { } result) continue;
            if (result.Generation != review.Id || result.Input?.Entry?.Id != review.GameId || result.Input.Lifecycle is null || result.AlgorithmVersion != review.AlgorithmVersion
                || result.SourceFingerprint is not { Length: 64 } || result.Local?.Fingerprint is not { Length: 64 } || result.Candidates.IsDefault || result.Candidates.Length > 5
                || result.Identity is null || result.Identity.LocalCandidates.IsDefault || result.Identity.LocalCandidates.Length > 128) throw new InvalidDataException("核对结果身份不完整。");
            Array(result.Input.Entry.Installations, 100);
            foreach (var installation in result.Input.Entry.Installations)
                if (installation is null || installation.Id is null || installation.Folder is null || installation.Arguments.IsDefault) throw new InvalidDataException("核对安装快照无效。");
            Evidence(result.Local.Evidence); Evidence(result.Identity.Evidence);
            Array(result.Identity.Queries, 6); Array(result.Identity.References, 100);
            Array(result.Local.Installations, 100); Array(result.Local.Observations, 10000);
            foreach (var indexed in result.Identity.LocalCandidates)
            {
                if (indexed?.Record is null || indexed.ObservedTitle is null) throw new InvalidDataException("索引建议无效。");
                Array(indexed.Record.Names, 100); Array(indexed.Record.References, 100);
            }
            foreach (var candidate in result.Candidates)
            {
                if (candidate is null || candidate.Work?.Id is not { Length: > 0 and <= 20 } || !candidate.Work.Id.All(c => c is >= '0' and <= '9') || !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1 || candidate.Vetoed && candidate.HighConfidence)
                    throw new InvalidDataException("核对候选不符合约束。");
                Array(candidate.Conflicts, 100); Array(candidate.MatchedReferences, 100); Array(candidate.Evidence, 1000);
                if (candidate.Work.Title is null || candidate.Work.OriginalTitle is null || candidate.Reason is null || candidate.Evidence.Any(e => e is null || e.Source is null || e.ObservedTitle is null || e.MatchedTitle is null || !double.IsFinite(e.Similarity))) throw new InvalidDataException("候选说明无效。");
                Array(candidate.Work.Aliases, 1000); Array(candidate.Work.References, 1000);
            }
        }
    }
    private static void Array<T>(ImmutableArray<T> array, int maximum)
    { if (array.IsDefault || array.Length > maximum || array.Any(v => v is null)) throw new InvalidDataException("核对数据集合无效或超过限制。"); }
    private static void Evidence(RecognitionEvidence evidence)
    {
        if (evidence is null) throw new InvalidDataException("本地证据缺失。");
        Array(evidence.Titles, 100); Array(evidence.References, 100); Array(evidence.Companies, 100); Array(evidence.Warnings, 1000); Array(evidence.Fingerprints, 1000); Array(evidence.IndexProofs, 100);
        if (evidence.Titles.Any(t => t.Value is null || t.Source is null) || evidence.References.Any(r => r.Value is null || r.Source is null)) throw new InvalidDataException("证据说明无效。");
        foreach (var proof in evidence.IndexProofs) { Array(proof.RecordNames, 100); Array(proof.References, 100); }
    }
}
