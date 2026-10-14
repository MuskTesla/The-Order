using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

// This explicit socket/DPAPI contract fixture is run by run-client-bridge.mjs.
// Ordinary unit tests do not start external services or access live accounts.
public sealed class WorkerFixtureFactAttribute : FactAttribute
{
    public WorkerFixtureFactAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("THE_ORDER_WORKER_FIXTURE_URL"))) Skip = "Opt-in local workerd fixture; run services/bangumi-auth-worker/test/run-client-bridge.mjs"; }
}
public sealed class WorkerBrokerIntegrationTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Relay(Uri address) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var uri = request.RequestUri!;
            if (uri.Origin() == BangumiOAuthDefaults.Service!.Origin() && uri.AbsolutePath.StartsWith("/v1/bangumi/", StringComparison.Ordinal))
            { Assert.Null(request.Headers.Authorization); request.RequestUri = new(address, uri.PathAndQuery); }
            else if (uri.AbsoluteUri == "https://api.bgm.tv/v0/me") request.RequestUri = new(address, "__fixture/me");
            else if (uri.AbsoluteUri == "https://api.bgm.tv/v0/subjects/13") request.RequestUri = new(address, "__fixture/subject/13");
            else throw new InvalidOperationException("The synthetic fixture refuses every other URL");
            return base.SendAsync(request, cancellation);
        }
    }
    private sealed class Browser(Uri address, HttpClient client) : IBangumiOAuthBrowser
    {
        public int Opens;
        public async Task OpenAsync(Uri authorization, CancellationToken cancellation)
        {
            Opens++; Assert.True(BangumiOAuthBrowserPolicy.ValidOrigin(authorization));
            Assert.DoesNotContain("synthetic_client_secret", authorization.AbsoluteUri);
            var query = authorization.Query[1..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Assert.Equal(new Uri(BangumiOAuthDefaults.Service!, BangumiOAuthProtocol.Callback).AbsoluteUri, query["redirect_uri"]);
            using var response = await client.GetAsync(new Uri(address, BangumiOAuthProtocol.Callback + "?state=" + query["state"] + "&code=synthetic_code_123456"), cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync(cancellation));
        }
    }
    [WorkerFixtureFact] public async Task LoginRestartRefreshRestrictedReadAndDisconnectUseTheExistingClientAndEncryptedStore()
    {
        var url = Environment.GetEnvironmentVariable("THE_ORDER_WORKER_FIXTURE_URL")!;
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme == "http" && address.Host == "127.0.0.1" && address.Port > 0 && address.AbsolutePath == "/");
        var root = Path.Combine(Path.GetTempPath(), "the-order-worker-bridge-" + Guid.NewGuid().ToString("N"));
        NativeProfileStore? profile = null;
        try
        {
            profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            var shelf = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "library.json")));
            using var network = new HttpClient(new Relay(address!)); using var browserNetwork = new HttpClient();
            var clock = new Clock(); var browser = new Browser(address!, browserNetwork);
            var provider = new BangumiOAuthProvider(network, browser, BangumiOAuthDefaults.Service, clock);
            var accounts = new BangumiAccountProvider(network); var store = new WindowsBangumiCredentialStore(profile.Configuration());
            var access = new BangumiAccessContext(); var auth = new BangumiAuthorization(store, accounts, access, provider, clock);
            using var model = new BangumiSettingsViewModel(auth); await model.InitializeAsync(); await model.SignInAsync();
            Assert.Equal(1, browser.Opens); Assert.True(model.HasAuthorization); Assert.Contains("@bridge-fixture", model.Status);
            var cipher = File.ReadAllBytes(Path.Combine(root, WindowsBangumiCredentialStore.FileName));
            Assert.DoesNotContain("synthetic_bridge_access", Encoding.UTF8.GetString(cipher)); Assert.DoesNotContain("synthetic_bridge_refresh", Encoding.UTF8.GetString(cipher));
            var original = await store.ReadCredentialAsync(default); Assert.True(original!.IsOAuth);

            profile.Dispose(); profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            store = new(profile.Configuration()); access = new(); auth = new(store, accounts, access, provider, clock);
            await auth.InitializeAsync(default); Assert.Equal("123", auth.Account!.Id); Assert.True(access.Capture().Authorized);
            Assert.Equal(cipher, File.ReadAllBytes(Path.Combine(root, WindowsBangumiCredentialStore.FileName)));
            clock.Now += TimeSpan.FromMinutes(56); await Task.WhenAll(auth.RefreshAsync(default), auth.RefreshAsync(default));
            var rotated = await store.ReadCredentialAsync(default); Assert.NotEqual(original.WithSecrets((_, refresh) => refresh), rotated!.WithSecrets((_, refresh) => refresh));
            Assert.True(access.Capture().Authorized);

            var authorized = new BangumiSubjectDetailsProvider(network, access: access);
            var facts = await authorized.FetchAsync("13", BangumiRelationKind.Game, default); Assert.Equal("13", facts.Id);
            var anonymous = new BangumiSubjectDetailsProvider(network);
            await Assert.ThrowsAsync<BangumiRelationsException>(() => anonymous.FetchAsync("13", BangumiRelationKind.Game, default));
            await auth.ClearAsync(default); Assert.Null(await store.ReadCredentialAsync(default)); Assert.False(access.Capture().Authorized);
            Assert.Equal(shelf, SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "library.json"))));
            using var stats = JsonDocument.Parse(await browserNetwork.GetStringAsync(new Uri(address!, "__fixture/stats")));
            Assert.Equal(1, stats.RootElement.GetProperty("exchanges").GetInt32()); Assert.Equal(1, stats.RootElement.GetProperty("refreshes").GetInt32());
            Assert.Equal(2, stats.RootElement.GetProperty("directMetadata").GetInt32());
        }
        finally
        {
            profile?.Dispose(); var absolute = Path.GetFullPath(root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (absolute.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("the-order-worker-bridge-", StringComparison.Ordinal)) Directory.Delete(absolute, true);
        }
    }
}
internal static class WorkerTestUri
{ internal static string Origin(this Uri uri) => uri.GetLeftPart(UriPartial.Authority); }
