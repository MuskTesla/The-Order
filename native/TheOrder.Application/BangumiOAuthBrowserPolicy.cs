namespace TheOrder.Application;

public static class BangumiOAuthBrowserPolicy
{
    public static bool ValidOrigin(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true }
        && uri.IdnHost == "bgm.tv" && uri.AbsolutePath == "/oauth/authorize" && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.Query.Length is > 0 and <= 2048;
}
