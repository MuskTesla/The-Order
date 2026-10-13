using System.Security.Cryptography;
using System.Text;

namespace TheOrder.Application;

// Secret values remain callable only by persistence/network code, not serializable properties.
public sealed class BangumiCredential
{
    private readonly string token;
    private readonly string? refresh;
    private BangumiCredential(string token, string? refresh, DateTimeOffset? expires, Uri? service, string? account)
    { this.token = token; this.refresh = refresh; Expires = expires; Service = service; AccountId = account; }
    public bool IsOAuth => refresh is not null;
    public DateTimeOffset? Expires { get; }
    public Uri? Service { get; }
    public string? AccountId { get; }
    public static BangumiCredential Manual(string token) => new(BangumiAuthorization.NormalizeToken(token), null, null, null, null);
    public static BangumiCredential OAuth(string token, string refresh, DateTimeOffset expires, Uri service, string account)
    {
        if (!BangumiOAuthProtocol.ValidService(service) || !BangumiRelationIdentityResolver.ValidSubjectId(account)
            || expires <= DateTimeOffset.UnixEpoch) throw new BangumiAuthorizationException(BangumiAuthorizationFailure.InvalidResponse);
        return new(BangumiAuthorization.NormalizeToken(token), BangumiAuthorization.NormalizeToken(refresh), expires, service, account);
    }
    public T WithSecrets<T>(Func<string, string?, T> action) => action(token, refresh);
    public override string ToString() => IsOAuth ? "Bangumi credential: OAuth" : "Bangumi credential: manual";
}

public interface IBangumiOAuth
{
    bool Configured { get; }
    Uri? Service { get; }
    Task<BangumiCredential> SignInAsync(CancellationToken cancellation);
    Task<BangumiCredential> RefreshAsync(BangumiCredential credential, CancellationToken cancellation);
}
public interface IBangumiOAuthBrowser
{ Task OpenAsync(Uri authorization, CancellationToken cancellation); }

public static class BangumiOAuthProtocol
{
    public const string Begin = "v1/bangumi/login/start";
    public const string Poll = "v1/bangumi/login/poll";
    public const string Cancel = "v1/bangumi/login/cancel";
    public const string Callback = "v1/bangumi/callback";
    public const string Refresh = "v1/bangumi/refresh";
    public static string RandomSecret() => UrlSafe(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => UrlSafe(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static bool ValidSecret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool Same(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    public static bool ValidService(Uri? value) => value is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true }
        && value.UserInfo.Length == 0 && value.Query.Length == 0 && value.Fragment.Length == 0 && value.AbsolutePath == "/"
        && value.HostNameType == UriHostNameType.Dns && !value.IsLoopback && value.AbsoluteUri.Length <= 512;
    private static string UrlSafe(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
