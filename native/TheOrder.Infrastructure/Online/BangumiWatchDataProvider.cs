using System.Net;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// Published JSON only. GitHub commits pin metadata and data to the same revision;
// no upstream scripts, npm runtime, platform API or account is used by the app.
public sealed class BangumiWatchDataProvider(HttpClient client) : IBangumiWatchDataProvider
{
    private sealed record Asset(string Repository, string Revision, DateTimeOffset UpdatedAtUtc, string Version, string Sha256, string File);
    public Task<BangumiWatchSnapshot> BuiltInAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var assembly = typeof(BangumiWatchDataProvider).Assembly;
        using var manifest = assembly.GetManifestResourceStream("TheOrder.Infrastructure.WatchData.snapshot-manifest.json") ?? throw new InvalidDataException("缺少放送数据快照。");
        var assets = JsonSerializer.Deserialize<Asset[]>(manifest) ?? throw new InvalidDataException();
        var values = assets.Select(a =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var file = assembly.GetManifestResourceStream("TheOrder.Infrastructure.WatchData." + a.File) ?? throw new InvalidDataException();
            using var bytes = new MemoryStream(); file.CopyTo(bytes);
            var data = new BangumiWatchDataset(a.Repository, a.Revision, a.UpdatedAtUtc, a.Version, a.Sha256, bytes.ToArray()); data.Decode(); return data;
        }).ToArray();
        return new BangumiWatchSnapshot(values.Single(x => x.Repository == "bangumi-data/bangumi-data"), values.SingleOrDefault(x => x.Repository == "wan0ge/bangumi-data"), DateTimeOffset.MinValue);
    }, cancellation);
    public async Task<BangumiWatchSnapshot> UpdateAsync(BangumiWatchSnapshot previous, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var primary = await UpdateOneAsync(previous.Primary, deadline.Token).ConfigureAwait(false); var extra = previous.Supplement; string? warning = null;
        if (extra is not null)
        {
            try
            {
                var candidate = await UpdateOneAsync(extra, deadline.Token).ConfigureAwait(false);
                _ = new BangumiWatchDataIndex(new(primary, candidate, DateTimeOffset.UtcNow)); extra = candidate;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException)
            { warning = "补充的播放页编号暂未更新，继续使用已验证的旧数据。"; }
        }
        return new(primary, extra, DateTimeOffset.UtcNow, warning);
    }
    private async Task<BangumiWatchDataset> UpdateOneAsync(BangumiWatchDataset old, CancellationToken cancellation)
    {
        if (old.Repository is not ("bangumi-data/bangumi-data" or "wan0ge/bangumi-data")) throw new InvalidDataException();
        var metadata = await ReadAsync(new Uri("https://api.github.com/repos/" + old.Repository + "/commits?sha=master&per_page=1"), 64 * 1024, cancellation).ConfigureAwait(false);
        string? sha; DateTimeOffset date;
        try
        {
            using var document = JsonDocument.Parse(metadata); var revision = document.RootElement[0]; sha = revision.GetProperty("sha").GetString();
            date = revision.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException) { throw new InvalidDataException("放送数据版本无效。", e); }
        if (sha is not { Length: 40 } || !sha.All(char.IsAsciiHexDigit) || date > DateTimeOffset.UtcNow.AddMinutes(10)) throw new InvalidDataException("放送数据版本无效。");
        if (sha == old.Revision || date <= old.UpdatedAtUtc) return old;
        var raw = await ReadAsync(new Uri("https://raw.githubusercontent.com/" + old.Repository + "/" + sha + "/dist/data.json"), 12 * 1024 * 1024, cancellation).ConfigureAwait(false);
        return BangumiWatchDataset.Create(old.Repository, sha, date, "Git 快照", raw);
    }
    private async Task<byte[]> ReadAsync(Uri uri, int limit, CancellationToken cancellation)
    {
        if (uri.Scheme != "https" || uri.Host is not ("api.github.com" or "raw.githubusercontent.com") || uri.UserInfo.Length > 0) throw new InvalidDataException();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TheOrder/1.1.0 (+https://github.com/bangumi-data/bangumi-data)");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        // No automatic retry: the shared service persists a weekly attempt, so a
        // GitHub quota or outage cannot create a request storm.
        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden) throw new HttpRequestException("放送数据更新受限。");
        if ((int)response.StatusCode is < 200 or >= 300 || response.Content.Headers.ContentLength > limit) throw new HttpRequestException("放送数据暂不可用。");
        using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[64 * 1024]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        { if (output.Length + read > limit) throw new InvalidDataException("放送数据超过大小限制。"); await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false); }
        return output.ToArray();
    }
}
