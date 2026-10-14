using System.Security.Cryptography;
using System.Text;

namespace TheOrder.Application;

public sealed class AniListService(IAniListProvider provider, IAniListCache? disk = null,
    Func<DateTimeOffset>? clock = null) : IAniListReader
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24), SearchFreshFor = TimeSpan.FromHours(6), MaximumStaleAge = TimeSpan.FromDays(14);
    private readonly object gate = new(); private readonly SemaphoreSlim commits = new(1);
    private readonly Dictionary<string, AniListCacheValue> memory = new();
    private readonly Dictionary<string, long> latest = new(); private long sequence;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public Task<AniListMatch> MatchAsync(AniListSource source, string? query, bool refresh, CancellationToken cancellation)
        => MatchCoreAsync(source, query, refresh, true, cancellation);
    public Task<AniListMatch> FindCandidatesAsync(AniListSource source, bool refresh, CancellationToken cancellation)
        => MatchCoreAsync(source, null, refresh, false, cancellation);
    private async Task<AniListMatch> MatchCoreAsync(AniListSource source, string? query, bool refresh, bool allowBinding, CancellationToken cancellation)
    {
        if (!source.IsValid) throw new InvalidDataException("Bangumi 作品身份无效。");
        if (allowBinding && query is null && await ReadAsync("binding:" + source.Fingerprint, cancellation).ConfigureAwait(false) is { Binding: { } binding }
            && binding.SourceFingerprint == source.Fingerprint && binding.Kind == source.Kind) return new([], binding);
        if (allowBinding && query is null && source.ExternalLinks.Length > 0)
        {
            var references = source.ExternalLinks.Select(x => { AniListReference.TryParse(x, out var r); return r!; }).Distinct().ToArray();
            var values = new List<AniListMedia>();
            foreach (var reference in references.Take(2)) values.Add(await provider.ResolveAsync(reference, cancellation).ConfigureAwait(false));
            if (values.Select(x => x.Id).Distinct().Count() == 1 && !AniListMatching.Assess(source, values[0]).Conflict)
                return new(values.ToArray(), new(source.Fingerprint, values[0].Id, source.Kind, "Bangumi 明确外部编号"));
            return new(values.DistinctBy(x => x.Id).ToArray(), null, "外部编号证据存在差异，请核对候选。");
        }
        var queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query ?? "")))[..16];
        var key = "search:" + source.Fingerprint + ":" + queryHash;
        var result = await LoadCacheAsync(key, refresh, SearchFreshFor, async token => new(key, Now,
            Search: await provider.SearchAsync(source, query, token).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
        if (allowBinding && query is null && !result.Stale && AniListMatching.AutomaticCandidate(source, result.Value.Search!) is { } winner)
        {
            try
            {
                var details = await LoadAsync(winner.Id, winner.Kind, false, 1, cancellation).ConfigureAwait(false);
                if (!details.IsStale && AniListMatching.StrongEvidence(source, details.Details.Media))
                {
                    var proof = new AniListBinding(source.Fingerprint, winner.Id, source.Kind,
                        "自动关联 · 唯一候选的标题、发行形式及首播日期一致"
                        + (source.Episodes is { } eps && details.Details.Media.Episodes is { } count && eps != count ? "（集数口径相差1集）" : ""));
                    proof = await SaveBindingAsync(proof, cancellation, onlyIfUnbound: true).ConfigureAwait(false);
                    return new(result.Value.Search!, proof, result.Warning);
                }
            }
            catch (Exception e) when (e is AniListException or IOException or UnauthorizedAccessException)
            { return new(result.Value.Search!, null, "暂时无法核实或保存自动关联，请核对候选；Bangumi 资料仍可浏览。"); }
        }
        return new(result.Value.Search!, null, result.Warning);
    }
    public async Task<AniListResult> LoadAsync(int id, AniListMediaKind kind, bool refresh, int peoplePage, CancellationToken cancellation)
    {
        if (id <= 0 || !Enum.IsDefined(kind) || peoplePage is < 1 or > 10) throw new InvalidDataException("AniList 作品身份无效。");
        var key = $"media:{(int)kind}:{id}:{peoplePage}";
        var result = await LoadCacheAsync(key, refresh, FreshFor, async token => new(key, Now,
            Details: await provider.FetchAsync(id, kind, peoplePage, token).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
        var details = result.Value.Details!;
        if (details.Media.Id != id || details.Media.Kind != kind || details.PeoplePage != peoplePage) throw new AniListException(AniListFailure.InvalidResponse, "AniList 缓存身份不一致。");
        return new(details, result.Cached, result.Stale, result.Warning);
    }
    public async Task ConfirmAsync(AniListSource source, AniListDetails details, CancellationToken cancellation)
    {
        if (!source.IsValid || !AniListValidation.Details(details) || AniListMatching.Assess(source, details.Media).Conflict)
            throw new InvalidDataException("作品类型或发行证据不一致，未保存此关联。");
        await SaveBindingAsync(new(source.Fingerprint, details.Media.Id, details.Media.Kind,
            "用户核对确认 · Bangumi → AniList"), cancellation).ConfigureAwait(false);
    }
    private async Task<AniListBinding> SaveBindingAsync(AniListBinding binding, CancellationToken cancellation, bool onlyIfUnbound = false)
    {
        await commits.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (onlyIfUnbound && await ReadAsync("binding:" + binding.SourceFingerprint, cancellation).ConfigureAwait(false) is { Binding: { } existing }) return existing;
            var value = new AniListCacheValue("binding:" + binding.SourceFingerprint, Now, Binding: binding);
            if (disk is not null) await disk.WriteAsync(value, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); lock (gate) Remember(value);
            return binding;
        }
        finally { commits.Release(); }
    }
    private async Task<(AniListCacheValue Value, bool Cached, bool Stale, string? Warning)> LoadCacheAsync(string key, bool refresh,
        TimeSpan fresh, Func<CancellationToken, Task<AniListCacheValue>> fetch, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var previous = await ReadAsync(key, cancellation).ConfigureAwait(false);
        var needsPlayback = previous?.Details is { Media.Kind: AniListMediaKind.Anime, HasPlaybackMetadata: false };
        if (!refresh && previous is not null && !needsPlayback && Now - previous.CachedAtUtc < fresh) return (previous, true, false, null);
        long version; lock (gate) latest[key] = version = ++sequence;
        try
        {
            var value = await fetch(cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
            if (value.Key != key || !AniListValidation.Cache(value) || value.CachedAtUtc > Now.AddMinutes(5))
                throw new AniListException(AniListFailure.InvalidResponse, "AniList 资料格式不一致。");
            await SaveIfCurrentAsync(key, version, value, cancellation).ConfigureAwait(false); return (value, false, false, null);
        }
        catch (AniListException e) when (previous is not null && e.Failure is AniListFailure.Offline or AniListFailure.Timeout or AniListFailure.Server or AniListFailure.RateLimited)
        {
            cancellation.ThrowIfCancellationRequested(); return (previous, true, needsPlayback || Now - previous.CachedAtUtc >= fresh, "暂时无法更新，显示之前读取的 AniList 资料。");
        }
        catch (AniListException)
        {
            await SaveIfCurrentAsync(key, version, new(key, Now, Rejected: true), cancellation).ConfigureAwait(false); throw;
        }
        finally { lock (gate) { if (latest.GetValueOrDefault(key) == version) latest.Remove(key); } }
    }
    private async Task SaveIfCurrentAsync(string key, long version, AniListCacheValue value, CancellationToken cancellation)
    {
        await commits.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            bool owns; lock (gate) owns = latest.GetValueOrDefault(key) == version;
            if (!owns) return;
            if (disk is not null) try { await disk.WriteAsync(value, cancellation).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            cancellation.ThrowIfCancellationRequested(); lock (gate) { if (latest.GetValueOrDefault(key) == version) Remember(value); }
        }
        finally { commits.Release(); }
    }
    private async Task<AniListCacheValue?> ReadAsync(string key, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); AniListCacheValue? value; lock (gate) memory.TryGetValue(key, out value);
        if (value is null && disk is not null) try { value = await disk.ReadAsync(key, cancellation).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        cancellation.ThrowIfCancellationRequested();
        if (value?.Key != key || !AniListValidation.Cache(value) || value!.Rejected || value.CachedAtUtc > Now.AddMinutes(5)
            || value.Binding is null && Now - value.CachedAtUtc > MaximumStaleAge) return null;
        lock (gate) Remember(value); return value;
    }
    private void Remember(AniListCacheValue value)
    { memory.Remove(value.Key); memory[value.Key] = value; while (memory.Count > 64) memory.Remove(memory.Keys.First()); }
}
