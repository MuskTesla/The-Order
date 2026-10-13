using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class GitHubUpdateFeed
{
    private static AppUpdateBlockMapAsset? OptionalBlockMap(JsonElement[] assets, string tag, string version)
    {
        // Incomplete optional metadata never blocks a valid full installer or triggers a download during checks.
        try
        {
            var name = AppUpdateIdentity.BlockMapName(version);
            var maps = assets.Where(x => x.GetProperty("name").GetString() == name).ToArray();
            if (maps.Length != 1) return null;
            var map = maps[0];
            if (map.GetProperty("state").GetString() != "uploaded"
                || !map.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String) return null;
            var value = digest.GetString()!;
            if (!value.StartsWith("sha256:", StringComparison.Ordinal) || !AppUpdateIdentity.ValidHash(value[7..])) return null;
            var uri = new Uri(map.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            var bytes = map.GetProperty("size").GetInt64();
            if (!AppUpdateIdentity.ValidAsset(uri, version, name) || uri.AbsolutePath != $"/{AppUpdateIdentity.Repository}/releases/download/{tag}/{name}"
                || bytes is <= 0 or > AppUpdateIdentity.MaximumBlockMapBytes) return null;
            return new(uri, bytes, value[7..]);
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or UriFormatException or FormatException or OverflowException) { return null; }
    }
    public async Task<AppUpdateBlockMap> ReadBlockMapAsync(AppUpdateRelease release, CancellationToken cancellation)
    {
        AppUpdateIdentity.Validate(release);
        if (!AppUpdateIdentity.ValidBlockMap(release)) throw new AppUpdateException("这个发行尚未提供差量更新，将下载完整包。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var map = release.BlockMap!;
            using var response = await GetAsync(map.DownloadUri, "application/json", true, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            var data = await ReadBoundedAsync(response, AppUpdateIdentity.MaximumBlockMapBytes, timeout.Token).ConfigureAwait(false);
            if (data.LongLength != map.Bytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), Convert.FromHexString(map.Sha256)))
                throw new AppUpdateException("差量更新信息校验失败，将下载完整包。");
            return AppUpdateBlocks.Parse(data, release);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AppUpdateException("差量更新信息读取超时，将下载完整包。"); }
        catch (HttpRequestException) { throw new AppUpdateException("差量更新信息暂时不可用，将下载完整包。"); }
    }
    public async Task CopyRangeAsync(AppUpdateRelease release, long offset, int bytes, Stream destination, CancellationToken cancellation)
    {
        AppUpdateIdentity.Validate(release);
        if (offset < 0 || bytes is <= 0 or > AppUpdateBlocks.MaximumRange || offset > release.Bytes - bytes)
            throw new AppUpdateException("差量更新范围无效，将下载完整包。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await GetAsync(release.DownloadUri, "application/octet-stream", true, timeout.Token, (offset, bytes)).ConfigureAwait(false);
            EnsureSuccess(response);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes"
                || range.From != offset || range.To != offset + bytes - 1 || range.Length != release.Bytes
                || response.Content.Headers.ContentLength is { } length && length != bytes)
                throw new AppUpdateException("下载服务不支持本次差量请求，将下载完整包。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[65536]; int received = 0, read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                received += read; if (received > bytes) throw new AppUpdateException("差量下载范围不一致，将下载完整包。");
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            if (received != bytes) throw new AppUpdateException("差量下载不完整，将下载完整包。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AppUpdateException("差量下载超时，将下载完整包。"); }
        catch (HttpRequestException) { throw new AppUpdateException("差量下载中断，将下载完整包。"); }
    }
}
