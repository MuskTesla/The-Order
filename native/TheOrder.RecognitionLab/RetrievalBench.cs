using System.Diagnostics;
using System.Text.Json;
using TheOrder.Infrastructure.Recognition;

namespace TheOrder.RecognitionLab;

// Offline public-title retrieval measurement. This does not decide or apply a work identity.
internal static class RetrievalBench
{
    private sealed record Input(string Id, string Title, string ExpectedReference);
    internal static async Task<int> Run(string inputPath, string indexPath, string outputPath)
    {
        var inputs = JsonSerializer.Deserialize<Input[]>(await File.ReadAllTextAsync(inputPath))!;
        if (inputs.Length > 100 || inputs.Any(i => i.Title.Length > 500)) throw new InvalidDataException();
        var index = new TitleIndex(() => File.OpenRead(indexPath));
        var load = Stopwatch.StartNew(); await index.FindAsync([], default); var loadMs = load.Elapsed.TotalMilliseconds;
        var outcomes = new List<object>();
        foreach (var input in inputs)
        {
            var watch = Stopwatch.StartNew(); var exact = await index.FindAsync([input.Title], default); var exactMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var nearby = await index.FindSimilarAsync([input.Title], default); var firstMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var repeat = await index.FindSimilarAsync([input.Title], default); var repeatMs = watch.Elapsed.TotalMilliseconds;
            outcomes.Add(new { input.Id, input.Title, input.ExpectedReference, exactExpected = exact.Any(r => r.Record.References.Contains(input.ExpectedReference)),
                nearbyExpected = nearby.Any(r => r.Record.References.Contains(input.ExpectedReference)), exactRows = exact.Length, nearbyRows = nearby.Length,
                allNearbyNonAuthoritative = nearby.All(r => !r.Exact), repeatSameRows = nearby.Select(r => r.Row).SequenceEqual(repeat.Select(r => r.Row)), exactMs, firstMs, repeatMs });
        }
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new { loadMs, index.RecordCount, index.TitleCount, outcomes }, Program.Json));
        Console.WriteLine($"Measured {inputs.Length} public title mutations; no network, ranking or automatic application.");
        return 0;
    }
}
