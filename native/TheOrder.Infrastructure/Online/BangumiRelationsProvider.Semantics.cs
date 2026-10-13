using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class BangumiRelationsProvider
{
    public const int MaximumSemanticSubjects = 8;
    private readonly object semanticGate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, ImmutableArray<RelatedSemanticEvidence> Items)> semanticCache = new(StringComparer.Ordinal);

    // Reuses this provider's anonymous client, pacing, bounded bodies, request queue,
    // cancellation and one whole-operation retry/deadline budget. This is a refinement
    // of existing exact relations, not a subject search or an additional data source.
    public async Task<ImmutableArray<RelatedSemanticEvidence>> LoadAsync(string sourceSubjectId,
        ImmutableArray<BangumiRelationItem> items, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiRelationIdentityResolver.ValidSubjectId(sourceSubjectId) || items.IsDefault
            || items.Length > BangumiRelationsService.MaxItems || items.Any(item => item is null
                || !BangumiRelationIdentityResolver.ValidSubjectId(item.Id))) throw new InvalidDataException("关系来源编号无效。");
        var ids = items.Where(item => item.Kind == BangumiRelationKind.Anime && item.Relation == "动画")
            .Select(item => item.Id).Where(id => id != sourceSubjectId).Distinct(StringComparer.Ordinal)
            .Take(MaximumSemanticSubjects).ToArray();
        if (ids.Length == 0) return [];
        var lease = authorization.Capture();
        var key = lease.Scope + ":" + sourceSubjectId + ":" + string.Join(',', ids.Order(StringComparer.Ordinal));
        lock (semanticGate) if (semanticCache.TryGetValue(key, out var hit) && hit.Until > Now) return hit.Items;
        var duration = operationTimeout ?? TimeSpan.FromSeconds(25);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease.Changed);
        timeout.CancelAfter(duration); var budget = new Budget(Now + duration, timeout.Token, lease); bool entered = false, complete = true;
        var result = ImmutableArray.CreateBuilder<RelatedSemanticEvidence>();
        try
        {
            await queue.WaitAsync(budget.Token).ConfigureAwait(false); entered = true;
            if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie"))
                throw Invalid("匿名资料网络配置无效，未发送请求。");
            foreach (var id in ids)
            {
                try
                {
                    using var body = await GetAsync(id, true, budget).ConfigureAwait(false);
                    // Validate the whole response before using even one exact reverse edge.
                    _ = MapRelations(id, body.RootElement, Now);
                    foreach (var row in body.RootElement.EnumerateArray())
                        if (Id(row) == sourceSubjectId && Integer(row, "type") == (int)BangumiRelationKind.Game)
                            result.Add(new(sourceSubjectId, id, Text(row, "relation", 200)));
                }
                catch (BangumiRelationsException error)
                {
                    complete = false;
                    if (error.Failure is BangumiRelationsFailure.RateLimited or BangumiRelationsFailure.Timeout) break;
                }
            }
            budget.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !lease.Changed.IsCancellationRequested) { complete = false; }
        catch (HttpRequestException) { complete = false; }
        catch (IOException) { complete = false; }
        finally { if (entered) queue.Release(); }
        cancellation.ThrowIfCancellationRequested(); lease.Changed.ThrowIfCancellationRequested();
        var accepted = result.Distinct().ToImmutableArray();
        if (complete)
        {
            lock (semanticGate)
            {
                while (semanticCache.Count >= 32) semanticCache.Remove(semanticCache.OrderBy(pair => pair.Value.Until).First().Key);
                semanticCache[key] = (Now.AddHours(1), accepted);
            }
        }
        return accepted;
    }
}
