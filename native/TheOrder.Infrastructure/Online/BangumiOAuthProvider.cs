using System.Text;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public static class BangumiOAuthDefaults
{
    // Set only after the project's own HTTPS authorization service is deployed.
    // No borrowed application identity, embedded App Secret, or fictional endpoint.
    public const string ServiceUrl = "";
    public static Uri? Service => Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && BangumiOAuthProtocol.ValidService(uri) ? uri : null;
}

public sealed class BangumiOAuthProvider(HttpClient client, IBangumiOAuthBrowser browser, Uri? service,
    TimeProvider? clock = null, TimeSpan? loginTimeout = null, TimeSpan? pollInterval = null) : IBangumiOAuth
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public bool Configured => BangumiOAuthProtocol.ValidService(service);
    public Uri? Service => Configured ? service : null;
    public async Task<BangumiCredential> SignInAsync(CancellationToken cancellation)
    {
        RequireConfiguration();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(loginTimeout ?? TimeSpan.FromMinutes(3));
        var verifier = BangumiOAuthProtocol.RandomSecret();
        var challenge = BangumiOAuthProtocol.Challenge(verifier);
        string? session = null;
        try
        {
            using var begin = await PostAsync(BangumiOAuthProtocol.Begin, new { challenge }, deadline.Token).ConfigureAwait(false);
            var root = begin!.RootElement;
            session = Text(root, "session_id");
            if (!BangumiOAuthProtocol.ValidSecret(session) || !BangumiOAuthProtocol.Same(Text(root, "challenge"), challenge)
                || !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds) || seconds is < 1 or > 180
                || !Uri.TryCreate(Text(root, "authorization_url"), UriKind.Absolute, out var authorization) || !ValidAuthorization(authorization)) throw Error();
            await browser.OpenAsync(authorization, deadline.Token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 90; attempt++)
            {
                using var result = await PostAsync(BangumiOAuthProtocol.Poll, new { session_id = session, verifier }, deadline.Token, pending: true).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (result is not null) return Credential(result.RootElement);
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), time, deadline.Token).ConfigureAwait(false);
            }
            throw Error(BangumiAuthorizationFailure.Timeout);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw Error(BangumiAuthorizationFailure.Timeout); }
        finally
        {
            if (session is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { using var result = await PostAsync(BangumiOAuthProtocol.Cancel, new { session_id = session, verifier }, cleanup.Token, pending: true).ConfigureAwait(false); }
                catch { /* An expired/consumed session contains no durable login. */ }
            }
        }
    }
    public async Task<BangumiCredential> RefreshAsync(BangumiCredential credential, CancellationToken cancellation)
    {
        RequireConfiguration();
        if (!credential.IsOAuth || credential.Service != Service) throw Error();
        using var document = await credential.WithSecrets((_, refresh) => PostAsync(BangumiOAuthProtocol.Refresh,
            new { refresh_token = refresh }, cancellation)).ConfigureAwait(false);
        var result = Credential(document!.RootElement);
        if (result.AccountId != credential.AccountId) throw Error();
        return result;
    }
    private BangumiCredential Credential(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds is < 60 or > 31536000) throw Error();
        var id = root.TryGetProperty("user_id", out var account) && account.ValueKind == JsonValueKind.Number && account.TryGetInt64(out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : Text(root, "user_id");
        return BangumiCredential.OAuth(Text(root, "access_token"), Text(root, "refresh_token"), time.GetUtcNow().AddSeconds(seconds), Service!, id);
    }
    private bool ValidAuthorization(Uri uri)
    {
        if (!BangumiOAuthBrowserPolicy.ValidOrigin(uri)) return false;
        try
        {
            var fields = uri.Query[1..].Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length == 2 ? Uri.UnescapeDataString(x[1]) : "", StringComparer.Ordinal);
            return fields.Count == 4 && fields["response_type"] == "code" && fields["client_id"] is { Length: >= 1 and <= 128 } clientId
                && clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && BangumiOAuthProtocol.ValidSecret(fields["state"])
                && fields["redirect_uri"] == new Uri(Service!, BangumiOAuthProtocol.Callback).AbsoluteUri;
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or UriFormatException) { return false; }
    }
    private async Task<JsonDocument?> PostAsync(string path, object payload, CancellationToken cancellation, bool pending = false)
    {
        RequireConfiguration();
        if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie")) throw Error();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Service!, path)) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json"); request.Headers.UserAgent.ParseAdd(BangumiAccountProvider.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var code = (int)response.StatusCode;
            if (pending && code is 202 or 204) return null;
            if (code is 401 or 403) throw Error(path == BangumiOAuthProtocol.Refresh ? BangumiAuthorizationFailure.Unauthorized : BangumiAuthorizationFailure.Denied);
            if (code == 410) throw Error(BangumiAuthorizationFailure.Timeout);
            if (code == 429) throw Error(BangumiAuthorizationFailure.RateLimited);
            if (code >= 500) throw Error(BangumiAuthorizationFailure.Server);
            if (code != 200 || response.Content.Headers.ContentLength > 65536
                || response.Content.Headers.ContentType?.MediaType != "application/json") throw Error();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 65537 - (int)body.Length)), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                body.Write(buffer, 0, count); if (body.Length > 65536) throw Error();
            }
            timeout.Token.ThrowIfCancellationRequested();
            var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw Error(); }
            return document;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw Error(BangumiAuthorizationFailure.Timeout); }
        catch (HttpRequestException) { throw Error(BangumiAuthorizationFailure.Offline); }
        catch (Exception error) when (error is JsonException or IOException) { throw Error(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    private void RequireConfiguration() { if (!Configured) throw Error(BangumiAuthorizationFailure.NotConfigured); }
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 4096 } text ? text : throw Error();
    private static BangumiAuthorizationException Error(BangumiAuthorizationFailure failure = BangumiAuthorizationFailure.InvalidResponse) => new(failure);
}
