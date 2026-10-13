using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;

namespace TheOrder.RecognitionLab;

// A single public credit/appearance page and one missing-cover batch. Use the existing
// supported Test credential provider; persist catalog JSON, never headers or credentials.
public static partial class Live
{
    private sealed class EntityBodies(string path, string page, string? covers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var body = request.RequestUri!.AbsolutePath == "/v2/" + path ? page
                : request.RequestUri.AbsolutePath == "/v2/catalog/works" && covers is not null ? covers
                : throw new InvalidDataException("Unrecorded entity request");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    public static async Task<int> CaptureEntityCovers(string kindValue, string id, string directory)
    {
        if (!RemoteWorkDto.ValidId(id) || !Enum.TryParse<CatalogEntityKind>(kindValue, out var kind)
            || kind is not (CatalogEntityKind.Character or CatalogEntityKind.CreditName)) return 2;
        if (Directory.Exists(directory)) throw new InvalidOperationException("A fresh evidence directory is required");
        Directory.CreateDirectory(directory);
        var path = kind == CatalogEntityKind.Character ? "catalog/characters/" + id + "/appearances"
            : "catalog/credit-names/" + id + "/credits";
        var parameters = new Dictionary<string, string> { ["nsfw"] = "true", ["limit"] = "20" };
        using var network = new CatalogNetwork();
        var transport = new CatalogTransport(network.Client, new EnvironmentCredential(), new("https://api.nextmoe.dev/v2/"));
        var receipts = new List<object>();
        string page = ""; string? covers = null; ImmutableArray<string> missing = [];
        await transport.Operation(async scope =>
        {
            var started = DateTimeOffset.UtcNow;
            using var response = await transport.GetAsync(path, parameters, scope);
            page = response.RootElement.GetRawText();
            // Determine the missing set through the shipping validator with a synthetic
            // empty cover batch, rather than duplicating the global eligibility rule.
            using var previewHttp = new HttpClient(new EntityBodies(path, page, "{\"object\":\"list\",\"items\":[]}"));
            var previewCatalog = new NextMoeCatalog(new(previewHttp, new RecordedCredential(), new("https://api.nextmoe.dev/v2/")), clock: () => DateTimeOffset.UnixEpoch);
            var preview = await previewCatalog.EntityWorksAsync(kind, id, null, default);
            missing = [.. preview.Works.Where(w => w.CoverUrl is null).Select(w => w.Id)];
            await File.WriteAllTextAsync(Path.Combine(directory, "entity-works.json"), page);
            receipts.Add(new { path, parameters, started, finished = DateTimeOffset.UtcNow, file = "entity-works.json" });
            if (!missing.IsEmpty)
            {
                var query = new Dictionary<string, string> { ["nsfw"] = "true", ["ids"] = string.Join(',', missing), ["include"] = "covers", ["limit"] = "100" };
                started = DateTimeOffset.UtcNow;
                using var batch = await transport.GetAsync("catalog/works", query, scope);
                covers = batch.RootElement.GetRawText();
                await File.WriteAllTextAsync(Path.Combine(directory, "covers.json"), covers);
                receipts.Add(new { path = "catalog/works", parameters = query, started, finished = DateTimeOffset.UtcNow, file = "covers.json" });
            }
            return true;
        }, default);
        await File.WriteAllTextAsync(Path.Combine(directory, "requests.json"), JsonSerializer.Serialize(receipts, Program.Json));
        using var recorded = new HttpClient(new EntityBodies(path, page, covers));
        var catalog = new NextMoeCatalog(new(recorded, new RecordedCredential(), new("https://api.nextmoe.dev/v2/")), clock: () => DateTimeOffset.UnixEpoch);
        var result = await catalog.EntityWorksAsync(kind, id, null, default);
        var summary = new { kind = kind.ToString(), id, requests = receipts.Count, source = "direct-online-response-and-offline-mapping-replay",
            eligibleWorks = result.Works.Length, missingBefore = missing.Length, withCoversAfter = result.Works.Count(w => w.CoverUrl is not null),
            result.NextCursor, works = result.Works.Select(w => new { w.Id, w.Title, w.CoverUrl }) };
        await File.WriteAllTextAsync(Path.Combine(directory, "mapped-result.json"), JsonSerializer.Serialize(summary, Program.Json));
        Console.WriteLine($"{kind} {id}: eligible={result.Works.Length}; missing-before={missing.Length}; covers-after={result.Works.Count(w => w.CoverUrl is not null)}; online-requests={receipts.Count}; replay-http=0");
        return 0;
    }
}
