using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using TheOrder.Application;
using TheOrder.BangumiAuthService;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiQuickLoginTests
{
    private const string Access = "synthetic_access_1234567890123456", Refresh = "synthetic_refresh_1234567890123456";
    private static readonly Uri Service = new("https://auth.example/");
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T10:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class MemoryStore : IBangumiCredentialStore
    {
        public BangumiCredential? Value;
        public Action? AfterCommit;
        public int Writes;
        public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult(Value?.WithSecrets((token, _) => token));
        public Task<BangumiCredential?> ReadCredentialAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
        public Task SaveAsync(string token, CancellationToken cancellation) => SaveCredentialAsync(BangumiCredential.Manual(token), cancellation);
        public Task SaveCredentialAsync(BangumiCredential value, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Value = value; Writes++; AfterCommit?.Invoke(); return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Value = null; return Task.CompletedTask; }
    }
    private sealed class Accounts : IBangumiAccountProvider
    { public string Id = "123"; public Task<BangumiAccount> ValidateAsync(string token, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new BangumiAccount(Id, "synthetic", "合成用户")); } }
    private sealed class OAuth(Clock clock) : IBangumiOAuth
    {
        public bool Configured { get; set; } = true;
        public Uri? Service { get; set; } = BangumiQuickLoginTests.Service;
        public int SignIns, Refreshes;
        public BangumiAuthorizationFailure? Failure;
        public Task<BangumiCredential> SignInAsync(CancellationToken cancellation) { SignIns++; return Make(cancellation); }
        public Task<BangumiCredential> RefreshAsync(BangumiCredential value, CancellationToken cancellation) { Refreshes++; return Make(cancellation); }
        private Task<BangumiCredential> Make(CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (Failure is { } failure) throw new BangumiAuthorizationException(failure); return Task.FromResult(BangumiCredential.OAuth(Access, Refresh, clock.Now.AddHours(1), Service!, "123")); }
    }
    private sealed class Browser(Func<Uri, CancellationToken, Task>? action = null) : IBangumiOAuthBrowser
    { public int Opens; public async Task OpenAsync(Uri uri, CancellationToken cancellation) { Opens++; if (action is not null) await action(uri, cancellation); } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { Calls++; return action(request, cancellation); } }
    private static HttpResponseMessage Json(string value, int code = 200) => new((HttpStatusCode)code) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static string Tokens => JsonSerializer.Serialize(new { access_token = Access, refresh_token = Refresh, expires_in = 3600, user_id = "123" });
    private static (BangumiAuthorization Auth, MemoryStore Store, Accounts Accounts, OAuth OAuth, Clock Clock, BangumiAccessContext Access) Fixture()
    { var clock = new Clock(); var store = new MemoryStore(); var accounts = new Accounts(); var oauth = new OAuth(clock); var access = new BangumiAccessContext(); return (new(store, accounts, access, oauth, clock), store, accounts, oauth, clock, access); }

    [Fact] public async Task UnconfiguredLoginIsHonestAndDoesNotOpenOrRequestAnything()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not request")); var browser = new Browser();
        var oauth = new BangumiOAuthProvider(new HttpClient(handler), browser, null);
        Assert.False(oauth.Configured);
        Assert.Equal(BangumiAuthorizationFailure.NotConfigured, (await Assert.ThrowsAsync<BangumiAuthorizationException>(() => oauth.SignInAsync(default))).Failure);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, browser.Opens);
        var f = Fixture(); f.OAuth.Configured = false; using var model = new BangumiSettingsViewModel(f.Auth);
        await model.InitializeAsync(); Assert.False(model.CanQuickLogin); Assert.True(model.CanOperate); Assert.Contains("尚未开通", model.QuickLoginNotice);
        await model.SaveAsync(Access); Assert.True(model.HasAuthorization);
    }
    [Fact] public async Task QuickLoginValidatesAccountAndCommitsBothSecretsBeforePublishingAccess()
    {
        var f = Fixture(); using var model = new BangumiSettingsViewModel(f.Auth); await model.InitializeAsync();
        Assert.True(model.CanQuickLogin); await model.SignInAsync();
        Assert.Equal(1, f.OAuth.SignIns); Assert.True(f.Store.Value!.IsOAuth); Assert.Equal("123", f.Store.Value.AccountId);
        Assert.True(f.Access.Capture().Authorized); Assert.Contains("快捷登录成功", model.Message);
        Assert.DoesNotContain(Access, JsonSerializer.Serialize(f.Store.Value)); Assert.DoesNotContain(Refresh, JsonSerializer.Serialize(f.Store.Value));
        Assert.DoesNotContain(Access, f.Store.Value.ToString()); Assert.DoesNotContain(Refresh, f.Store.Value.ToString());
    }
    [Theory] [InlineData(BangumiAuthorizationFailure.Denied)] [InlineData(BangumiAuthorizationFailure.Offline)] [InlineData(BangumiAuthorizationFailure.Timeout)]
    public async Task FailedLoginPreservesManualAuthorization(BangumiAuthorizationFailure failure)
    {
        var f = Fixture(); await f.Auth.SaveAsync(Access, default); var old = f.Store.Value; var scope = f.Access.Capture().Scope;
        f.OAuth.Failure = failure;
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.SignInAsync(default));
        Assert.Same(old, f.Store.Value); Assert.Equal(scope, f.Access.Capture().Scope);
    }
    [Fact] public async Task AccountMismatchNeverReplacesCurrentCredential()
    {
        var f = Fixture(); await f.Auth.SaveAsync(Access, default); var old = f.Store.Value; f.Accounts.Id = "999";
        Assert.Equal(BangumiAuthorizationFailure.InvalidResponse, (await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.SignInAsync(default))).Failure);
        Assert.Same(old, f.Store.Value);
    }
    [Fact] public async Task CancellationAfterAtomicCommitStillPublishesTheCommittedOAuthValue()
    {
        var f = Fixture(); using var stop = new CancellationTokenSource(); f.Store.AfterCommit = stop.Cancel;
        await f.Auth.SignInAsync(stop.Token); Assert.True(f.Auth.HasSavedToken); Assert.True(f.Access.Capture().Authorized); Assert.True(f.Store.Value!.IsOAuth);
    }
    [Fact] public async Task RefreshRotatesCredentialsWhileTransientFailureRetainsAStillValidSession()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Clock.Now = f.Clock.Now.AddMinutes(56); f.OAuth.Failure = BangumiAuthorizationFailure.Offline;
        var old = f.Store.Value; await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.RefreshAsync(default));
        Assert.Same(old, f.Store.Value); Assert.True(f.Access.Capture().Authorized);
        f.OAuth.Failure = null; await f.Auth.RefreshAsync(default); Assert.NotSame(old, f.Store.Value); Assert.False(f.Auth.NeedsRefresh);
    }
    [Fact] public async Task ExpiredOrRevokedOAuthNeverKeepsAnAuthorizedSnapshot()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Clock.Now = f.Clock.Now.AddHours(2); f.OAuth.Failure = BangumiAuthorizationFailure.Offline;
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.RefreshAsync(default)); Assert.False(f.Access.Capture().Authorized); Assert.True(f.Access.Rejected);
        var again = Fixture(); again.Store.Value = f.Store.Value; again.OAuth.Configured = false; again.Clock.Now = f.Clock.Now;
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => again.Auth.InitializeAsync(default)); Assert.False(again.Access.Capture().Authorized);
    }
    [Fact] public async Task StoredRefreshTokensCannotBeSentToANewService()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Clock.Now = f.Clock.Now.AddMinutes(56); f.OAuth.Service = new("https://another.example/");
        Assert.Equal(BangumiAuthorizationFailure.NotConfigured, (await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.RefreshAsync(default))).Failure);
        Assert.Equal(0, f.OAuth.Refreshes);
    }
    [Fact] public async Task AccessExpiresBeforeTheNextTimerTickAndRejectsLateReaders()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); var old = f.Access.Capture(); f.Clock.Now += TimeSpan.FromHours(2);
        Assert.False(f.Access.Capture().Authorized); Assert.True(f.Access.Rejected); Assert.True(old.Changed.IsCancellationRequested);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bgm.tv/v0/subjects/13");
        Assert.ThrowsAny<OperationCanceledException>(() => old.Authorize(request)); Assert.Null(request.Headers.Authorization);
    }
    [Fact] public async Task ChoosingManualAuthorizationRemovesOAuthRefreshAndExpiryEvenForTheSameToken()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); await f.Auth.SaveAsync(Access, default);
        f.Clock.Now += TimeSpan.FromHours(2); Assert.False(f.Store.Value!.IsOAuth); Assert.False(f.Auth.NeedsRefresh); Assert.True(f.Access.Capture().Authorized);
    }
    [Fact] public async Task ShutdownDrainsAnOutstandingAccountVerificationWithoutSavingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new WaitingAccounts(entered); var f = Fixture();
        using var model = new BangumiSettingsViewModel(new(f.Store, accounts, f.Access, f.OAuth, f.Clock));
        await model.InitializeAsync(); var signing = model.SignInAsync(); await entered.Task;
        await model.DrainAsync(); await signing; Assert.False(model.Busy); Assert.Null(f.Store.Value); Assert.Equal(0, f.Store.Writes);
    }
    private sealed class WaitingAccounts(TaskCompletionSource entered) : IBangumiAccountProvider
    {
        public async Task<BangumiAccount> ValidateAsync(string token, CancellationToken cancellation)
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); throw new InvalidOperationException(); }
    }
    [Theory] [InlineData("http://auth.example/")] [InlineData("https://auth.example/path")] [InlineData("https://auth.example/?x=1")]
    [InlineData("https://user@auth.example/")] [InlineData("https://127.0.0.1/")] [InlineData("https://localhost/")]
    public void InvalidServiceCannotBecomeAnOAuthDestination(string url)
    { Assert.False(new BangumiOAuthProvider(new HttpClient(), new Browser(), new Uri(url)).Configured); }
    [Fact] public async Task AForeignBrowserDestinationIsRejectedBeforeItOpens()
    {
        var id = BangumiOAuthProtocol.RandomSecret(); var browser = new Browser();
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("cancel")) return new(HttpStatusCode.NoContent);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return Json(JsonSerializer.Serialize(new { session_id = id, challenge = json.RootElement.GetProperty("challenge").GetString(),
                authorization_url = "https://evil.example/oauth/authorize", expires_in = 180 }));
        });
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => new BangumiOAuthProvider(new HttpClient(handler), browser, Service).SignInAsync(default));
        Assert.Equal(0, browser.Opens);
    }
    [Theory] [InlineData(302)] [InlineData(429)] [InlineData(503)] [InlineData(401)]
    public async Task OAuthErrorsDoNotExposeResponseSecrets(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(Access + Refresh, status)));
        var error = await Assert.ThrowsAsync<BangumiAuthorizationException>(() => new BangumiOAuthProvider(new HttpClient(handler), new Browser(), Service).SignInAsync(default));
        Assert.DoesNotContain(Access, error.ToString()); Assert.DoesNotContain(Refresh, error.ToString());
    }
    [Fact] public async Task LateTokenResponseAfterCancelIsIgnored()
    {
        using var stop = new CancellationTokenSource();
        var handler = new Handler((_, _) => { stop.Cancel(); return Task.FromResult(Json(Tokens)); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BangumiOAuthProvider(new HttpClient(handler), new Browser(), Service).RefreshAsync(
            BangumiCredential.OAuth(Access, Refresh, DateTimeOffset.UtcNow.AddHours(1), Service, "123"), stop.Token));
    }
    [Fact] public async Task NewEncryptedStoreReadsLegacyManualFormatAndOAuthSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-quick-login-" + Guid.NewGuid().ToString("N"));
        NativeProfileStore? profile = null;
        try
        {
            profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            var shelf = File.ReadAllBytes(Path.Combine(root, "library.json"));
            var file = Path.Combine(root, WindowsBangumiCredentialStore.FileName);
            var old = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, profile.Manifest.ProfileId, profile.Manifest.StoreId, Token = Access });
            File.WriteAllBytes(file, ProtectedData.Protect(old, null, DataProtectionScope.CurrentUser));
            var store = new WindowsBangumiCredentialStore(profile.Configuration());
            Assert.False((await store.ReadCredentialAsync(default))!.IsOAuth);
            await store.SaveCredentialAsync(BangumiCredential.OAuth(Access, Refresh, DateTimeOffset.UtcNow.AddHours(1), Service, "123"), default);
            Assert.DoesNotContain(Access, Encoding.UTF8.GetString(File.ReadAllBytes(file))); Assert.DoesNotContain(Refresh, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            profile.Dispose(); profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            store = new(profile.Configuration()); var restored = await store.ReadCredentialAsync(default); Assert.True(restored!.IsOAuth); Assert.Equal(Service, restored.Service);
            await store.SaveAsync(Access, default); Assert.False((await store.ReadCredentialAsync(default))!.IsOAuth);
            await store.ClearAsync(default); Assert.Null(await store.ReadCredentialAsync(default)); Assert.Equal(shelf, File.ReadAllBytes(Path.Combine(root, "library.json")));
        }
        finally
        {
            profile?.Dispose(); var absolute = Path.GetFullPath(root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (absolute.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("the-order-quick-login-", StringComparison.Ordinal)) Directory.Delete(absolute, true);
        }
    }

    private static async Task<BangumiServiceResponse> Post(BangumiAuthSessions sessions, string path, object value)
    { using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value)); return await sessions.PostAsync(path, body, default); }
    private static BangumiAuthSessions Server(Handler handler, Clock? clock = null) => new(new("synthetic_app", "synthetic_client_secret_not_real", Service.AbsoluteUri), new HttpClient(handler), clock) { RequestSpacing = TimeSpan.Zero };
    private static Handler GrantHandler() => new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/oauth/access_token"
        ? Json(JsonSerializer.Serialize(new { access_token = Access, refresh_token = Refresh, expires_in = 3600 })) : Json("{\"id\":123,\"username\":\"synthetic\",\"nickname\":\"合成用户\"}")));
    private static string State(string url) => Uri.UnescapeDataString(new Uri(url).Query[1..].Split('&').Single(x => x.StartsWith("state=", StringComparison.Ordinal))[6..]);

    [Fact] public async Task ServerRejectsWrongProofReplayAndWrongCallbackState()
    {
        var handler = GrantHandler(); using var server = Server(handler); var verifier = BangumiOAuthProtocol.RandomSecret();
        var begin = await Post(server, BangumiOAuthProtocol.Begin, new { challenge = BangumiOAuthProtocol.Challenge(verifier) });
        using var json = JsonDocument.Parse(begin.Body!); var id = json.RootElement.GetProperty("session_id").GetString()!; var url = json.RootElement.GetProperty("authorization_url").GetString()!;
        Assert.Equal(410, (await Post(server, BangumiOAuthProtocol.Poll, new { session_id = id, verifier = BangumiOAuthProtocol.RandomSecret() })).Status);
        Assert.Equal(410, await server.CallbackAsync(BangumiOAuthProtocol.RandomSecret(), "synthetic_code_12345", false, default)); Assert.Equal(0, handler.Calls);
        Assert.Equal(200, await server.CallbackAsync(State(url), "synthetic_code_12345", false, default));
        Assert.Equal(410, await server.CallbackAsync(State(url), "synthetic_code_12345", false, default));
        var token = await Post(server, BangumiOAuthProtocol.Poll, new { session_id = id, verifier }); Assert.Equal(200, token.Status);
        Assert.Equal(410, (await Post(server, BangumiOAuthProtocol.Poll, new { session_id = id, verifier })).Status);
    }
    [Fact] public async Task CancelledExpiredOrDeniedSessionsCannotYieldTokens()
    {
        var clock = new Clock(); var handler = GrantHandler(); using var server = Server(handler, clock);
        var verifier = BangumiOAuthProtocol.RandomSecret();
        async Task<(string Id, string State)> Begin()
        { var result = await Post(server, BangumiOAuthProtocol.Begin, new { challenge = BangumiOAuthProtocol.Challenge(verifier) }); using var body = JsonDocument.Parse(result.Body!); return (body.RootElement.GetProperty("session_id").GetString()!, State(body.RootElement.GetProperty("authorization_url").GetString()!)); }
        var cancelled = await Begin(); Assert.Equal(204, (await Post(server, BangumiOAuthProtocol.Cancel, new { session_id = cancelled.Id, verifier })).Status);
        Assert.Equal(410, await server.CallbackAsync(cancelled.State, "synthetic_code_12345", false, default));
        var expired = await Begin(); clock.Now += TimeSpan.FromMinutes(4); Assert.Equal(410, await server.CallbackAsync(expired.State, "synthetic_code_12345", false, default));
        var denied = await Begin(); Assert.Equal(200, await server.CallbackAsync(denied.State, "", true, default));
        Assert.Equal(403, (await Post(server, BangumiOAuthProtocol.Poll, new { session_id = denied.Id, verifier })).Status); Assert.Equal(0, handler.Calls);
    }
    [Fact] public async Task FullNativeLoginUsesTheRealHttpHostAndNeverSendsAppSecretToTheClient()
    {
        var handler = GrantHandler(); using var sessions = Server(handler);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var host = BangumiAuthHost.Build(builder, sessions); await host.StartAsync();
        var address = new Uri(host.Urls.Single()); using var nativeNetwork = new HttpClient(new Relay(address)); using var browserNetwork = new HttpClient();
        var browser = new Browser(async (authorization, cancellation) =>
        {
            Assert.DoesNotContain("synthetic_client_secret", authorization.AbsoluteUri);
            using var response = await browserNetwork.GetAsync(new Uri(address, BangumiOAuthProtocol.Callback + "?state=" + State(authorization.AbsoluteUri) + "&code=synthetic_code_12345"), cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.DoesNotContain(Access, await response.Content.ReadAsStringAsync(cancellation));
        });
        var f = Fixture(); var provider = new BangumiOAuthProvider(nativeNetwork, browser, Service);
        var auth = new BangumiAuthorization(f.Store, f.Accounts, f.Access, provider); using var model = new BangumiSettingsViewModel(auth);
        await model.InitializeAsync(); await model.SignInAsync(); Assert.True(model.HasAuthorization); Assert.Equal(1, browser.Opens); Assert.True(f.Store.Value!.IsOAuth);
        var refreshed = await provider.RefreshAsync(f.Store.Value, default); Assert.Equal("123", refreshed.AccountId);
        await host.StopAsync();
    }
    private sealed class Relay(Uri address) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal(Service.Host, request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            request.RequestUri = new Uri(address, request.RequestUri.PathAndQuery); return base.SendAsync(request, cancellation);
        }
    }
}
