using System.Text.Json;
using TheOrder.Infrastructure.Online;

namespace TheOrder.RecognitionLab;

// Bounded public catalog contract capture. Credentials are loaded by the supported provider;
// only response bodies and non-secret request paths are saved in the explicit research directory.
public static partial class Live
{
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
