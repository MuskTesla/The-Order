namespace TheOrder.Application;

// Reuses the established Bangumi transport and access generation. No parallel
// login, fake game identity, disk mirror or recursive volume fetching.
public sealed class BangumiBookRelationsService(IBangumiBookRelationsProvider provider,
    BangumiAccessContext? access = null, Func<DateTimeOffset>? clock = null) : IBangumiBookRelationsReader
{
    private readonly BangumiAccessContext authorization = access ?? new();
    private readonly SemaphoreSlim queue = new(1);
    private readonly Dictionary<string, BangumiRelationsSnapshot> memory = new(StringComparer.Ordinal);
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public async Task<BangumiRelationsLoadResult> LoadAsync(string id, bool refresh, CancellationToken cancellation)
    {
        if (!BangumiRelationIdentityResolver.ValidSubjectId(id)) throw new InvalidDataException("书籍编号无效。");
        var lease = authorization.Capture(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Changed);
        await queue.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var key = lease.Scope + ":book:" + id; var previous = memory.GetValueOrDefault(key);
            if (previous is not null && (previous.FetchedAtUtc > Now.AddMinutes(5) || Now - previous.FetchedAtUtc > TimeSpan.FromDays(7))) previous = null;
            if (!refresh && previous is not null && Now - previous.FetchedAtUtc < TimeSpan.FromHours(1))
            { authorization.RequireCurrent(lease); return new(previous, true, false, null); }
            try
            {
                var result = await provider.FetchBookAsync(id, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested(); authorization.RequireCurrent(lease);
                if (result.SubjectId != id || result.FetchedAtUtc == default || result.Items.IsDefault || result.Items.Length > 256
                    || result.Items.Any(x => !BangumiRelationIdentityResolver.ValidSubjectId(x.Id) || x.Id == id || !Enum.IsDefined(x.Kind)
                        || !AniListValidation.Text(x.Title, 2000, false) || !AniListValidation.Text(x.OriginalTitle, 2000)
                        || !AniListValidation.Text(x.Relation, 200) || x.CoverUrl is not null && !BangumiRelationsService.ValidCoverUrl(x.CoverUrl))
                    || result.Items.Select(x => x.Id).Distinct().Count() != result.Items.Length)
                    throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "书籍关系资料不一致。");
                memory.Remove(key); memory.Add(key, result); while (memory.Count > 32) memory.Remove(memory.Keys.First());
                return new(result, false, false, null);
            }
            catch (BangumiRelationsException e) when (previous is not null && e.Failure is BangumiRelationsFailure.Offline or BangumiRelationsFailure.Timeout or BangumiRelationsFailure.RateLimited or BangumiRelationsFailure.Server)
            { linked.Token.ThrowIfCancellationRequested(); authorization.RequireCurrent(lease); return new(previous, true, true, "显示上次书籍关系资料。"); }
            catch (BangumiRelationsException) { memory.Remove(key); throw; }
        }
        finally { queue.Release(); }
    }
}
