using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// Dedicated official-account transport. No cookies, redirects, API keys or arbitrary endpoints.
public sealed class NextMoeOAuth(HttpClient client, TimeProvider? clock = null) : INextMoeOAuth
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 2 }) { Timeout = Timeout.InfiniteTimeSpan };
    public Task<OAuthTokens> ExchangeAsync(string clientId, OAuthAuthorizationCode code, CancellationToken cancellation)
    {
        if (code.Redirect.Scheme != "http" || code.Redirect.Host != "127.0.0.1" || code.Redirect.AbsolutePath != "/callback"
            || code.Redirect.Query.Length != 0 || code.Redirect.Fragment.Length != 0 || code.Redirect.UserInfo.Length != 0) throw new OnlineException(OnlineFailure.InvalidRequest);
        return Request(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
            ["code"] = code.Code, ["code_verifier"] = code.Verifier, ["redirect_uri"] = code.Redirect.AbsoluteUri }, cancellation);
    }
    public Task<OAuthTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellation)
        => Request(new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken }, cancellation);
    private async Task<OAuthTokens> Request(Dictionary<string, string> form, CancellationToken cancellation)
    {
        if (form.Any(x => x.Value.Length is < 1 or > 16384 || x.Value.Any(char.IsControl))) throw new OnlineException(OnlineFailure.InvalidRequest);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://account.nextmoe.com/api/v1/oauth/token") { Content = new FormUrlEncodedContent(form) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new OnlineException(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                ? OnlineFailure.LoginRequired : (int)response.StatusCode is >= 300 and < 400 ? OnlineFailure.RedirectBlocked : OnlineFailure.Server);
            using var body = JsonDocument.Parse(await CatalogTransport.ReadBoundedAsync(response.Content, 65536, timeout.Token).ConfigureAwait(false));
            var root = body.RootElement;
            var access = Secret(root, "access_token"); var refresh = Secret(root, "refresh_token");
            if (!root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds is < 1 or > 86400) throw new OnlineException(OnlineFailure.InvalidResponse);
            var scopes = Text(root, "scope", 2048).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (NextMoeOAuthIdentity.CatalogScopes.Except(scopes, StringComparer.Ordinal).Any()) throw new OnlineException(OnlineFailure.ScopeRequired);
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://account.nextmoe.com/api/v1/oauth/userinfo");
            profileRequest.Headers.Authorization = new("Bearer", access);
            using var profileResponse = await client.SendAsync(profileRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!profileResponse.IsSuccessStatusCode) throw new OnlineException((int)profileResponse.StatusCode is >= 300 and < 400 ? OnlineFailure.RedirectBlocked : OnlineFailure.LoginRequired);
            using var profile = JsonDocument.Parse(await CatalogTransport.ReadBoundedAsync(profileResponse.Content, 65536, timeout.Token).ConfigureAwait(false));
            var subject = Text(profile.RootElement, "sub", 256); var name = Text(profile.RootElement, "name", 256, false);
            return new(access, refresh, (clock ?? TimeProvider.System).GetUtcNow().AddSeconds(seconds), scopes, new(subject, name.Length == 0 ? "NextMoe 用户" : name));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new OnlineException(OnlineFailure.Timeout); }
        catch (HttpRequestException) { throw new OnlineException(OnlineFailure.Offline); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { throw new OnlineException(OnlineFailure.InvalidResponse); }
    }
    private static string Secret(JsonElement root, string name)
    { var value = Text(root, name, 16384); if (value.Any(c => c < 0x21 || c > 0x7e)) throw new OnlineException(OnlineFailure.InvalidResponse); return value; }
    private static string Text(JsonElement root, string name, int maximum, bool required = true)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
        { if (!required) return ""; throw new OnlineException(OnlineFailure.InvalidResponse); }
        var value = field.GetString()!;
        if (value.Length > maximum || required && value.Length == 0 || value.Any(char.IsControl)) throw new OnlineException(OnlineFailure.InvalidResponse);
        return value;
    }
}
public sealed class NextMoeAccountNetwork : IDisposable
{
    public HttpClient Client { get; } = NextMoeOAuth.CreateClient();
    public void Dispose() => Client.Dispose();
}
