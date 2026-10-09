using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;

namespace TheOrder.RecognitionLab;

// Offline research harness. Every command is read-only with respect to its inputs and writes
// only to the explicit output path. It never launches executables or touches a library profile.
public static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: inventory <root> <index.json.gz> <out.json>"); return 2; }
        return args[0] switch
        {
            "inventory" => await Inventory(args[1], args[2], args[3]),
            "replay" => await RunReplay(args[1], args[2], args[3], args[4], args[5]),
            "indexstats" => await IndexStats(args[1]),
            "rankbench" => await Engines.RankBench(args[1], args[2], args[3]),
            "explain" => await Engines.Explain(args[1], args[2], args[3], args[4]),
            "dumpprofile" => Live.DumpTestProfile(args[1]),
            "seedcredential" => await Live.SeedTestCredential(args[1]),
            "live" => await Live.Run(args[1], args[2], args[3], args[4], args[5], args.Length > 6 && args[6] == "coverage"),
            _ => 2,
        };
    }

    // Uses the production scanner and evidence collector so the input matches a real import.
    private static async Task<int> Inventory(string root, string indexPath, string output)
    {
        var identity = new WindowsFilesystemIdentity(); var filesystem = new WindowsScanFilesystem();
        var scan = await new BoundedScanner(identity, filesystem).ScanAsync(root, []);
        var reader = new WindowsRecognitionEvidence(new LocalEvidenceParser(), RecognitionFingerprint.Evidence, new ExecutableEvidenceParser());
        var index = new TitleIndex(() => File.OpenRead(indexPath));
        var rows = new List<object>();
        foreach (var candidate in scan.Candidates.OrderBy(c => c.Folder.StoredPath, StringComparer.Ordinal))
        {
            var installation = new Installation("i-" + rows.Count, candidate.Folder.StoredPath,
                candidate.Executables.Length == 1 ? candidate.Executables[0] : null, []);
            var entry = Entry(candidate.Name, installation);
            var started = System.Diagnostics.Stopwatch.StartNew();
            LocalEvidenceSnapshot local;
            try { local = await reader.ReadAsync(entry, true, CancellationToken.None); }
            catch (Exception e)
            {
                rows.Add(new { Folder = Path.GetRelativePath(root, candidate.Folder.StoredPath), candidate.Name, Failure = e.GetType().Name + ": " + e.Message,
                    Executables = candidate.Executables.Select(Path.GetFileName).ToArray() });
                continue;
            }
            var evidenceMs = started.Elapsed.TotalMilliseconds;
            var built = await RecognitionIdentityBuilder.BuildAsync(entry, local.Evidence, index, CancellationToken.None);
            rows.Add(new
            {
                Folder = Path.GetRelativePath(root, candidate.Folder.StoredPath),
                candidate.Name,
                Executables = candidate.Executables.Select(Path.GetFileName).ToArray(),
                installation.Executable,
                EvidenceMs = evidenceMs,
                Titles = local.Evidence.Titles.Select(t => new { t.Source, t.Value }),
                References = local.Evidence.References.Select(r => new { r.Source, r.Value }),
                local.Evidence.Companies,
                local.Evidence.Warnings,
                local.Evidence.AuxiliaryDirectory,
                IdentityTitles = built.Evidence.Titles.Select(t => new { t.Source, t.Value }),
                IndexMatches = built.LocalCandidates.Select(m => new { m.Row, m.ObservedTitle, Names = m.Record.Names, References = m.Record.References }),
                built.Queries,
                IdentityReferences = built.References,
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Root = root, scan.Status, scan.Warnings, Candidates = rows }, Json));
        Console.WriteLine($"{scan.Status}: {rows.Count} candidates");
        return 0;
    }

    private static async Task<int> RunReplay(string casesPath, string catalogPath, string indexPath, string output, string engineName)
    {
        var cases = JsonSerializer.Deserialize<List<EvidenceCase>>(await File.ReadAllTextAsync(casesPath), Replay.Options)!;
        var load = System.Diagnostics.Stopwatch.StartNew();
        var catalog = new ReplayCatalog(catalogPath); var index = new TitleIndex(() => File.OpenRead(indexPath));
        Console.Error.WriteLine($"catalog {catalog.Works.Count} works loaded in {load.Elapsed.TotalSeconds:0.0}s");
        var (factory, select, apply) = Engines.Get(engineName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        int parallel = int.TryParse(Environment.GetEnvironmentVariable("LAB_PARALLEL"), out var p) ? p : 1;
        var outcomes = await Replay.RunAsync(cases, catalog, index, factory, select, apply, parallel);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(outcomes, Json));
        var summary = Replay.Summary(outcomes); Console.WriteLine(summary);
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".summary.txt"), summary);
        return 0;
    }

    // Load cost of a title index as the application loads it (first lookup), plus readme matching.
    private static async Task<int> IndexStats(string path)
    {
        long before = GC.GetTotalMemory(true);
        var index = new TitleIndex(() => File.OpenRead(path));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await index.FindAsync([], CancellationToken.None);
        double load = watch.Elapsed.TotalMilliseconds;
        long after = GC.GetTotalMemory(true);
        watch.Restart();
        var found = await index.FindAsync(["CLANNAD", "Summer Pockets", "星空の航路"], CancellationToken.None);
        double find = watch.Elapsed.TotalMilliseconds; watch.Restart();
        var readme = await index.FindReadmeTitlesAsync(["サクラノ詩－櫻の森の上を舞う－ 10th Anniversary edition をお買い上げいただき"], CancellationToken.None);
        double readmeMs = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{Path.GetFileName(path)} bytes={new FileInfo(path).Length} records={index.RecordCount} titles={index.TitleCount} load_ms={load:0} retained_MB={(after - before) / 1048576.0:0.0} find_ms={find:0.0} hits={found.Length} readme_ms={readmeMs:0.0} readme={string.Join("|", readme)}");
        GC.KeepAlive(index);
        return 0;
    }

    internal static LibraryEntry Entry(string name, Installation installation) => new(
        "lab-" + Guid.NewGuid().ToString("N"), name, false, null, [installation], installation.Id, "planned", null, false,
        [], [], "", 0, 0, [], "2026-10-09T00:00:00.000Z", null, null, null, new(false, null));
}
