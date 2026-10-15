namespace TheOrder.Application;

// Public, read-only subject data. Subject identity is provider + numeric id + media
// type; neither these fields nor the cache participate in library matching or edits.
public sealed record BangumiSubjectDetails(string Id, BangumiRelationKind Kind, string Title,
    string OriginalTitle, string? CoverUrl, string Summary, string? ReleaseDate, string? Platform,
    int? EpisodeCount, double? Score, int? RatingCount, DateTimeOffset FetchedAtUtc)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ChineseTitle { get; init; }
    public string Url => "https://bgm.tv/subject/" + Id;
    public BangumiMusicFacts? MusicFacts { get; init; }
    public BangumiBookFacts? BookFacts { get; init; }
    public string[] ExternalLinks { get; init; } = [];
    public bool HasAmbiguousExternalIdentity { get; init; }
}

public sealed record BangumiSubjectDetailsResult(BangumiSubjectDetails Details, bool FromCache, bool IsStale, string? Warning);
public interface IBangumiSubjectDetailsProvider
{
    Task<BangumiSubjectDetails> FetchAsync(string subjectId, BangumiRelationKind expectedKind, CancellationToken cancellation);
}
public interface IBangumiSubjectDetailsReader
{
    Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind,
        bool forceRefresh, CancellationToken cancellation);
}

public interface IBangumiSubjectDetailsCache
{
    Task<BangumiSubjectDetails?> ReadAsync(string scope, string subjectId, BangumiRelationKind kind, CancellationToken cancellation);
    Task WriteAsync(string scope, BangumiSubjectDetails value, Func<bool> canCommit, CancellationToken cancellation);
    Task RemoveAsync(string scope, string subjectId, BangumiRelationKind kind, Func<bool> canCommit, CancellationToken cancellation);
}

// Bounded display metadata, scoped to the active Bangumi access lease. The optional
// disposable Profile cache survives restarts; it never contains user decisions or tokens.
public sealed class BangumiSubjectDetailsService(IBangumiSubjectDetailsProvider provider,
    Func<DateTimeOffset>? clock = null, BangumiAccessContext? access = null,
    IBangumiSubjectDetailsCache? cache = null) : IBangumiSubjectDetailsReader
{
    private readonly BangumiAccessContext authorization = access ?? new();
    public const int MaximumEntries = 64, MaximumSummaryLength = 32768;
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1), MaximumStaleAge = TimeSpan.FromDays(7);
    private readonly object sync = new();
    private readonly Dictionary<string, (BangumiSubjectDetails Value, long Access)> saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> latest = new(StringComparer.Ordinal);
    private long sequence;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    public static bool Valid(string subjectId, BangumiRelationKind expectedKind, BangumiSubjectDetails? value)
        => BangumiRelationIdentityResolver.ValidSubjectId(subjectId) && Enum.IsDefined(expectedKind)
        && value is not null && value.Id == subjectId && value.Kind == expectedKind
        && Text(value.Title, 2000, false) && Text(value.OriginalTitle, 2000) && Text(value.Summary, MaximumSummaryLength, multiline: true)
        && (value.ChineseTitle is null || Text(value.ChineseTitle, 2000))
        && (value.CoverUrl is null || BangumiRelationsService.ValidCoverUrl(value.CoverUrl))
        && (value.ReleaseDate is null || DateOnly.TryParseExact(value.ReleaseDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _))
        && (value.Platform is null || Text(value.Platform, 200, false))
        && (value.EpisodeCount is null || value.EpisodeCount is > 0 and <= 1000000)
        && (value.Score is null && value.RatingCount is null || value.Score is >= 0 and <= 10 && value.RatingCount is > 0)
        && (value.MusicFacts is null || expectedKind == BangumiRelationKind.Music && value.MusicFacts.IsValid)
        && (value.BookFacts is null || expectedKind == BangumiRelationKind.Book && value.BookFacts.IsValid)
        && value.ExternalLinks is { Length: <= 16 } && value.ExternalLinks.All(x => AniListReference.TryParse(x, out var reference)
            && (expectedKind == BangumiRelationKind.Anime && reference!.Kind == AniListMediaKind.Anime
                || expectedKind == BangumiRelationKind.Book && reference!.Kind == AniListMediaKind.Manga))
        && value.FetchedAtUtc != default;
    private static bool Text(string? value, int limit, bool empty = true, bool multiline = false)
        => value is not null && value.Length <= limit && (empty || !string.IsNullOrWhiteSpace(value))
        && !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));

    public async Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind,
        bool forceRefresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiRelationIdentityResolver.ValidSubjectId(subjectId) || !Enum.IsDefined(expectedKind))
            throw new InvalidDataException("Bangumi 作品身份无效。");
        var lease = authorization.Capture();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Changed);
        cancellation = linked.Token;
        var key = lease.Scope + ":" + subjectId + ":" + (int)expectedKind;
        BangumiSubjectDetails? previous = null;
        long request;
        lock (sync)
        {
            request = ++sequence;
            if (saved.TryGetValue(key, out var entry))
            {
                if (Valid(subjectId, expectedKind, entry.Value) && entry.Value.FetchedAtUtc <= Now.AddMinutes(5)
                    && Now - entry.Value.FetchedAtUtc <= MaximumStaleAge)
                { previous = entry.Value; saved[key] = (previous, request); }
                else saved.Remove(key);
            }
            if (!forceRefresh && previous is not null && Now - previous.FetchedAtUtc < FreshFor)
            { authorization.RequireCurrent(lease); return new(previous, true, false, null); }
            latest[key] = request;
        }
        try
        {
            if (previous is null && cache is not null)
            {
                BangumiSubjectDetails? persisted = null;
                try { persisted = await cache.ReadAsync(lease.Scope, subjectId, expectedKind, cancellation).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
                cancellation.ThrowIfCancellationRequested(); authorization.RequireCurrent(lease);
                if (Valid(subjectId, expectedKind, persisted) && persisted!.FetchedAtUtc <= Now.AddMinutes(5)
                    && Now - persisted.FetchedAtUtc <= MaximumStaleAge)
                {
                    previous = persisted;
                    lock (sync) if (CanCommit()) Remember(persisted);
                    if (!forceRefresh && Now - persisted.FetchedAtUtc < FreshFor)
                        return new(persisted, true, false, null);
                }
            }
            var value = await provider.FetchAsync(subjectId, expectedKind, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            authorization.RequireCurrent(lease);
            if (!Valid(subjectId, expectedKind, value) || value.FetchedAtUtc > Now.AddMinutes(5))
                throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "Bangumi 返回的作品身份或资料格式不一致。");
            string? warning = null;
            if (cache is not null && CanCommit())
            {
                try { await cache.WriteAsync(lease.Scope, value, CanCommit, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !CanCommit()) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                { warning = "资料已读取，但暂未保存离线缓存。"; }
            }
            cancellation.ThrowIfCancellationRequested(); authorization.RequireCurrent(lease);
            lock (sync) if (CanCommit()) Remember(value);
            return new(value, false, false, warning);
        }
        catch (BangumiRelationsException e) when (e.Failure is BangumiRelationsFailure.Offline or BangumiRelationsFailure.Timeout
            or BangumiRelationsFailure.Server or BangumiRelationsFailure.RateLimited && previous is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            authorization.RequireCurrent(lease);
            // A denial or invalid response must never resurrect a formerly public item.
            return new(previous!, true, Now - previous!.FetchedAtUtc >= FreshFor, "暂时无法更新，显示上次读取的资料。" + e.Message);
        }
        catch (BangumiRelationsException)
        {
            lock (sync)
            {
                if (latest.TryGetValue(key, out var active) && active == request) saved.Remove(key);
            }
            if (cache is not null && CanCommit())
            {
                try { await cache.RemoveAsync(lease.Scope, subjectId, expectedKind, CanCommit, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !CanCommit()) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            throw;
        }
        finally
        {
            lock (sync)
            {
                if (latest.TryGetValue(key, out var active) && active == request) latest.Remove(key);
            }
        }
        bool CanCommit()
        {
            if (cancellation.IsCancellationRequested || !authorization.IsCurrent(lease)) return false;
            lock (sync) return latest.TryGetValue(key, out var active) && active == request;
        }
        void Remember(BangumiSubjectDetails value)
        {
            saved[key] = (value, ++sequence);
            while (saved.Count > MaximumEntries) saved.Remove(saved.MinBy(pair => pair.Value.Access).Key);
        }
    }
}
