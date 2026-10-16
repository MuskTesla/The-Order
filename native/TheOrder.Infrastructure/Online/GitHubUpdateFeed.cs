using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// Public releases only; this separate transport never receives catalog or GitHub credentials.
public sealed partial class GitHubUpdateFeed : IAppUpdateChannelFeed, IAppUpdateDifferentialFeed, IDisposable
{
    public static readonly Uri LatestUri = new($"https://api.github.com/repos/{AppUpdateIdentity.Repository}/releases/latest");
    private readonly HttpClient client;
    public GitHubUpdateFeed() : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 2,
        ConnectTimeout = TimeSpan.FromSeconds(12), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan }) { }
    // The alternate client is an explicit test dependency, never a configurable update URL.
    public GitHubUpdateFeed(HttpClient client) => this.client = client;
    public async Task<AppUpdateRelease?> LatestAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await GetAsync(LatestUri, "application/vnd.github+json", false, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            var bytes = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = json.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new AppUpdateException("没有可安装的正式更新，请稍后重试。");
            if (!AppUpdateIdentity.TryVersion(root.GetProperty("tag_name").GetString(), out var version))
                throw new AppUpdateException("发行版本信息无法识别，请稍后重试。");
            var tag = root.GetProperty("tag_name").GetString()!;
            var number = version.ToString(3);
            var expectedName = AppUpdateIdentity.InstallerName(number);
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var installers = assets.Where(x => x.GetProperty("name").GetString() == expectedName).ToArray();
            if (installers.Length != 1) throw new AppUpdateException("新版本尚未提供完整的 Windows 安装包，请稍后重新检查。");
            var asset = installers[0];
            if (asset.GetProperty("state").GetString() != "uploaded") throw new AppUpdateException("安装包尚未发布完成，请稍后重试。");
            var download = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            if (download.AbsolutePath != $"/{AppUpdateIdentity.Repository}/releases/download/{tag}/{expectedName}")
                throw new AppUpdateException("安装包来源不正确，未开始下载。");
            string? hash = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                var value = digest.GetString()!;
                if (!value.StartsWith("sha256:", StringComparison.Ordinal) || !AppUpdateIdentity.ValidHash(value[7..]))
                    throw new AppUpdateException("安装包的校验信息无效，未开始下载。");
                hash = value[7..];
            }
            if (hash is null)
            {
                var sums = assets.Where(x => x.GetProperty("name").GetString() == "SHA256SUMS.txt" && x.GetProperty("state").GetString() == "uploaded").ToArray();
                if (sums.Length != 1) throw new AppUpdateException("新版本缺少安装包校验信息，未开始下载。");
                var sumUri = new Uri(sums[0].GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
                if (!AppUpdateIdentity.SafeHttps(sumUri) || sumUri.Host != "github.com" || sumUri.Query.Length != 0
                    || sumUri.AbsolutePath != $"/{AppUpdateIdentity.Repository}/releases/download/{tag}/SHA256SUMS.txt")
                    throw new AppUpdateException("校验文件来源不正确，未开始下载。");
                using var sumResponse = await GetAsync(sumUri, "application/octet-stream", true, timeout.Token).ConfigureAwait(false);
                EnsureSuccess(sumResponse);
                hash = ParseChecksum(Encoding.UTF8.GetString(await ReadBoundedAsync(sumResponse, 65536, timeout.Token).ConfigureAwait(false)), expectedName);
            }
            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            notes = new string(notes.Take(4000).Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            var release = new AppUpdateRelease(number, notes, download, asset.GetProperty("size").GetInt64(), hash,
                OptionalBlockMap(assets, tag, number));
            AppUpdateIdentity.Validate(release);
            return release;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AppUpdateException("检查更新超时，请稍后重试。"); }
        catch (HttpRequestException) { throw new AppUpdateException("暂时无法连接更新服务，请检查网络后重试。"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or FormatException or OverflowException)
        { throw new AppUpdateException("更新服务返回的信息不完整，请稍后重试。"); }
    }
    internal static string ParseChecksum(string text, string name)
    {
        var matches = text.Split('\n').Select(line => line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == name).ToArray();
        if (matches.Length != 1 || !AppUpdateIdentity.ValidHash(matches[0][0])) throw new AppUpdateException("安装包的校验文件不完整，未开始下载。");
        return matches[0][0];
    }
    public async Task CopyInstallerAsync(AppUpdateRelease release, Stream destination, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation)
    {
        AppUpdateIdentity.Validate(release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await GetAsync(release.DownloadUri, "application/octet-stream", true, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is { } length && length != release.Bytes) throw new AppUpdateException("下载文件大小与发行信息不一致，请重新检查更新。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long received = 0; int read; long nextProgress = 0;
            progress.Report(new(0, release.Bytes));
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                received += read;
                if (received > release.Bytes) throw new AppUpdateException("下载文件超过发行声明的大小，已停止下载。");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                if (received >= nextProgress || received == release.Bytes) { progress.Report(new(received, release.Bytes)); nextProgress = received + 512 * 1024; }
            }
            if (received != release.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                throw new AppUpdateException("安装包校验失败，文件未安装。请重新检查更新并下载。");
            await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AppUpdateException("下载更新超时，文件未安装。请稍后重试。"); }
        catch (HttpRequestException) { throw new AppUpdateException("下载中断，文件未安装。请检查网络后重试。"); }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, string accept, bool redirects, CancellationToken cancellation, (long Offset, int Bytes)? range = null)
    {
        for (int count = 0; count <= 5; count++)
        {
            if (!AppUpdateIdentity.SafeHttps(uri) || (count > 0 && !(uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com")))
                throw new AppUpdateException("更新下载重定向到未认可的地址，已停止。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("The-Order-Updater/1.0"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (range is { } part) request.Headers.Range = new RangeHeaderValue(part.Offset, part.Offset + part.Bytes - 1);
            if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (!redirects || location is null || count == 5) throw new AppUpdateException("更新服务返回了无法使用的下载地址，请稍后重试。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new AppUpdateException("更新下载地址无效。");
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new AppUpdateException("更新服务暂时限制了请求，请稍后再检查。");
        if (!response.IsSuccessStatusCode) throw new AppUpdateException("更新服务暂时不可用，请稍后重试。");
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > limit) throw new AppUpdateException("更新信息超过允许的大小，已停止读取。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        { if (output.Length + read > limit) throw new AppUpdateException("更新信息超过允许的大小，已停止读取。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
