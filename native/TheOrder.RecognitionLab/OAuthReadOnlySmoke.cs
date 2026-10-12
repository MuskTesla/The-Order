using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;

namespace TheOrder.RecognitionLab;

internal static class OAuthReadOnlySmoke
{
    internal static async Task<int> Run(string directory, string output)
    {
        var taskRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "auth-sync-20261011")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(directory);
        if (!path.StartsWith(taskRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(output).StartsWith(taskRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Task-owned Test profile and redacted output required.");
        var requests = new List<object>();
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, path), new NativeLibraryCodec());
            var before = profile.ReadComponent("library.json")!;
            var keys = new IsolatedCredentialStore(profile.Configuration());
            using var network = new NextMoeAccountNetwork();
            var rotations = new CountOAuth(new NextMoeOAuth(network.Client));
            var auth = new NextMoeAuthentication(keys, keys, new WindowsNextMoeAuthStore(profile.Configuration()), rotations, new WindowsOAuthLogin(new WindowsOAuthSystemBrowser()));
            var status = await auth.StatusAsync(default);
            if (status.Mode != NextMoeAccessMode.Account || status.Account is null || status.NeedsLogin) throw new OnlineException(OnlineFailure.LoginRequired);
            // A new process has no cached access token: this performs one rotation through the actual official account service.
            await auth.UserTokenAsync(null, default);
            var afterRefresh = await auth.StatusAsync(default);
            if (rotations.Refreshes != 1 || afterRefresh.Account?.Id != status.Account.Id) throw new InvalidOperationException("Account/refresh invariant failed.");
            using var client = new HttpClient(new ReadGuard(requests)) { Timeout = Timeout.InfiniteTimeSpan };
            var rate = new NextMoeRateCoordinator();
            var catalog = new NextMoeCatalog(new CatalogTransport(client, auth, new("https://api.nextmoe.dev/v2/"), rate: rate), includeExplicit: false);
            await catalog.TestConnectionAsync(default);
            var api = new ReadOnlyPersonalApi(new NextMoePersonalApi(client, auth, rate));
            var times = await api.PlaytimesAsync(null, default); var states = await api.StatesAsync(null, default); var ratings = await api.RatingsAsync(null, default);
            int? folders = null;
            if (status.Scopes.Contains("folder:read", StringComparer.Ordinal) || status.Scopes.Contains("folder:write", StringComparer.Ordinal))
            {
                var read = await api.FoldersAsync(null, default); if (read.Items.Any(x => x.OwnerId != status.Account.Id)) throw new InvalidOperationException("Folder account mismatch."); folders = read.Items.Length;
            }
            var snapshot = await auth.AuthContext.CaptureAsync(default);
            await auth.SignOutAsync(default); var signedOut = await auth.StatusAsync(default);
            if (signedOut.Mode != NextMoeAccessMode.Anonymous || signedOut.Account is not null || await auth.ReadAsync(default) is not null || !snapshot.Changed.IsCancellationRequested) throw new InvalidOperationException("Local logout invariant failed.");
            bool privateRejected;
            try { await auth.UserTokenAsync(null, default); privateRejected = false; } catch (OnlineException e) when (e.Failure == OnlineFailure.UserIdentityRequired) { privateRejected = true; }
            if (!before.AsSpan().SequenceEqual(profile.ReadComponent("library.json"))) throw new InvalidOperationException("Read-only acceptance changed library data.");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "PASS", actualOAuth = true, processRestartRefreshes = rotations.Refreshes,
                accountConsistent = true, catalogReadable = true, privateReads = new { playtimePageItems = times.Items.Length, statePageItems = states.Items.Length, ratingPageItems = ratings.Items.Length, folderPageItems = folders },
                localLogout = true, privateRejectedAfterLogout = privateRejected, libraryUnchanged = true, requests, cloudWrites = 0, productionAccess = false, secretsLogged = false, utc = DateTimeOffset.UtcNow }, Program.Json));
            return 0;
        }
        catch (Exception e)
        { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "FAIL", type = e.GetType().Name, failure = (e as OnlineException)?.Failure.ToString(), requests, cloudWrites = 0, productionAccess = false }, Program.Json)); return 1; }
    }
    private sealed class CountOAuth(INextMoeOAuth inner) : INextMoeOAuth
    {
        internal int Refreshes;
        public Task<OAuthTokens> ExchangeAsync(string id, OAuthAuthorizationCode code, CancellationToken token) => throw new InvalidOperationException("Login must be done by the user in the actual application.");
        public Task<OAuthTokens> RefreshAsync(string id, string refresh, CancellationToken token) { Refreshes++; return inner.RefreshAsync(id, refresh, token); }
    }
    private sealed class ReadGuard(List<object> evidence) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (evidence.Count >= 10 || request.Method != HttpMethod.Get || request.RequestUri?.Scheme != "https" || request.RequestUri.Host != "api.nextmoe.dev"
                || !request.RequestUri.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal) || request.Headers.Authorization?.Scheme != "Bearer") throw new InvalidOperationException("Read-only endpoint invariant failed.");
            var response = await base.SendAsync(request, token); evidence.Add(new { path = request.RequestUri.AbsolutePath, status = (int)response.StatusCode }); return response;
        }
    }
}
