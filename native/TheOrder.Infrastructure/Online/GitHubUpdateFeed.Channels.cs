using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class GitHubUpdateFeed
{
    public static readonly Uri ChannelsUri = new($"https://api.github.com/repos/{AppUpdateIdentity.Repository}/releases?per_page=30");
    public async Task<AppUpdateRelease?> LatestAsync(AppUpdateChannel channel, CancellationToken cancellation)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await GetAsync(channel == AppUpdateChannel.Stable ? LatestUri : ChannelsUri,
                "application/vnd.github+json", false, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false), new() { MaxDepth = 24 });
            var roots = channel == AppUpdateChannel.Stable ? new[] { json.RootElement } : json.RootElement.EnumerateArray().Take(30).ToArray();
            var candidates = roots.Where(r => !r.GetProperty("draft").GetBoolean()
                && AppUpdateVersion.TryParse(r.GetProperty("tag_name").GetString(), out var version)
                && r.GetProperty("prerelease").GetBoolean() == (version.Beta is not null)
                && (channel == AppUpdateChannel.Beta || version.Beta is null)
                && r.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "update-channel.json"))
                .OrderByDescending(r => { AppUpdateVersion.TryParse(r.GetProperty("tag_name").GetString(), out var v); return v; }).ToArray();
            if (candidates.Length == 0) return null; // Legacy beta.1 is intentionally ineligible.
            var root = candidates[0]; AppUpdateVersion.TryParse(root.GetProperty("tag_name").GetString(), out var selected);
            if (candidates.Skip(1).Any(r => AppUpdateVersion.TryParse(r.GetProperty("tag_name").GetString(), out var v) && v == selected))
                throw new AppUpdateException("更新版本存在冲突，未选择安装包。");
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var versionText = selected.Text;
            async Task<(JsonElement Asset, Uri Uri, string Hash)> Asset(string name)
            {
                var items = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
                if (items.Length != 1 || items[0].GetProperty("state").GetString() != "uploaded") throw new AppUpdateException("更新附件尚未完整发布。");
                var item = items[0]; var uri = new Uri(item.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
                var digest = item.GetProperty("digest").GetString() ?? "";
                if (!AppUpdateIdentity.ValidAsset(uri, versionText, name) || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                    || !AppUpdateIdentity.ValidHash(digest[7..])) throw new AppUpdateException("更新来源或校验信息无效。");
                await Task.CompletedTask; return (item, uri, digest[7..]);
            }
            var contract = await Asset("update-channel.json");
            if (contract.Asset.GetProperty("size").GetInt64() is < 2 or > 16384) throw new AppUpdateException("通道兼容信息无效。");
            using var manifestResponse = await GetAsync(contract.Uri, "application/octet-stream", true, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(manifestResponse);
            var bytes = await ReadBoundedAsync(manifestResponse, 16384, timeout.Token).ConfigureAwait(false);
            if (bytes.LongLength != contract.Asset.GetProperty("size").GetInt64() || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(contract.Hash)))
                throw new AppUpdateException("通道兼容信息校验失败。");
            using var manifest = JsonDocument.Parse(bytes); var m = manifest.RootElement;
            if (m.GetProperty("schema").GetInt32() != 1 || m.GetProperty("profileContract").GetInt32() != 1
                || m.GetProperty("version").GetString() != versionText
                || m.GetProperty("installationIdentity").GetString() != "TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3")
                throw new AppUpdateException("该版本不支持安全切换通道，未启用安装。");
            var setup = await Asset(AppUpdateIdentity.InstallerName(versionText));
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            notes = new string(notes.Take(4000).Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            var release = new AppUpdateRelease(versionText, notes, setup.Uri, setup.Asset.GetProperty("size").GetInt64(), setup.Hash) { ChannelProtocol = 1 };
            AppUpdateIdentity.Validate(release); return release;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AppUpdateException("检查更新超时，请稍后重试。"); }
        catch (HttpRequestException) { throw new AppUpdateException("暂时无法连接更新服务，请稍后重试。"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or UriFormatException or FormatException or OverflowException)
        { throw new AppUpdateException("更新服务信息不完整，未开始安装。"); }
    }
}
