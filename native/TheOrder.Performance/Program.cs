using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;

namespace TheOrder.Performance;

public static class Program
{
    public const string Date = "2026-01-01T00:00:00.000Z";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly NativeLibraryCodec Codec = new();
    private static int seed = 7101;
    private static int[] libraryCounts = [1000, 5000, 10000], historyCounts = [1000, 10000, 50000];
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("mode output-directory cover-directory [soak-minutes]"); return 2; }
        string output = Path.GetFullPath(args[1]), covers = Path.GetFullPath(args[2]);
        if (!output.Contains(Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Artifact root required");
        NativeProfilePaths.Validate(new(NativeProfileKind.Test, output));
        Directory.CreateDirectory(output);
        if (Environment.GetEnvironmentVariable("THE_ORDER_PERF_SEED") is { } configuredSeed) seed = int.Parse(configuredSeed);
        if (args[0] != "soak" && args.Length > 3) libraryCounts = Counts(args[3], 10000);
        if (args.Length > 4) historyCounts = Counts(args[4], 50000);
        Write(Path.Combine(output, "environment.json"), new { machine = Environment.MachineName, os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription, cpuCount = Environment.ProcessorCount, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), source = SourceIdentity(), productionAccess = false, seed, libraryCounts, historyCounts });
        try
        {
            if (args[0] == "soak") await Soak(output, covers, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 120);
            else if (args[0] == "generate") { foreach (int n in libraryCounts) Generate(Path.Combine(output, "library-" + n), n, 0, covers); }
            else if (args[0] == "session-checkpoints") await SessionCheckpoints(output, covers);
            else if (args[0] == "media") await Media(output, covers);
            else await Benchmarks(output, covers);
            return 0;
        }
        catch (Exception e) { Write(Path.Combine(output, "failure.json"), new { type = e.GetType().Name, e.Message, e.StackTrace }); return 1; }
    }
    public static string SourceIdentity()
    {
        using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        string head = git!.StandardOutput.ReadToEnd().Trim(); git.WaitForExit(); return head;
    }
    public static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    private static int[] Counts(string csv, int maximum) { var values = csv.Split(',').Select(int.Parse).ToArray(); Need(values.Length is > 0 and <= 8 && values.All(n => n > 0 && n <= maximum), "Invalid dataset counts"); return values; }
    public static ImmutableArray<LibraryEntry> Dataset(int count, int history = 0, int deterministicSeed = 7101)
    {
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var id = Id($"game-{deterministicSeed}-" + i).ToString(); var install = Id($"install-{deterministicSeed}-" + i).ToString();
            var title = $"合成作品 {i:D5} / 空と星の物語 {i % 91}";
            var extension = new[] { "jpg", "png", "webp" }[i % 3];
            var metadata = new WorkMetadata("synthetic-" + i, title, $"星空の約束 {i:D5}", [$"Alias {i:D5}", $"別名 {i}"], null, $"https://images.example/cover-{i:D5}.{extension}", null, null,
                $"Original synthetic description {i}; route {i % 23}.", "2020-01-01", "day", [$"Studio {i % 31}"], [$"genre {i % 17}"], [new("synthetic", i % 10, 10, 100)], [], [], null, null, Date);
            int records = history > 0 && i == 0 ? history : i % 4;
            var sessions = Enumerable.Range(0, records).Select(s =>
            {
                var at = DateTimeOffset.Parse(Date).AddMinutes(s); long seconds = s % 241 + 30;
                if (history >= 50000 && s % 4 != 0) return new PlaySession(Id($"session-{i}-{s}").ToString(), NativeSession.Format(at), NativeSession.Format(at.AddSeconds(seconds)), seconds);
                return new NativeSession(1, id, install, at, at, at.AddSeconds(seconds), at.AddSeconds(seconds), seconds * NativeSession.TicksPerSecond,
                    SessionLifecycle.Completed, SessionReason.ExitConfirmed, false, 2, Id("historic-owner"), new(0, null), null).Project(Id($"session-{i}-{s}").ToString());
            }).ToImmutableArray();
            var folder = $"D:\\SyntheticGames\\作品-{i:D5}";
            return new LibraryEntry(id, title, false, metadata, i % 9 == 0 ? [] : [new(install, folder, folder + "\\game.exe", ["--synthetic", i.ToString()], folder)], i % 9 == 0 ? null : install,
                new[] { "planned", "playing", "completed", "paused", "dropped" }[i % 5], i % 11, i % 3 == 0, [$"category {i % 13}"], [$"tag {i % 19}"], $"Synthetic note {i}, variation {i % 47}",
                sessions.Sum(s => s.Seconds), sessions.Length, sessions, DateTimeOffset.Parse(Date).AddMinutes(i).ToString("o"), sessions.Length > 0 ? sessions[^1].StartedAt : null, null, null, new(false, null));
        })];
    }
    public static object Generate(string root, int count, int history, string covers)
    {
        var data = Dataset(count, history, seed); var bytes = Codec.Encode(data, Date);
        using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec);
        using (var library = TransactionalLibrary.Open(Codec, owner.CreateLibraryPersistence()))
        {
            if (library.Snapshot.Entries.Length == 0) library.ImportAsync(bytes).GetAwaiter().GetResult();
            Need(library.Snapshot.Entries.Length == count, "Existing fixture count mismatch");
        }
        owner.CheckpointBackups();
        var identity = new { seed, count, history, payloadBytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), root, owner.Manifest.ProfileId, owner.Manifest.StoreId, coverDataset = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(covers, "dataset.json")))) };
        Write(Path.Combine(root, "performance-dataset.json"), identity); return identity;
    }
    public static async Task Benchmarks(string output, string covers)
    {
        var all = new List<object>();
        foreach (int n in libraryCounts)
        {
            Console.WriteLine("library=" + n); var root = Path.Combine(output, "library-" + n);
            var identity = Generate(root, n, 0, covers);
            var operations = new List<object>();
            operations.Add(await Measure("profile-open", 1, 5, () => { using var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); return Task.CompletedTask; }));
            using (var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec))
            await using (var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, p.Configuration()), new LegacyClientTextRules()))
            {
                Need((await client.LoadAsync(default)).Outcome == ClientOutcome.Success, "Load failed");
                operations.Add(await Measure("first-query-index", 0, 1, async () => { Need((await client.QueryAsync(new(), default)).Items.Length == n, "Index count"); }));
                foreach (var q in Queries()) operations.Add(await Measure(q.Name, 1, 9, async () => { await client.QueryAsync(q.Query, default); }));
                operations.Add(await Measure("combined-200-queries", 1, 3, async () => { for (int i = 0; i < 200; i++) await client.QueryAsync(Queries()[i % Queries().Length].Query, default); }));
                operations.Add(await Measure("details-300", 1, 3, async () => { for (int i = 0; i < 300; i++) await client.DetailsAsync(Id($"game-{seed}-" + i % n).ToString(), default); }));
                operations.Add(await Measure("metadata-edit", 1, 3, async () =>
                {
                    var d = await client.DetailsAsync(Id($"game-{seed}-1").ToString(), default); var v = d.Personal;
                    Need((await client.SaveAsync(d, new(v.Name, v.Status, (v.Rating ?? 0).ToString(), v.Favorite, "edit " + Guid.NewGuid(), string.Join(", ", v.Categories), string.Join(", ", v.Tags)), default)).IsCommitted, "Edit rejected");
                }));
            }
            operations.Add(await Measure("backup", 1, 3, () => { using var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); p.CheckpointBackups(); return Task.CompletedTask; }));
            operations.Add(await Measure("restore-reopen", 1, 3, () => { using (var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec)) p.Restore("library.json"); using var reopened = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); return Task.CompletedTask; }));
            all.Add(new { identity, operations, envelopeBytes = new FileInfo(Path.Combine(root, "library.json")).Length, backupBytes = new FileInfo(Path.Combine(root, "library.json.bak")).Length, writtenBytesPerCommit = 2 * new FileInfo(Path.Combine(root, "library.json")).Length, temporaryPeakUpperBound = 2 * new FileInfo(Path.Combine(root, "library.json")).Length, resources = Resources() });
            Write(Path.Combine(output, "library-results.json"), all);
        }
        var histories = new List<object>();
        foreach (int n in historyCounts)
        {
            Console.WriteLine("history=" + n); string root = Path.Combine(output, "history-" + n); var identity = Generate(root, 1, n, covers);
            var operations = new List<object>();
            operations.Add(await Measure("history-open-parse", 1, 3, () => { using var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); return Task.CompletedTask; }));
            using (var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec))
            await using (var library = TransactionalLibrary.Open(Codec, owner.CreateLibraryPersistence()))
            {
                var entry = library.Snapshot.Entries[0]; var install = Id("install-7101-0").ToString();
                // Existing valid nonterminal record supplies the same production checkpoint transaction path.
                // Synthetic owner rewrite establishes its current writer; no process is launched.
                // Append is measured through portable import; imported history never grants launch ownership.
                int next = 0;
                operations.Add(await Measure("history-append-import", 1, 3, async () =>
                {
                    var incoming = entry with { Id = Id("append-game-" + next++).ToString(), Metadata = null, Installations = [], PreferredInstallationId = null, Sessions = [entry.Sessions[0]], PlayCount = 1, TotalPlaySeconds = entry.Sessions[0].Seconds };
                    await library.ImportAsync(Codec.Encode([incoming], Date));
                }));
            }
            operations.Add(await Measure("history-backup", 1, 3, () => { using var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); p.CheckpointBackups(); return Task.CompletedTask; }));
            operations.Add(await Measure("history-restore", 1, 3, () => { using var p = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec); p.Restore("library.json"); return Task.CompletedTask; }));
            histories.Add(new { identity, operations, envelopeBytes = new FileInfo(Path.Combine(root, "library.json")).Length, resources = Resources() });
            Write(Path.Combine(output, "history-results.json"), histories);
        }
        Write(Path.Combine(output, "completed.json"), new { status = "BENCHMARKS_COMPLETED", at = DateTimeOffset.UtcNow });
    }
    public static (string Name, LibraryQuery Query)[] Queries() => [
        ("exact-title", new("合成作品 00500 / 空と星の物語 45")), ("prefix", new("合成作品 00")), ("substring", new("物語")), ("alias", new("Alias 00500")), ("no-match", new("missing-unique-token")), ("common", new("合成作品")), ("unicode", new("星空の約束")),
        ("sort-title", new(Sort: "name")), ("sort-recent", new(Sort: "recent")), ("sort-playtime", new(Sort: "playtime")), ("sort-rating", new(Sort: "rating")), ("sort-added", new(Sort: "added")),
        ("favorite", new(Favorites: true)), ("category", new(Category: "category 3")), ("tag", new(Tag: "genre 4")), ("installed", new(View: "local")), ("combined", new(Favorites: true, Category: "category 3", Tag: "tag 4")), ("search-filter", new("物語", Favorites: true)), ("empty-filter", new(Category: "absent")) ];
    private static async Task SessionCheckpoints(string output, string covers)
    {
        var results = new List<object>();
        foreach (int count in historyCounts)
        {
            string root = Path.Combine(output, "checkpoint-" + count); var data = Dataset(1, count, seed); var original = data[0];
            string install = Id("checkpoint-install").ToString(), sessionId = Id("checkpoint-session").ToString();
            var at = DateTimeOffset.Parse(Date);
            var open = new NativeSession(1, original.Id, install, at, at, null, at, 0, SessionLifecycle.Open, null, false, 0, Id("fixture-writer"), new(0, null), null);
            var entry = original with { Installations = [new(install, "D:\\SyntheticCheckpoint", "D:\\SyntheticCheckpoint\\fixture.exe", [], "D:\\SyntheticCheckpoint")], PreferredInstallationId = install, Sessions = original.Sessions.Add(open.Project(sessionId)), PlayCount = original.PlayCount + 1 };
            using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec);
            // Seed through the validated native persistence adapter; no process is launched.
            using (var seed = owner.CreateLibraryPersistence()) await seed.SaveAsync(owner.ReadComponent("library.json")!, Codec.Encode([entry], Date));
            await using var library = TransactionalLibrary.Open(Codec, owner.CreateLibraryPersistence());
            var identity = (await library.CaptureLaunchAsync(entry.Id)).Identity;
            open = open with { WriterOwner = library.SessionWriterOwner, Revision = 1 };
            await library.CommitSessionAsync(identity, open.Project(sessionId), recover: true, expectedRevision: 0);
            long beforeSeconds = library.Snapshot.Entries[0].TotalPlaySeconds, beforeCount = library.Snapshot.Entries[0].PlayCount;
            var measured = await Measure("55-second-session-checkpoint", 1, 3, async () =>
            {
                open = open with { Revision = open.Revision + 1, Accumulated100ns = open.Accumulated100ns + 55 * NativeSession.TicksPerSecond, AccountingCutoffAtUtc = at.AddSeconds(55 * open.Revision) };
                await library.CommitSessionAsync(identity, open.Project(sessionId));
                await library.CommitSessionAsync(identity, open.Project(sessionId)); // exactly-once replay, no second delta
            });
            Need(library.Snapshot.Entries[0].TotalPlaySeconds == beforeSeconds + 220 && library.Snapshot.Entries[0].PlayCount == beforeCount, "Checkpoint accounting changed");
            results.Add(new { history = count, nativeHistory = original.Sessions.Count(s => s.Native is not null), measurement = measured, mainBytes = new FileInfo(Path.Combine(root, "library.json")).Length, writtenBytesPerCheckpoint = 2 * new FileInfo(Path.Combine(root, "library.json")).Length, countUnchanged = true, exactlyOnce = true, resources = Resources() });
            Write(Path.Combine(output, "checkpoint-results.json"), results);
        }
    }
    private static async Task Media(string output, string covers)
    {
        var results = new List<object>();
        foreach (int count in libraryCounts)
        {
            string root = Path.Combine(output, "media-" + count); var identity = Generate(root, count, 0, covers);
            using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec);
            await using var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, owner.Configuration()), new LegacyClientTextRules());
            await client.LoadAsync(default); using var http = new HttpClient(new FixtureHandler(covers)); var remote = new RemoteCoverSource(http);
            var source = new CombinedCoverSources(new LocalCoverSources(client), client, remote, remote);
            var passes = new List<object>();
            for (int pass = 0; pass < 3; pass++)
            {
                await using var pipe = new ImagePipeline(source, new WindowsRasterCodec(), new IsolatedThumbnailCache(owner.Configuration()), new());
                var watch = Stopwatch.StartNew(); var resources = new List<object>();
                for (int i = 0; i < count; i += 8)
                {
                    var requests = client.CurrentSnapshot!.Entries.Skip(i).Take(8).Select(e => pipe.LoadAsync(ImageSourceKeys.For(e), 256, default));
                    Need((await Task.WhenAll(requests)).All(r => r.Pixels is not null), "Unique decode failure");
                    if (i % 200 == 0) resources.Add(new { index = i, metrics = pipe.Metrics, resources = Resources() });
                }
                passes.Add(new { pass, warmup = pass == 0, elapsedMs = watch.Elapsed.TotalMilliseconds, metrics = pipe.Metrics, resources, diskBytes = Directory.EnumerateFiles(Path.Combine(root, "native-thumbnails-v1"), "*.thumb").Sum(f => new FileInfo(f).Length) });
                Write(Path.Combine(output, "media-progress.json"), new { count, pass, passes });
            }
            results.Add(new { identity, passes }); Write(Path.Combine(output, "media-results.json"), results);
        }
        var codec = new WindowsRasterCodec(); var edges = new List<object>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(covers, "edge"), "*", SearchOption.AllDirectories))
        {
            try { var raster = await codec.DecodeAsync(await File.ReadAllBytesAsync(file), 256, default); edges.Add(new { file = Path.GetRelativePath(covers, file), accepted = true, raster.Width, raster.Height }); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or System.Runtime.InteropServices.COMException) { edges.Add(new { file = Path.GetRelativePath(covers, file), accepted = false, error = e.GetType().Name }); }
        }
        Write(Path.Combine(output, "edge-results.json"), edges);
    }
    public static async Task<object> Measure(string name, int warmup, int samples, Func<Task> action)
    {
        for (int i = 0; i < warmup; i++) await action();
        var elapsed = new List<double>(); var allocations = new List<long>();
        for (int i = 0; i < samples; i++) { long before = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew(); await action(); elapsed.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes() - before); }
        var sorted = elapsed.Order().ToArray(); return new { name, warmup, samples, milliseconds = elapsed, allocations, median = Percentile(sorted, .5), p95 = Percentile(sorted, .95), min = sorted[0], max = sorted[^1] };
    }
    public static double Percentile(double[] sorted, double fraction) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    public static async Task Soak(string output, string covers, double minutes)
    {
        Need(minutes > 0 && minutes <= 180, "Invalid duration");
        var root = Path.Combine(output, "soak-profile"); var identity = Generate(root, 10000, 0, covers);
        using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec);
        await using var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, owner.Configuration()), new LegacyClientTextRules());
        Need((await client.LoadAsync(default)).Outcome == ClientOutcome.Success, "Load failed");
        using var http = new HttpClient(new FixtureHandler(covers)); var remote = new RemoteCoverSource(http);
        var source = new CombinedCoverSources(new LocalCoverSources(client), client, remote, remote);
        await using var pipeline = new ImagePipeline(source, new WindowsRasterCodec(), new IsolatedThumbnailCache(owner.Configuration()), new());
        await using var vm = new WorkspaceViewModel(client, new IsolatedUiPreferencesStore(owner.Configuration()), owner.Configuration(), new LegacyClientTextRules());
        await vm.InitializeAsync();
        var duration = Stopwatch.StartNew(); long operations = 0; int round = 0; var samples = new List<object>();
        while (duration.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            for (int i = 0; i < 10; i++) { await client.QueryAsync(Queries()[(round + i) % Queries().Length].Query, default); operations++; }
            for (int i = 0; i < 12; i++) { int item = (round * 47 + i) % 10000; var entry = client.CurrentSnapshot!.Entries[item]; var result = await pipeline.LoadAsync(ImageSourceKeys.For(entry), 256, default); Need(result.Pixels is not null, "Soak decode failed"); operations++; }
            await vm.NavigateAsync(WorkspacePage.Details, client.CurrentSnapshot!.Entries[round % 10000].Id, () => Task.FromResult(UnsavedChoice.Discard));
            await vm.NavigateAsync(WorkspacePage.Library, null, () => Task.FromResult(UnsavedChoice.Discard)); operations += 2;
            var obsolete = vm.SetQueryAsync(new("obsolete")); var latest = vm.SetQueryAsync(new("合成作品")); await Task.WhenAll(obsolete, latest); Need(vm.Items.Count == 10000 && !vm.IsSearching, "Stale query overwrite"); operations += 2;
            if (round % 60 == 0) { await new IsolatedUiPreferencesStore(owner.Configuration()).LoadAsync(default); await vm.ChangeAppearanceAsync(vm.Theme, vm.Layout == "grid" ? "list" : "grid"); operations += 2; }
            if (round > 0 && round % 225 == 0) { await Task.Run(owner.CheckpointBackups); operations++; }
            Need(pipeline.Metrics.CachedBytes <= 64L * 1024 * 1024 && pipeline.Metrics.Outstanding == 0 && pipeline.Metrics.ActiveDecodes == 0, "Image resource budget/backlog");
            var sample = new { seconds = duration.Elapsed.TotalSeconds, batch = (int)(duration.Elapsed.TotalMinutes / 15), round, operations, resources = Resources(), image = pipeline.Metrics, errors = 0 };
            samples.Add(sample); File.AppendAllText(Path.Combine(output, "soak-samples.jsonl"), JsonSerializer.Serialize(sample) + Environment.NewLine);
            if (round % 30 == 0) { Console.WriteLine($"soak minutes={duration.Elapsed.TotalMinutes:F1} operations={operations}"); Write(Path.Combine(output, "soak-progress.json"), sample); }
            round++; await Task.Delay(TimeSpan.FromSeconds(4));
        }
        Write(Path.Combine(output, "soak-result.json"), new { status = "SOAK_COMPLETED", identity, durationSeconds = duration.Elapsed.TotalSeconds, batches = Math.Ceiling(duration.Elapsed.TotalMinutes / 15), operations, rounds = round, samples, finalResources = Resources(), finalImages = pipeline.Metrics, workload = "Actual Application/Presentation/media/persistence paths; no XAML virtualization claim", failures = 0 });
    }
    public static object Resources()
    {
        using var p = Process.GetCurrentProcess(); return new { workingSet = p.WorkingSet64, privateBytes = p.PrivateMemorySize64, managedHeap = GC.GetTotalMemory(false), totalAllocated = GC.GetTotalAllocatedBytes(), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), handles = p.HandleCount, threads = p.Threads.Count, user = GetGuiResources(p.Handle, 1), gdi = GetGuiResources(p.Handle, 0), pendingThreadPool = ThreadPool.PendingWorkItemCount };
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, uint flags);
    public static void Write(string file, object value) => File.WriteAllText(file, JsonSerializer.Serialize(value, Json));
    public static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

// Injected transport only; production source registration, response bounds, decoder and caches are unchanged.
public sealed class FixtureHandler(string directory) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        string file = Path.GetFileName(request.RequestUri!.AbsolutePath);
        if (!file.StartsWith("cover-", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(directory, file), cancellation)) };
        response.Content.Headers.ContentType = new(Path.GetExtension(file) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" }); return response;
    }
}
