using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Domain;

namespace TheOrder.Application;

// Derived public display data. These identities and relations never enter library.json,
// recognition evidence, metadata mixing, or automatic association/merge decisions.
public enum BangumiRelationKind { Book = 1, Anime = 2, Music = 3, Game = 4 }
public sealed record BangumiRelationIdentity(string LocalGameId, string Lifecycle, string NextMoeId, string SubjectId, string Fingerprint)
{
    public string AccessScope { get; init; } = "anonymous";
    // Lifecycle guards the current UI request. It is intentionally excluded from the
    // persistent key so a verified source association can use its cache after restart.
    public string CacheKey => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { BangumiRelationsService.SchemaVersion, LocalGameId, NextMoeId, SubjectId, Fingerprint, Access = AccessScope })));
}
public sealed record BangumiRelationItem(string Id, BangumiRelationKind Kind, string Title, string OriginalTitle, string Relation, string? CoverUrl)
{
    public string Url => "https://bgm.tv/subject/" + Id;
}
public sealed record BangumiRelationsSnapshot(string SubjectId, DateTimeOffset FetchedAtUtc, ImmutableArray<BangumiRelationItem> Items)
{
    public bool Truncated { get; init; }
    public int UnsupportedCount { get; init; }
}
public sealed record BangumiRelationsLoadResult(BangumiRelationsSnapshot Snapshot, bool FromCache, bool IsStale, string? Warning);
public interface IBangumiRelationsProvider
{ Task<BangumiRelationsSnapshot> FetchAsync(string subjectId, CancellationToken cancellation); }
public interface IBangumiRelationsCache
{
    Task<BangumiRelationsSnapshot?> ReadAsync(BangumiRelationIdentity identity, CancellationToken cancellation);
    Task WriteAsync(BangumiRelationIdentity identity, BangumiRelationsSnapshot snapshot, CancellationToken cancellation);
}
public interface IBangumiRelationsReader
{ Task<BangumiRelationsLoadResult> LoadAsync(BangumiRelationIdentity identity, bool forceRefresh, CancellationToken cancellation); }

public enum BangumiRelationsFailure { Unavailable, RateLimited, Server, Timeout, InvalidResponse, ResponseTooLarge, Offline, Unauthorized }
public sealed class BangumiRelationsException(BangumiRelationsFailure failure, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public BangumiRelationsFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class BangumiRelationIdentityResolver
{
    public static bool ValidSubjectId(string? id) => id is { Length: > 0 and <= 10 } && id[0] is >= '1' and <= '9' && id.All(char.IsAsciiDigit);
    public static bool TryReferenceId(string? input, out string id)
    {
        id = "";
        if (input is null || input.Length > 500 || input != input.Trim()) return false;
        if (ValidSubjectId(input)) { id = input; return true; }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.IdnHost is not ("bgm.tv" or "bangumi.tv" or "chii.in")
            || !Regex.IsMatch(input, "^https://(?:bgm\\.tv|bangumi\\.tv|chii\\.in)(?::443)?/subject/[1-9][0-9]{0,9}/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        var match = Regex.Match(uri.AbsolutePath, "^/subject/([1-9][0-9]{0,9})/?$", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        id = match.Groups[1].Value; return true;
    }
    public static bool TryCreate(LibraryEntry entry, string lifecycle, out BangumiRelationIdentity? identity, out string reason)
    {
        // Compose adds manually selected source layers to Metadata.References. Only
        // the NextMoe base is an authority for this feature, including Base == null.
        var work = entry.MetadataMix is { } mix ? mix.Base : entry.Metadata;
        return TryCreateWork(work, entry.Id, lifecycle, out identity, out reason);
    }
    // A catalog page has no local installation or shelf entry. It uses the same
    // exact reference checks, with a distinct cache namespace and page lifetime.
    public static bool TryCreateCatalog(WorkMetadata? work, string lifecycle, out BangumiRelationIdentity? identity, out string reason)
        => TryCreateWork(work, "catalog:" + work?.Id, lifecycle, out identity, out reason);

    private static bool TryCreateWork(WorkMetadata? work, string owner, string lifecycle, out BangumiRelationIdentity? identity, out string reason)
    {
        identity = null;
        reason = "暂无可靠的 Bangumi 编号，仅展示 KunGal 关联。";
        if (owner is not { Length: > 0 and <= 200 } || lifecycle is not { Length: > 0 and <= 200 }) return false;
        if (work is null || work.Id is null || !MetadataMixing.IsNextMoeWorkId(work.Id) || work.References.IsDefault
            || work.References.Length > 1000 || work.References.Any(r => r is null)) return false;
        var refs = work.References.Where(r => string.Equals(r.Source, "bangumi", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (refs.Length == 0) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            if (!TryReferenceId(reference.Id, out var id) || reference.Url is { Length: > 0 } url
                && (!TryReferenceId(url, out var linked) || linked != id))
            { reason = "Bangumi 来源编号或链接不一致，请先核对作品关联资料。"; return false; }
            ids.Add(id);
        }
        if (ids.Count != 1) { reason = "当前资料存在多个不同的 Bangumi 编号，请先核对，暂不查询关联。"; return false; }
        var subjectId = ids.Single();
        var references = refs.Select(r => new { Source = r.Source.ToLowerInvariant(), r.Id, r.Url })
            .Distinct().OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Url, StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { work.Id, References = references })));
        identity = new(owner, lifecycle, work.Id, subjectId, fingerprint); reason = ""; return true;
    }
}

public sealed class BangumiRelationsService(IBangumiRelationsProvider provider, IBangumiRelationsCache cache,
    Func<DateTimeOffset>? clock = null, BangumiAccessContext? access = null) : IBangumiRelationsReader
{
    private readonly BangumiAccessContext authorization = access ?? new();
    public const int SchemaVersion = 1, MaxItems = 256;
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, long> latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writes = new(1);
    private long sequence;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public static bool ValidIdentity(BangumiRelationIdentity identity) => identity is not null
        && identity.LocalGameId is { Length: > 0 and <= 200 } && identity.Lifecycle is { Length: > 0 and <= 200 }
        && identity.NextMoeId is not null && MetadataMixing.IsNextMoeWorkId(identity.NextMoeId) && BangumiRelationIdentityResolver.ValidSubjectId(identity.SubjectId)
        && identity.Fingerprint is { Length: 64 } && identity.Fingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')
        && BangumiAccessContext.ValidScope(identity.AccessScope);
    public static bool Valid(BangumiRelationIdentity identity, BangumiRelationsSnapshot? snapshot)
    {
        if (!ValidIdentity(identity) || snapshot is null || snapshot.SubjectId != identity.SubjectId || snapshot.Items.IsDefault
            || snapshot.Items.Length > MaxItems || snapshot.UnsupportedCount is < 0 or > 1000 || snapshot.FetchedAtUtc == default) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return snapshot.Items.All(item => item is not null && BangumiRelationIdentityResolver.ValidSubjectId(item.Id)
            && item.Id != snapshot.SubjectId && seen.Add(item.Id) && Enum.IsDefined(item.Kind)
            && Text(item.Title, 2000, false) && Text(item.OriginalTitle, 2000) && Text(item.Relation, 200)
            && (item.CoverUrl is null || ValidCoverUrl(item.CoverUrl)));
    }
    public static bool ValidCoverUrl(string? value) => value is { Length: > 0 and <= 2048 }
        && !value.Any(char.IsControl) && !value.Contains('%') && !value.Contains('\\') && !value.Contains("..", StringComparison.Ordinal)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.IdnHost == "lain.bgm.tv"
        && (uri.AbsolutePath.StartsWith("/pic/cover/", StringComparison.Ordinal) || Regex.IsMatch(uri.AbsolutePath, "^/r/[1-9][0-9]{0,4}/pic/cover/", RegexOptions.CultureInvariant)
            || uri.AbsolutePath == "/img/no_icon_subject.png");
    private static bool Text(string? value, int maximum, bool empty = true) => value is not null && value.Length <= maximum
        && (empty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
    public async Task<BangumiRelationsLoadResult> LoadAsync(BangumiRelationIdentity identity, bool forceRefresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!ValidIdentity(identity)) throw new InvalidDataException("关联查询身份无效。");
        var lease = authorization.Capture();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Changed);
        cancellation = linked.Token;
        identity = identity with { AccessScope = lease.Scope };
        BangumiRelationsSnapshot? saved = null;
        try { saved = await cache.ReadAsync(identity, cancellation).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        cancellation.ThrowIfCancellationRequested();
        authorization.RequireCurrent(lease);
        if (!Valid(identity, saved) || saved!.FetchedAtUtc > Now.AddMinutes(5) || Now - saved.FetchedAtUtc > TimeSpan.FromDays(7)) saved = null;
        bool Stale(BangumiRelationsSnapshot snapshot) => Now - snapshot.FetchedAtUtc >= FreshFor;
        if (!forceRefresh && saved is not null && !Stale(saved)) return new(saved, true, false, null);
        var request = Interlocked.Increment(ref sequence); var key = identity.CacheKey; latest[key] = request;
        try
        {
            BangumiRelationsSnapshot fetched;
            try
            {
                fetched = await provider.FetchAsync(identity.SubjectId, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                authorization.RequireCurrent(lease);
                if (!Valid(identity, fetched) || fetched.FetchedAtUtc > Now.AddMinutes(5))
                    throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "Bangumi 返回的关联资料格式或来源不一致，请稍后重试。");
            }
            catch (BangumiRelationsException e) when (saved is not null && e.Failure is BangumiRelationsFailure.Offline
                or BangumiRelationsFailure.Timeout or BangumiRelationsFailure.Server or BangumiRelationsFailure.RateLimited)
            { cancellation.ThrowIfCancellationRequested(); return new(saved, true, Stale(saved), "刷新未完成，保留上次缓存。" + e.Message); }
            string? warning = null;
            await writes.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                authorization.RequireCurrent(lease);
                if (latest.TryGetValue(key, out var active) && active == request)
                {
                    try { await cache.WriteAsync(identity, fetched, cancellation).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                    { warning = "关联资料已读取，但本地缓存暂时无法保存。"; }
                }
            }
            finally { writes.Release(); }
            cancellation.ThrowIfCancellationRequested();
            authorization.RequireCurrent(lease);
            return new(fetched, false, false, warning);
        }
        finally { ((ICollection<KeyValuePair<string, long>>)latest).Remove(new(key, request)); }
    }
}
