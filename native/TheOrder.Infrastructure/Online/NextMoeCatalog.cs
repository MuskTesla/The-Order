using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

public sealed partial class NextMoeCatalog(CatalogTransport transport, bool includeExplicit = true, Func<DateTimeOffset>? clock = null) : IRecognitionCatalog
{
    private readonly object gate = new();
    private sealed record CachedDetail(WorkMetadata Work, DateTimeOffset Until, int Bytes, long AuthGeneration);
    private readonly Dictionary<string, CachedDetail> cache = [];
    private long cacheGeneration;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public void ClearCache() { lock (gate) { cache.Clear(); cacheGeneration++; } }
    private Dictionary<string, string> Parameters() => new() { ["nsfw"] = includeExplicit ? "true" : "false" };
    public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation)
    {
        var q = query.Trim(); if (q.Length is < 1 or > 512 || cursor is not null && (!cursor.StartsWith("cur_", StringComparison.Ordinal) || cursor.Length > 512)) throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation<OnlinePage>(async scope =>
        {
            var p = Parameters(); p["object"] = "work"; p["q"] = q; p["limit"] = "20"; if (cursor is not null) p["cursor"] = cursor;
            using var search = await transport.GetAsync("catalog/search", p, scope).ConfigureAwait(false);
            var root = search.RootElement; RequireList(root, 20);
            var next = Text(root, "next_cursor"); if (next.Length > 0 && (!next.StartsWith("cur_", StringComparison.Ordinal) || next.Length > 512)) throw new OnlineException(OnlineFailure.InvalidResponse);
            var ids = new List<string>();
            foreach (var hit in Rows(root, "items"))
            {
                var id = Text(hit, "id");
                if (Text(hit, "object") != "search_result" || Text(hit, "target_object") != "work" || !ValidId(id)) throw new OnlineException(OnlineFailure.InvalidResponse);
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) return new([], next.Length == 0 ? null : next);
            p = Parameters(); p["ids"] = string.Join(',', ids); p["include"] = "ratings,companies,refs,titles,covers";
            using var batch = await transport.GetAsync("catalog/works", p, scope).ConfigureAwait(false);
            RequireList(batch.RootElement); var works = new Dictionary<string, WorkMetadata>();
            foreach (var dto in Rows(batch.RootElement, "items")) if (ids.Contains(Text(dto, "id"))) { var work = new RemoteWorkDto(dto).Map(includeExplicit, Now); works[work.Id] = work; }
            scope.Checkpoint(OnlineAcceptanceStage.Mapped);
            return new([.. ids.Where(works.ContainsKey).Select(id => works[id])], next.Length == 0 ? null : next);
        }, cancellation);
    }
    public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); if (!ValidId(id)) throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation(async scope =>
        {
            long generation = 0;
            var cached = transport.ReadCurrent<WorkMetadata?>(scope, () =>
            {
                lock (gate)
                {
                    if (!refresh && cache.TryGetValue(id, out var hit) && hit.Until > Now && hit.AuthGeneration == scope.Auth.Generation) return hit.Work;
                    generation = ++cacheGeneration;
                    return null;
                }
            });
            if (cached is not null) return cached;
            var visited = new HashSet<string>(); var current = id;
            for (int merge = 0; merge <= 3; merge++)
            {
                if (!visited.Add(current)) throw new OnlineException(OnlineFailure.MergeLoop);
                try
                {
                    var p = Parameters(); p["include"] = "titles,refs,links,intros,covers,companies,ratings,tags,characters";
                    using var body = await transport.GetAsync("catalog/works/" + current, p, scope).ConfigureAwait(false);
                    var work = new RemoteWorkDto(body.RootElement).Map(includeExplicit, Now);
                    if (!string.Equals(work.Id, current, StringComparison.Ordinal)) throw new OnlineException(OnlineFailure.InvalidResponse);
                    scope.Checkpoint(OnlineAcceptanceStage.Mapped);
                    int bytes = System.Text.Encoding.UTF8.GetByteCount(body.RootElement.GetRawText());
                    scope.DeferCache(() =>
                    {
                        lock (gate)
                        {
                            scope.Check();
                            if (generation == cacheGeneration)
                            {
                                // Simple TTL cache, 32 entries / 8 MiB of source JSON. Search cards never masquerade as full details.
                                cache.Remove(id, out var previous);
                                while (cache.Count >= 32 || cache.Values.Sum(v => (long)v.Bytes) + bytes > CatalogTransport.MaximumBytes)
                                { if (cache.Count == 0) break; cache.Remove(cache.OrderBy(k => k.Value.Until).First().Key); }
                                var accepted = new CachedDetail(work, Now.AddMinutes(5), bytes, scope.Auth.Generation);
                                if (bytes <= CatalogTransport.MaximumBytes) cache[id] = accepted;
                                return () => { lock (gate) { if (cache.TryGetValue(id, out var entry) && ReferenceEquals(entry, accepted)) { cache.Remove(id); if (previous is not null) cache[id] = previous; } } };
                            }
                            return null;
                        }
                    });
                    return work;
                }
                catch (MergedWorkException merged) { current = merged.CanonicalId; }
            }
            throw new OnlineException(OnlineFailure.MergeLimit);
        }, cancellation);
    }
    public Task TestConnectionAsync(CancellationToken cancellation) => transport.Operation(async scope =>
    {
        var p = Parameters(); p["limit"] = "1"; using var body = await transport.GetAsync("catalog/works", p, scope).ConfigureAwait(false); RequireList(body.RootElement); return true;
    }, cancellation);
    private static void RequireList(JsonElement root, int maximum = int.MaxValue)
    { var rows = Field(root, "items"); if (Text(root, "object") != "list" || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > maximum) throw new OnlineException(OnlineFailure.InvalidResponse); }
}
