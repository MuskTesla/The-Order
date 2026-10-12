using System.Net.Http;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;

namespace TheOrder.RecognitionLab;

internal static class AuthenticationSmoke
{
    internal static async Task<int> Anonymous(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "the-order-anonymous-" + Guid.NewGuid().ToString("N"));
        var receipt = new List<object>();
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, directory), new NativeLibraryCodec());
            var keys = new IsolatedCredentialStore(profile.Configuration());
            using var account = new NextMoeAccountNetwork();
            var auth = new NextMoeAuthentication(keys, keys, new WindowsNextMoeAuthStore(profile.Configuration()), new NextMoeOAuth(account.Client), new WindowsOAuthLogin(new WindowsOAuthSystemBrowser()));
            if ((await auth.StatusAsync(default)).Mode != NextMoeAccessMode.Anonymous) throw new InvalidOperationException("New profile must be anonymous.");
            using var client = new HttpClient(new Guard(receipt)) { Timeout = Timeout.InfiniteTimeSpan };
            var transport = new CatalogTransport(client, auth, new("https://api.nextmoe.dev/v2/"), rate: new NextMoeRateCoordinator());
            var catalog = new NextMoeCatalog(transport, includeExplicit: false);
            var search = await catalog.SearchAsync("CLANNAD", null, default);
            if (search.Items.IsEmpty) throw new InvalidOperationException("Public search returned no usable work.");
            var details = await catalog.DetailsAsync(search.Items[0].Id, true, default);
            if (details.Id != search.Items[0].Id) throw new InvalidOperationException("Public identity changed.");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "PASS", liveAnonymous = true, mode = "Anonymous", requests = receipt,
                searchItems = search.Items.Length, detailId = details.Id, credentialRead = false, privateRequests = 0, userWrites = 0, productionAccess = false, utc = DateTimeOffset.UtcNow }, Program.Json));
            return 0;
        }
        catch (Exception exception)
        { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "FAIL", type = exception.GetType().Name, failure = (exception as OnlineException)?.Failure.ToString(), requests = receipt }, Program.Json)); return 1; }
        finally
        {
            var path = Path.GetFullPath(directory); var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("the-order-anonymous-", StringComparison.Ordinal)) throw new InvalidOperationException("Temp fixture escaped its root.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
    private sealed class Guard(List<object> evidence) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (evidence.Count >= 4 || request.Headers.Authorization is not null || request.Method != HttpMethod.Get || request.RequestUri?.Host != "api.nextmoe.dev"
                || request.RequestUri.Scheme != "https" || !request.RequestUri.AbsolutePath.StartsWith("/v2/catalog/", StringComparison.Ordinal)) throw new InvalidOperationException("Read-only anonymous contract violated.");
            var reply = await base.SendAsync(request, cancellation); evidence.Add(new { path = request.RequestUri.AbsolutePath, status = (int)reply.StatusCode, authorization = false }); return reply;
        }
    }
    internal static async Task<int> Seed(string directory)
    {
        var path = Path.GetFullPath(directory);
        var artifactRoot = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "auth-sync-20261011") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path)) throw new InvalidOperationException("Fresh owned Test profile required.");
        using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, path), new NativeLibraryCodec());
        var at = DateTimeOffset.UtcNow.ToString("o");
        var work = new WorkMetadata("86", "合成作品", "合成作品", [], null, null, null, null, "原创合成资料，用于隔离验收。", null, null, [], [], [], [], [], null, null, at);
        var entry = new LibraryEntry("synthetic-auth", "合成作品", true, work, [], null, "planned", 8.5, false, ["个人分类"], ["个人标签"], "本地合成笔记", 0, 0, [], at, null, null, null, new(false, null));
        using var writer = profile.CreateLibraryPersistence();
        await writer.SaveAsync(writer.ReadMain()!, new NativeLibraryCodec().Encode([entry], at));
        return 0;
    }
}
