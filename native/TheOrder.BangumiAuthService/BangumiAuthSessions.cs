using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.BangumiAuthService;

public sealed class BangumiAuthOptions
{
    private readonly string secret;
    public string AppId { get; }
    public Uri Service { get; }
    public Uri Redirect => new(Service, BangumiOAuthProtocol.Callback);
    public BangumiAuthOptions(string appId, string secret, string service)
    {
        if (appId.Length is < 1 or > 128 || !appId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || secret.Length is < 8 or > 4096 || secret.Any(char.IsControl)
            || !Uri.TryCreate(service, UriKind.Absolute, out var uri) || !BangumiOAuthProtocol.ValidService(uri))
            throw new InvalidOperationException("请先配置 The Order 的 Bangumi AppId、ClientSecret 和 HTTPS ServiceUrl。");
        AppId = appId; this.secret = secret; Service = uri;
    }
    internal string ClientSecret() => secret;
    public override string ToString() => "The Order Bangumi authorization service configuration";
}

public sealed record BangumiServiceResponse(int Status, byte[]? Body = null);

// Pending authorizations are bounded, short-lived and consumed exactly once. No account database.
public sealed class BangumiAuthSessions(BangumiAuthOptions options, HttpClient network, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outbound = new(1);
    private DateTimeOffset nextRequest;
    private bool disposed;
    private ITimer? cleanup;
    internal TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(1);
    private sealed class Session
    {
        public readonly string Id, State, Challenge;
        public readonly DateTimeOffset Expires;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly CancellationToken Stop;
        public Session(string id, string state, string challenge, DateTimeOffset expires)
        { Id = id; State = state; Challenge = challenge; Expires = expires; Stop = Cancellation.Token; }
        public bool Processing;
        public int Status = 202;
        public BangumiCredential? Credential;
    }
    public async Task<BangumiServiceResponse> PostAsync(string path, Stream body, CancellationToken cancellation)
    {
        using var document = await ReadJsonAsync(body, cancellation);
        var root = document.RootElement;
        if (path == BangumiOAuthProtocol.Begin)
        {
            var challenge = Text(root, "challenge");
            if (!ExactFields(root, "challenge") || !BangumiOAuthProtocol.ValidSecret(challenge)) return new(400);
            lock (gate)
            {
                Purge(); if (disposed) return new(503); if (sessions.Count >= 128) return new(429);
                cleanup ??= time.CreateTimer(_ => { lock (gate) { if (!disposed) Purge(); } }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                var value = new Session(BangumiOAuthProtocol.RandomSecret(), BangumiOAuthProtocol.RandomSecret(), challenge, time.GetUtcNow().AddMinutes(3));
                sessions.Add(value.Id, value);
                var url = "https://bgm.tv/oauth/authorize?client_id=" + Uri.EscapeDataString(options.AppId) + "&response_type=code&redirect_uri="
                    + Uri.EscapeDataString(options.Redirect.AbsoluteUri) + "&state=" + value.State;
                return Json(200, new { session_id = value.Id, challenge, authorization_url = url, expires_in = 180 });
            }
        }
        if (path == BangumiOAuthProtocol.Refresh)
        {
            if (!ExactFields(root, "refresh_token")) return new(400);
            var token = Text(root, "refresh_token");
            try { _ = BangumiAuthorization.NormalizeToken(token); }
            catch (BangumiAuthorizationException) { return new(400); }
            return await GrantResponseAsync("refresh_token", token, cancellation);
        }
        if (path is not (BangumiOAuthProtocol.Poll or BangumiOAuthProtocol.Cancel) || !ExactFields(root, "session_id", "verifier")) return new(400);
        var id = Text(root, "session_id"); var verifier = Text(root, "verifier");
        if (!BangumiOAuthProtocol.ValidSecret(id) || !BangumiOAuthProtocol.ValidSecret(verifier)) return new(400);
        lock (gate)
        {
            Purge();
            if (!sessions.TryGetValue(id, out var value) || !BangumiOAuthProtocol.Same(value.Challenge, BangumiOAuthProtocol.Challenge(verifier))) return new(410);
            if (path == BangumiOAuthProtocol.Cancel) { Remove(value); return new(204); }
            if (value.Status == 202) return new(202);
            var credential = value.Credential; var status = value.Status;
            Remove(value);
            return credential is null ? new(status) : Tokens(credential);
        }
    }
    public async Task<int> CallbackAsync(string state, string code, bool denied, CancellationToken cancellation)
    {
        if (!BangumiOAuthProtocol.ValidSecret(state) || denied && code.Length != 0) return 400;
        Session? value;
        lock (gate)
        {
            Purge(); value = sessions.Values.FirstOrDefault(x => BangumiOAuthProtocol.Same(x.State, state));
            if (value is null || value.Processing || value.Status != 202) return 410;
            if (denied) { value.Status = 403; return 200; }
            if (code.Length is < 8 or > 512 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return 400;
            value.Processing = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, value.Stop);
        BangumiCredential? credential = null; var status = 503;
        try { credential = await GrantAsync("authorization_code", code, linked.Token); status = 200; }
        catch (BangumiAuthorizationException error) { status = Status(error); }
        catch (OperationCanceledException) { status = 503; }
        lock (gate)
        {
            Purge();
            if (!sessions.TryGetValue(value.Id, out var current) || !ReferenceEquals(current, value)) return 410;
            value.Credential = credential; value.Status = status;
        }
        return status == 200 ? 200 : status;
    }
    private async Task<BangumiServiceResponse> GrantResponseAsync(string grant, string value, CancellationToken cancellation)
    {
        try { return Tokens(await GrantAsync(grant, value, cancellation)); }
        catch (BangumiAuthorizationException error) { return new(Status(error)); }
    }
    private async Task<BangumiCredential> GrantAsync(string grant, string value, CancellationToken cancellation)
    {
        var parameters = new Dictionary<string, string> { ["grant_type"] = grant, ["client_id"] = options.AppId,
            ["client_secret"] = options.ClientSecret(), ["redirect_uri"] = options.Redirect.AbsoluteUri,
            [grant == "authorization_code" ? "code" : "refresh_token"] = value };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://bgm.tv/oauth/access_token") { Content = new FormUrlEncodedContent(parameters) };
        using var document = await SendAsync(request, cancellation);
        var token = BangumiAuthorization.NormalizeToken(Text(document.RootElement, "access_token"));
        var refresh = BangumiAuthorization.NormalizeToken(Text(document.RootElement, "refresh_token"));
        if (!document.RootElement.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds is < 60 or > 31536000) throw Invalid();
        // Refresh responses need not contain user_id; verify the new token with the official account endpoint.
        using var me = new HttpRequestMessage(HttpMethod.Get, "https://api.bgm.tv/v0/me");
        me.Headers.Authorization = new("Bearer", token);
        using var account = await SendAsync(me, cancellation);
        if (!account.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0) throw Invalid();
        if (document.RootElement.TryGetProperty("user_id", out var declared) && (declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt64(out var expected) || expected != number)) throw Invalid();
        return BangumiCredential.OAuth(token, refresh, time.GetUtcNow().AddSeconds(seconds), options.Service,
            number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        if (network.DefaultRequestHeaders.Authorization is not null || network.DefaultRequestHeaders.Contains("Cookie")) throw Invalid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await outbound.WaitAsync(timeout.Token);
            try
            {
                var remaining = nextRequest - time.GetUtcNow();
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, timeout.Token);
                nextRequest = time.GetUtcNow() + RequestSpacing;
                request.Headers.UserAgent.ParseAdd("TheOrderAuth/1.1.0 (Ph-Rank; https://github.com/Ph-Rank/The-Order)");
                using var response = await network.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var code = (int)response.StatusCode;
                if (code is 400 or 401 or 403) throw new BangumiAuthorizationException(BangumiAuthorizationFailure.Unauthorized);
                if (code == 429) throw new BangumiAuthorizationException(BangumiAuthorizationFailure.RateLimited);
                if (code >= 500) throw new BangumiAuthorizationException(BangumiAuthorizationFailure.Server);
                if (code != 200 || response.Content.Headers.ContentLength > 16384 || response.Content.Headers.ContentType?.MediaType != "application/json") throw Invalid();
                return await ReadJsonAsync(await response.Content.ReadAsStreamAsync(timeout.Token), timeout.Token);
            }
            finally { outbound.Release(); }
        }
        catch (HttpRequestException) { throw new BangumiAuthorizationException(BangumiAuthorizationFailure.Offline); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new BangumiAuthorizationException(BangumiAuthorizationFailure.Timeout); }
        catch (Exception error) when (error is IOException or JsonException) { throw Invalid(); }
    }
    private BangumiServiceResponse Tokens(BangumiCredential credential) => credential.WithSecrets((token, refresh) => Json(200,
        new { access_token = token, refresh_token = refresh, expires_in = Math.Max(1, (int)(credential.Expires!.Value - time.GetUtcNow()).TotalSeconds), user_id = credential.AccountId }));
    private static async Task<JsonDocument> ReadJsonAsync(Stream stream, CancellationToken cancellation)
    {
        using var body = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 16385 - (int)body.Length)), cancellation);
            if (count == 0) break;
            body.Write(buffer, 0, count); if (body.Length > 16384) throw Invalid();
        }
        var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw Invalid(); }
        return document;
    }
    private static bool ExactFields(JsonElement root, params string[] names) => root.ValueKind == JsonValueKind.Object
        && root.EnumerateObject().Count() == names.Length && root.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 4096 } text ? text : throw Invalid();
    private static BangumiServiceResponse Json(int status, object body) => new(status, JsonSerializer.SerializeToUtf8Bytes(body));
    private static BangumiAuthorizationException Invalid() => new(BangumiAuthorizationFailure.InvalidResponse);
    private static int Status(BangumiAuthorizationException error) => error.Failure switch
    { BangumiAuthorizationFailure.Unauthorized => 403, BangumiAuthorizationFailure.RateLimited => 429, _ => 503 };
    private void Purge() { foreach (var value in sessions.Values.Where(x => x.Expires <= time.GetUtcNow()).ToArray()) Remove(value); }
    private void Remove(Session value) { sessions.Remove(value.Id); value.Credential = null; value.Cancellation.Cancel(); value.Cancellation.Dispose(); }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; cleanup?.Dispose(); foreach (var value in sessions.Values.ToArray()) Remove(value); } }
}
