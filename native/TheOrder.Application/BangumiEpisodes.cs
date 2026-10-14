namespace TheOrder.Application;

// A public subject-scoped wiki directory. Type and Sort are source facts, not a
// playback position, personal progress, or replacement for the subject eps count.
public sealed record BangumiEpisode(int Id, string SubjectId, int Type, decimal Sort,
    string Title, string OriginalTitle, string? AirDate, string Duration, string Summary);
public sealed record BangumiEpisodePage(string SubjectId, int Offset, int Total,
    BangumiEpisode[] Episodes, DateTimeOffset FetchedAtUtc);
public sealed record BangumiEpisodesResult(BangumiEpisodePage Page, bool FromCache, bool IsStale, string? Warning);
public interface IBangumiEpisodesProvider
{
    Task<BangumiEpisodePage> FetchEpisodesAsync(string subjectId, int offset, CancellationToken cancellation);
}
public interface IBangumiEpisodesReader
{
    Task<BangumiEpisodesResult> LoadAsync(string subjectId, int offset, bool refresh, CancellationToken cancellation);
}

public sealed class BangumiEpisodesService(IBangumiEpisodesProvider provider,
    Func<DateTimeOffset>? clock = null, BangumiAccessContext? access = null) : IBangumiEpisodesReader
{
    public const int PageSize = 50, MaximumOffset = 10000, MaximumTotal = 1000000, MaximumPages = 32;
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1), MaximumStaleAge = TimeSpan.FromDays(7);
    private readonly BangumiAccessContext authorization = access ?? new();
    private readonly object gate = new();
    private readonly Dictionary<string, (BangumiEpisodePage Page, long Access)> saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> latest = new(StringComparer.Ordinal);
    private long sequence;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    public static bool Valid(string id, int offset, BangumiEpisodePage? page) =>
        BangumiRelationIdentityResolver.ValidSubjectId(id) && offset is >= 0 and <= MaximumOffset && offset % PageSize == 0
        && page is not null && page.SubjectId == id && page.Offset == offset && page.Total is >= 0 and <= MaximumTotal
        && page.FetchedAtUtc != default && page.Episodes is { Length: <= PageSize }
        && page.Episodes.Length <= Math.Max(0, page.Total - offset)
        && (page.Total <= offset || page.Episodes.Length > 0)
        && page.Episodes.All(x => x is not null && x.Id > 0 && x.SubjectId == id && x.Type is >= 0 and <= 6
            && x.Sort is >= 0 and <= MaximumTotal && Text(x.Title, 2000) && Text(x.OriginalTitle, 2000)
            && (x.AirDate is null || Text(x.AirDate, 100, false)) && Text(x.Duration, 200) && Text(x.Summary, 32768, multiline: true))
        && page.Episodes.Select(x => x.Id).Distinct().Count() == page.Episodes.Length;
    private static bool Text(string? value, int limit, bool empty = true, bool multiline = false) =>
        value is not null && value.Length <= limit && (empty || !string.IsNullOrWhiteSpace(value))
        && !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));

    public async Task<BangumiEpisodesResult> LoadAsync(string subjectId, int offset, bool refresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiRelationIdentityResolver.ValidSubjectId(subjectId) || offset is < 0 or > MaximumOffset || offset % PageSize != 0)
            throw new InvalidDataException("Bangumi 章节身份或页码无效。");
        var lease = authorization.Capture();
        authorization.RequireCurrent(lease);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Changed);
        var token = operation.Token; var key = lease.Scope + ":" + subjectId + ":" + offset;
        BangumiEpisodePage? previous; long version;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            previous = saved.TryGetValue(key, out var hit) ? hit.Page : null;
            if (previous is not null && (!Valid(subjectId, offset, previous) || previous.FetchedAtUtc > Now.AddMinutes(5)
                || Now - previous.FetchedAtUtc > MaximumStaleAge)) { saved.Remove(key); previous = null; }
            if (!refresh && previous is not null && Now - previous.FetchedAtUtc < FreshFor && authorization.IsCurrent(lease))
            { authorization.RequireCurrent(lease); saved[key] = (previous, ++sequence); return new(previous, true, false, null); }
            latest[key] = version = ++sequence;
        }
        void Require()
        {
            token.ThrowIfCancellationRequested();
            authorization.RequireCurrent(lease);
            lock (gate) if (!authorization.IsCurrent(lease) || !latest.TryGetValue(key, out var current) || current != version)
                throw new OperationCanceledException("章节资料已切换。", token);
        }
        try
        {
            var page = await provider.FetchEpisodesAsync(subjectId, offset, token).ConfigureAwait(false);
            Require();
            if (!Valid(subjectId, offset, page) || page.FetchedAtUtc > Now.AddMinutes(5))
                throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "Bangumi 章节身份或资料格式无效。");
            lock (gate)
            {
                Require(); saved[key] = (page, ++sequence);
                while (saved.Count > MaximumPages) saved.Remove(saved.OrderBy(x => x.Value.Access).First().Key);
            }
            return new(page, false, false, offset == MaximumOffset && offset + page.Episodes.Length < page.Total ? "章节目录较长，本次读取已到页码上限；完整目录可在 Bangumi 查看。" : null);
        }
        catch (BangumiRelationsException e) when (e.Failure is BangumiRelationsFailure.Offline or BangumiRelationsFailure.Timeout
            or BangumiRelationsFailure.RateLimited or BangumiRelationsFailure.Server)
        {
            Require();
            if (previous is null || Now - previous.FetchedAtUtc > MaximumStaleAge) throw;
            return new(previous, true, true, "暂时无法更新，显示之前读取的 Bangumi 章节目录。");
        }
        catch (BangumiRelationsException)
        {
            lock (gate) if (latest.TryGetValue(key, out var current) && current == version) saved.Remove(key);
            throw;
        }
        finally
        {
            lock (gate) if (latest.TryGetValue(key, out var current) && current == version) latest.Remove(key);
        }
    }
}
