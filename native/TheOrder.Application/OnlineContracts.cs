using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public enum OnlineFailure { NoCredential, InvalidCredential, Unauthorized, Forbidden, NotFound, RateLimited, Server, Offline, Timeout, InvalidResponse, ResponseTooLarge, InvalidRequest, RedirectBlocked, MergeLoop, MergeLimit, CredentialStorage, CredentialStorageCorrupt }
public sealed class OnlineException(OnlineFailure failure, TimeSpan? retryAfter = null) : Exception(MessageFor(failure))
{
    public OnlineFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public static string MessageFor(OnlineFailure failure) => failure switch
    {
        OnlineFailure.NoCredential => "请在设置中保存隔离开发用 API 密钥。",
        OnlineFailure.InvalidCredential => "API 密钥格式不正确或已失效，请重新填写。",
        OnlineFailure.Unauthorized => "API 密钥已过期或未获授权，请重新填写。",
        OnlineFailure.Forbidden => "API 密钥没有访问此内容的权限。",
        OnlineFailure.NotFound => "未找到作品，或当前设置不允许查看此内容。",
        OnlineFailure.RateLimited => "请求暂时受限，请稍后重试。",
        OnlineFailure.Server => "在线服务暂时不可用，请稍后重试。",
        OnlineFailure.Offline => "无法连接在线服务。离线书架仍可使用。",
        OnlineFailure.Timeout => "在线请求超时，请重试。",
        OnlineFailure.ResponseTooLarge => "在线响应超过大小限制，已停止读取。",
        OnlineFailure.InvalidRequest => "请求参数无效，请重新搜索。",
        OnlineFailure.RedirectBlocked => "服务返回了不允许的跳转，已停止请求。",
        OnlineFailure.MergeLoop or OnlineFailure.MergeLimit => "作品合并信息异常，已停止跟随，请稍后重试。",
        OnlineFailure.CredentialStorage => "隔离凭据无法读取或保存，请检查目录和 Windows 账户保护。",
        OnlineFailure.CredentialStorageCorrupt => "凭据存储损坏或无法由当前 Windows 账户解密。原文件已保留，请重新保存或明确移除。",
        _ => "服务返回了不完整或无效的数据。"
    };
}
public sealed record OnlinePage(ImmutableArray<WorkMetadata> Items, string? NextCursor);
public interface IOnlineCatalog
{
    Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation);
    Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation);
    Task TestConnectionAsync(CancellationToken cancellation);
    void ClearCache();
}
// Secret retrieval is an infrastructure concern; Presentation receives only ICredentialSettings.
public interface ICredentialSettings
{
    Task<bool> HasCredentialAsync(CancellationToken cancellation);
    Task SaveAsync(string secret, CancellationToken cancellation);
    Task ClearAsync(CancellationToken cancellation);
}
public interface ICredentialProvider { Task<string?> ReadAsync(CancellationToken cancellation); }
public interface IRemoteCoverRegistry
{
    string? Register(string? url, string revision);
}
public static class ImageSourceKeys
{
    public static string? For(LibraryEntry entry) => entry.LocalCover is { } local ? LocalCoverSources.Key(local)
        : Remote(CoverSources.Selected(entry), entry.Metadata?.FetchedAt ?? "");
    public static string? Remote(string? url, string revision) => string.IsNullOrWhiteSpace(url) ? null :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("remote\n" + url + "\n" + revision)));
}
