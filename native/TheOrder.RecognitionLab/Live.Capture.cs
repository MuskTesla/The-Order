using System.Text.Json;
using TheOrder.Infrastructure.Online;

namespace TheOrder.RecognitionLab;

// Bounded public catalog contract capture. Credentials are loaded by the supported provider;
// only response bodies and non-secret request paths are saved in the explicit research directory.
public static partial class Live
{
    private sealed class RecordedCredential : TheOrder.Application.ICredentialProvider
    {
        public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>("recorded-response-only");
    }
    private sealed class CapturedBodies(string directory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (4 or 5) || parts[0] != "v2" || parts[1] != "catalog" || parts[2] != "works"
                || !RemoteWorkDto.ValidId(parts[3]) || parts.Length == 5 && parts[4] != "releases") throw new InvalidDataException("Unrecorded request");
            var file = Path.Combine(directory, parts[3] + (parts.Length == 5 ? "-releases.json" : "-work.json"));
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(await File.ReadAllTextAsync(file, cancellation), System.Text.Encoding.UTF8, "application/json") };
        }
    }
    // Map the recorded bodies through the real transport/catalog validators. The handler never
    // connects to a network; both compared binaries then consume exactly these same identities.
    public static async Task<int> MapCapture(string directory, string input, string output)
    {
        var cache = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(input))!;
        var files = Directory.GetFiles(directory, "*-work.json");
        if (files.Length is < 1 or > 8) return 2;
        using var client = new HttpClient(new CapturedBodies(directory));
        var catalog = new NextMoeCatalog(new CatalogTransport(client, new RecordedCredential(), new("https://api.nextmoe.dev/v2/")), clock: () => DateTimeOffset.UnixEpoch);
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(file).Replace("-work.json", "", StringComparison.Ordinal);
            if (!RemoteWorkDto.ValidId(id)) throw new InvalidDataException("Invalid captured identity");
            cache["details|" + id] = JsonSerializer.Serialize(await catalog.DetailsAsync(id, true, default), Replay.Options);
            cache["releases|" + id] = JsonSerializer.Serialize(await catalog.ReleasesAsync(id, default), Replay.Options);
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(cache));
        Console.WriteLine(files.Length + " recorded work/release pairs mapped; http=0"); return 0;
    }

    // Apply the shipping mapper to already captured public bodies without sending a request.
    public static async Task<int> MapFacts(string directory, string output)
    {
        var facts = new Dictionary<string, TheOrder.Domain.CatalogWorkIdentity>();
        foreach (var file in Directory.EnumerateFiles(directory, "*-work.json"))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var work = new RemoteWorkDto(document.RootElement).Map(true, DateTimeOffset.UnixEpoch);
            if (Path.GetFileName(file) != work.Id + "-work.json") throw new InvalidDataException("Captured work identity differs.");
            facts.Add(work.Id, work.CatalogIdentity!);
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(facts, Program.Json));
        Console.WriteLine(facts.Count + " bounded catalog identity records; http=0");
        return 0;
    }

    public static async Task<int> Capture(string ids, string directory)
    {
        var requested = ids.Split(',').Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length is < 1 or > 8 || requested.Any(id => !RemoteWorkDto.ValidId(id))) return 2;
        Directory.CreateDirectory(directory);
        using var network = new CatalogNetwork();
        var transport = new CatalogTransport(network.Client, new EnvironmentCredential(), new Uri("https://api.nextmoe.dev/v2/"));
        var receipts = new List<object>();
        foreach (var id in requested)
        {
            foreach (var release in new[] { false, true })
            {
                var path = "catalog/works/" + id + (release ? "/releases" : "");
                var parameters = release ? new Dictionary<string, string> { ["nsfw"] = "true", ["limit"] = "100", ["include"] = "companies,refs" }
                    : new Dictionary<string, string> { ["nsfw"] = "true", ["include"] = "titles,refs,companies,links" };
                var started = DateTimeOffset.UtcNow;
                var raw = await transport.Operation(async scope =>
                {
                    using var body = await transport.GetAsync(path, parameters, scope);
                    return body.RootElement.GetRawText();
                }, CancellationToken.None);
                var file = id + (release ? "-releases" : "-work") + ".json";
                await File.WriteAllTextAsync(Path.Combine(directory, file), raw);
                receipts.Add(new { path, parameters, started, finished = DateTimeOffset.UtcNow, file });
                Console.WriteLine(file + " " + System.Text.Encoding.UTF8.GetByteCount(raw) + " bytes");
                await File.WriteAllTextAsync(Path.Combine(directory, "requests.json"), JsonSerializer.Serialize(receipts, Program.Json));
            }
        }
        return 0;
    }
}
