using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
using static TheOrder.Tests.RecognitionRemediationTests;
using static TheOrder.Tests.RecognitionReviewTests;

namespace TheOrder.Tests;

public sealed class RecognitionFinalGateTests
{
    private static ExecutableEvidence Parse(byte[] bytes) => new ExecutableEvidenceParser().Read(new MemoryStream(bytes), "Game.exe", default);
    private static WindowsRecognitionEvidence WindowsReader() => new(new LocalEvidenceParser(), RecognitionFingerprint.Evidence, new ExecutableEvidenceParser());
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Rank(RecognitionEvidence evidence) => Json(new ReviewSafeRanking().Rank(evidence, [Work(title: "航路第1章")]));
    private static byte[] Languages((uint Id, byte[] Pe)[] records)
    {
        var payloads = records.Select(r => r.Pe.AsSpan(0x500, BinaryPrimitives.ReadUInt16LittleEndian(r.Pe.AsSpan(0x500))).ToArray()).ToArray();
        int length = 0x800 + payloads.Sum(p => (p.Length + 3) & ~3), raw = (length - 0x400 + 511) & ~511;
        var bytes = new byte[0x400 + raw]; Pe().AsSpan(0, 0x400).CopyTo(bytes);
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        U32(0x98 + 116, (uint)(length - 0x400)); U32(0x178 + 8, (uint)(length - 0x400)); U32(0x178 + 16, (uint)raw);
        U16(0x40e, 1); U32(0x410, 16); U32(0x414, 0x80000020); U16(0x42e, 1); U32(0x430, 1); U32(0x434, 0x80000040); U16(0x44e, records.Length);
        // Payload placement follows language identity, independently of directory enumeration.
        var offsets = new Dictionary<uint, int>(); int next = 0x800;
        foreach (var i in Enumerable.Range(0, records.Length).OrderBy(i => records[i].Id))
        { offsets.Add(records[i].Id, next); payloads[i].CopyTo(bytes, next); next += (payloads[i].Length + 3) & ~3; }
        for (int i = 0; i < records.Length; i++)
        {
            int data = 0x600 + i * 16; U32(0x450 + i * 8, records[i].Id); U32(0x454 + i * 8, (uint)(data - 0x400));
            U32(data, (uint)(0x1000 + offsets[records[i].Id] - 0x400)); U32(data + 4, (uint)payloads[i].Length);
        }
        return bytes;
    }

    // Mutate an independent PE fixture, not a second implementation of parsing/canonicalization.
    private static byte[] Chapters(bool reverse)
    {
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/recognition-evidence-oracle.json")));
        var bytes = Convert.FromBase64String(oracle.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "languages").GetProperty("bytes").GetString()!);
        var needle = Encoding.Unicode.GetBytes("星空の航路"); int found = 0;
        for (int i = 0; i <= bytes.Length - needle.Length; i++)
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
            { Encoding.Unicode.GetBytes(++found == 1 ? "航路第1章" : "航路第2章").CopyTo(bytes, i); i += needle.Length - 1; }
        Assert.Equal(2, found);
        if (reverse) { var first = bytes.AsSpan(0x450, 8).ToArray(); bytes.AsSpan(0x458, 8).CopyTo(bytes.AsSpan(0x450)); first.CopyTo(bytes, 0x458); }
        return bytes;
    }

    [Fact] public void ResourcePermutationHasIdenticalEffectiveEvidenceDigestAndRanking()
    {
        var a = Parse(Chapters(false)); var b = Parse(Chapters(true));
        Assert.Equal(a.EvidenceDigest, b.EvidenceDigest); Assert.Equal(Json(a.Evidence), Json(b.Evidence));
        Assert.Equal(Rank(Evidence([.. a.Evidence.Titles])), Rank(Evidence([.. b.Evidence.Titles])));
        Assert.NotEqual(a.Fingerprint, b.Fingerprint); // Legacy raw resource order is intentionally retained.
    }

    [Fact] public void MoreThanFortyRawTitlesAreCanonicalBeforePerExecutableBound()
    {
        var fields = Enumerable.Range(0, 64).Select(i => ("ProductName", $"航路第{i:00}章"))
            .Concat(new[] { ("FileDescription", "航路第01章"), ("ProductName", "航路第01章"), ("CompanyName", "Studio B"), ("CompanyName", "Studio A") }).ToArray();
        byte[] Container((string, string)[] f) => Languages([ (1041, Pe(f[..20])), (1033, Pe(f[20..40])), (2052, Pe(f[40..])) ]);
        var expected = Parse(Container(fields)); Assert.Equal(16, expected.Evidence.Titles.Length);
        var random = new Random(41011);
        for (int run = 0; run < 20; run++)
        {
            var first = fields[..20]; var second = fields[20..40]; var third = fields[40..]; random.Shuffle(first); random.Shuffle(second); random.Shuffle(third);
            var languages = new[] { (1041u, Pe(first)), (1033u, Pe(second)), (2052u, Pe(third)) }; random.Shuffle(languages);
            var actual = Parse(Languages(languages));
            Assert.Equal(expected.EvidenceDigest, actual.EvidenceDigest);
            Assert.Equal(Json(expected.Evidence), Json(actual.Evidence));
            Assert.Equal(Rank(Evidence([.. expected.Evidence.Titles])), Rank(Evidence([.. actual.Evidence.Titles])));
        }
        var changed = fields.ToArray(); changed[0] = ("ProductName", "另一作品第99章");
        Assert.NotEqual(expected.EvidenceDigest, Parse(Container(changed)).EvidenceDigest);
        Assert.NotEqual(Json(expected.Evidence), Json(Parse(Container(changed)).Evidence));
    }

    [Fact] public void LanguageSelectionIsCanonicalBeforeResourceLimitWhileLegacyHashKeepsOriginalSelection()
    {
        var resources = Enumerable.Range(1, 6).Select(i => ((uint)(1000 + i), Pe(("ProductName", $"航路第{i}章")))).ToArray();
        var forward = Parse(Languages(resources)); var reverse = Parse(Languages(resources.Reverse().ToArray()));
        Assert.Equal(4, forward.Evidence.Titles.Length); Assert.Equal(Json(forward.Evidence), Json(reverse.Evidence));
        Assert.Equal(forward.EvidenceDigest, reverse.EvidenceDigest); Assert.NotEqual(forward.Fingerprint, reverse.Fingerprint);
        Assert.Equal(Parse(Languages(resources.Take(4).ToArray())).Fingerprint, forward.Fingerprint);
        Assert.Equal(Parse(Languages(resources.Reverse().Take(4).ToArray())).Fingerprint, reverse.Fingerprint);
    }

    [Fact] public async Task AggregateFortyTitleBoundaryIsStableAcrossPePermutations()
    {
        using var profile = new TransactionProfile(); var folder = Path.Combine(profile.Root, "game"); Directory.CreateDirectory(folder);
        // No selected executable: all three bounded executable contributions are consumed.
        var entry = Entry("game", folder) with { Installations = [new("i", folder, null, [])] };
        var random = new Random(41012); string? input = null, digest = null, ranking = null;
        for (int run = 0; run < 8; run++)
        {
            for (int exe = 0; exe < 3; exe++)
            {
                var fields = Enumerable.Range(exe * 16, 16).Select(i => ("ProductName", $"航路第{i:00}章")).ToArray();
                var first = fields[..8]; var second = fields[8..]; random.Shuffle(first); random.Shuffle(second);
                var languages = new[] { (1041u, Pe(first)), (1033u, Pe(second)) }; random.Shuffle(languages);
                File.WriteAllBytes(Path.Combine(folder, $"Game{exe}.exe"), Languages(languages));
            }
            var actual = await WindowsReader().ReadAsync(entry, true, default); Assert.Equal(40, actual.Evidence.Titles.Length);
            if (run == 0) { input = Json(actual.Evidence.Titles); digest = actual.Fingerprint; ranking = Rank(actual.Evidence); }
            Assert.Equal(input, Json(actual.Evidence.Titles)); Assert.Equal(digest, actual.Fingerprint); Assert.Equal(ranking, Rank(actual.Evidence));
        }
        File.WriteAllBytes(Path.Combine(folder, "Game0.exe"), Pe(("ProductName", "不同作品第00章")));
        Assert.NotEqual(digest, (await WindowsReader().ReadAsync(entry, true, default)).Fingerprint);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ThirtyNineFoldersAndChapterBoundaryUsesActualWindowsReaderAndApply(bool mutateTitle)
    {
        using var files = new TransactionProfile();
        var installations = Enumerable.Range(0, 39).Select(i => {
            var folder = Path.Combine(files.Root, i.ToString(), "game"); Directory.CreateDirectory(folder);
            return new Installation(i.ToString(), folder, Path.Combine(folder, "Game.exe"), []);
        }).ToImmutableArray();
        var entry = Entry("game", installations[0].Folder) with { Installations = installations, PreferredInstallationId = "0" };
        string exe = installations[0].Executable!; File.WriteAllBytes(exe, Chapters(false));
        var physical = new WindowsFilesystemIdentity().Inspect(exe, false).Observation!.Identity;
        var reader = WindowsReader(); var before = await reader.ReadAsync(entry, true, default);
        Assert.Equal(40, before.Evidence.Titles.Length); Assert.Equal("航路第1章", before.Evidence.Titles[^1].Value);
        await using var lab = new Lab(entry: entry, reader: reader); lab.Catalog.Works = [Work(title: "航路第1章")]; await lab.Init();
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true, true); await lab.Queue.WhenIdleAsync();
        Assert.False(Assert.Single(lab.Queue.RequireCurrentResult(id).Candidates).Vetoed);
        var bytes = Chapters(true);
        if (mutateTitle)
        {
            var needle = Encoding.Unicode.GetBytes("航路第1章");
            for (int i = 0; i <= bytes.Length - needle.Length; i++)
                if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle)) Encoding.Unicode.GetBytes("航路第3章").CopyTo(bytes, i);
        }
        File.WriteAllBytes(exe, bytes); Assert.Equal(physical, new WindowsFilesystemIdentity().Inspect(exe, false).Observation!.Identity);
        var after = await reader.ReadAsync(entry, true, default);
        var library = File.ReadAllBytes(Path.Combine(lab.Profile.Root, "library.json"));
        var decision = await lab.Reviews.ApplyAsync(id, "200", default);
        if (mutateTitle)
        {
            Assert.NotEqual(before.Fingerprint, after.Fingerprint); Assert.False(decision.IsCommitted);
            Assert.Equal(library, File.ReadAllBytes(Path.Combine(lab.Profile.Root, "library.json")));
            Assert.Equal(RecognitionReviewStatus.Stale, Assert.Single(lab.Reviews.Snapshot).Status);
        }
        else
        {
            Assert.Equal(before.Fingerprint, after.Fingerprint); Assert.Equal(Json(before.Evidence.Titles), Json(after.Evidence.Titles));
            Assert.Equal(Rank(before.Evidence), Rank(after.Evidence)); Assert.True(decision.IsCommitted);
        }
    }

    private sealed class PausedStorage(byte[]? bytes) : IRecognitionReviewStorage
    {
        public byte[]? Bytes = bytes;
        public int Reads, Writes;
        public bool Fail;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RecognitionReviewDocuments> ReadAsync(CancellationToken c)
        { Interlocked.Increment(ref Reads); Entered.TrySetResult(); await Release.Task.WaitAsync(c); if (Fail) throw new IOException("synthetic read failure"); return new(Bytes, null); }
        public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken c)
        { c.ThrowIfCancellationRequested(); Bytes = value.ToArray(); Interlocked.Increment(ref Writes); return Task.CompletedTask; }
    }
    private static RecognitionReview History(int i, bool large = false) => new(new Guid(i, 0, 0, new byte[8]), "a", "history", DateTimeOffset.UnixEpoch.AddSeconds(i),
        RecognitionReviewStatus.Stale, "old-algorithm", null,
        large ? [.. Enumerable.Range(1, 5).Select(n => new HistoricalRecognitionCandidate(n.ToString(), new string('中', 2000), new string('文', 2000), .5, new string('字', 2000)))] : [], Message: "old");
    private static RecognitionReviewService Service(Lab lab, IRecognitionReviewStorage store, IRecognitionReviewCodec? codec = null) =>
        new(lab.Offline, lab.Queue, store, codec ?? new RecognitionReviewCodec(), lab.Reader, new ReviewSafeRanking(), lab.Catalog, lab.Credentials.AuthContext, RecognitionFingerprint.Identity);
    private static void Legal(RecognitionReviewService service)
    { var snapshot = service.Snapshot; Assert.InRange(snapshot.Length, 0, 10000); Assert.InRange(new RecognitionReviewCodec().Write(snapshot).Length, 1, 16 * 1024 * 1024); }

    [Theory][InlineData("ignore")][InlineData("apply")]
    public async Task PausedNearSixteenMiBLoadAndLiveResultNeverPublishOverBudget(string action)
    {
        var codec = new RecognitionReviewCodec(); var initial = codec.Write(codec.PrepareAdmission([.. Enumerable.Range(1, 256).Select(i => History(i, true))]));
        Assert.InRange(initial.Length, 16_000_000, 16 * 1024 * 1024);
        var store = new PausedStorage(initial); await using var lab = new Lab(); await lab.Offline.LoadAsync(default);
        lab.Catalog.Works = [Work() with { Description = new string('x', 1_000_000) }];
        await using var service = Service(lab, store); var observed = new ConcurrentQueue<Exception>();
        service.Changed += () => { try { Legal(service); } catch (Exception e) { observed.Enqueue(e); } };
        var initialize = service.InitializeAsync(default); await store.Entered.Task;
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        Assert.Empty(service.Snapshot); Assert.Equal(ReviewInitializationState.Initializing, service.InitializationState);
        await service.FlushAsync(); Assert.Equal(0, store.Writes); Assert.Equal(initial, store.Bytes);
        store.Release.SetResult(); await initialize; Legal(service); Assert.Equal(ReviewInitializationState.Ready, service.InitializationState);
        Assert.Contains(service.Snapshot, r => r.Id == id && r.Status == RecognitionReviewStatus.NeedsReview);
        Assert.True(service.Snapshot.Length < codec.Read(new(initial, null)).Length); Assert.Null(service.CapacityError);
        // The queue codec permits a large suggestion, whereas the authoritative library has a
        // smaller description limit. Fresh detail uses a valid library payload for this Apply.
        if (action == "apply") lab.Catalog.Works = [Work()];
        var result = action == "apply" ? await service.ApplyAsync(id, "200", default) : await service.IgnoreAsync(id, default);
        Assert.True(action == "apply" ? result.IsCommitted : result.Outcome == ClientOutcome.Success, result.Outcome + ": " + result.Message);
        await service.FlushAsync(); Legal(service); Assert.Empty(observed); Assert.InRange(store.Bytes!.Length, 1, 16 * 1024 * 1024);
    }

    [Fact] public async Task UnfitLiveAdmissionPreservesLegalRestoredStateAndReportsCapacity()
    {
        var codec = new RecognitionReviewCodec(); var bytes = codec.Write([History(1)]); var store = new PausedStorage(bytes);
        await using var lab = new Lab(); await lab.Offline.LoadAsync(default);
        lab.Catalog.Works = [Work() with { Description = new string('x', 17 * 1024 * 1024) }];
        await using var service = Service(lab, store); var initialize = service.InitializeAsync(default); await store.Entered.Task;
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); Assert.Empty(service.Snapshot);
        store.Release.SetResult(); await initialize; Legal(service);
        Assert.Equal(Json(codec.PrepareAdmission(codec.Read(new(bytes, null)))), Json(service.Snapshot));
        Assert.Contains("ReviewCapacity", service.CapacityError); Assert.Throws<StaleLibraryRequestException>(() => lab.Queue.RequireCurrentResult(id));
        Assert.Equal(ClientOutcome.Success, (await service.IgnoreAsync(History(1).Id, default)).Outcome);
    }

    [Fact] public async Task TenThousandHistoryAndLatestGenerationsMergeOnceAfterPausedRead()
    {
        var codec = new RecognitionReviewCodec(); var store = new PausedStorage(codec.Write([.. Enumerable.Range(1, 10000).Select(i => History(i))]));
        await using var lab = new Lab(); await lab.Offline.LoadAsync(default);
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([Entry(folder: @"C:\synthetic\other") with { Id = "b" }], Clock));
        await using var service = Service(lab, store); var initialize = service.InitializeAsync(default); await store.Entered.Task;
        var secondInitialize = service.InitializeAsync(default);
        var old = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        var b = await lab.Queue.EnqueueAsync("b", RecognitionMode.Reidentify, true);
        var latest = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        Assert.Empty(service.Snapshot); store.Release.SetResult(); await Task.WhenAll(initialize, secondInitialize);
        Legal(service); Assert.Equal(1, store.Reads); Assert.Equal(258, service.Snapshot.Length);
        Assert.DoesNotContain(service.Snapshot, r => r.Id == old); Assert.Contains(service.Snapshot, r => r.Id == latest); Assert.Contains(service.Snapshot, r => r.Id == b);
        Assert.Equal(Enumerable.Range(9745, 256), service.Snapshot.Where(r => r.Result is null).OrderBy(r => r.CreatedAt).Select(r => (int)(r.CreatedAt - DateTimeOffset.UnixEpoch).TotalSeconds));
        Assert.Equal(service.Snapshot.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id), service.Snapshot);
        Assert.Throws<StaleLibraryRequestException>(() => lab.Queue.RequireCurrentResult(old));
    }

    [Fact] public async Task ReadyStateProtectsAcceptedActiveResultWhenNextResultExceedsRealByteBudget()
    {
        var store = new PausedStorage(null); store.Release.SetResult(); await using var lab = new Lab(); await lab.Offline.LoadAsync(default);
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([Entry(folder: @"C:\synthetic\other") with { Id = "b" }], Clock));
        lab.Catalog.Works = [Work() with { Description = new string('x', 9 * 1024 * 1024) }];
        await using var service = Service(lab, store); await service.InitializeAsync(default);
        var first = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        // Queue idle is signalled just before observer delivery; synchronize with the service.
        for (int i = 0; i < 500 && service.Snapshot.IsEmpty; i++) await Task.Delay(10);
        var before = Json(service.Snapshot); Assert.Equal(first, Assert.Single(service.Snapshot).Id);
        var next = await lab.Queue.EnqueueAsync("b", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        for (int i = 0; i < 500 && service.CapacityError is null; i++) await Task.Delay(10);
        Legal(service); Assert.Equal(before, Json(service.Snapshot)); Assert.Contains("ReviewCapacity", service.CapacityError);
        Assert.Equal(first, lab.Queue.RequireCurrentResult(first).Generation); Assert.Throws<StaleLibraryRequestException>(() => lab.Queue.RequireCurrentResult(next));
    }

    [Theory][InlineData("io")][InlineData("corrupt")]
    public async Task InitializationFailurePreservesFileAndPublishesOnlyValidatedLiveResults(string fault)
    {
        var bytes = Encoding.UTF8.GetBytes("{broken"); var store = new PausedStorage(bytes) { Fail = fault == "io" };
        await using var lab = new Lab(); await lab.Offline.LoadAsync(default); await using var service = Service(lab, store);
        var initialize = service.InitializeAsync(default); await store.Entered.Task;
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); Assert.Empty(service.Snapshot);
        store.Release.SetResult(); await initialize; Legal(service);
        Assert.Equal(ReviewInitializationState.Failed, service.InitializationState); Assert.Equal(id, Assert.Single(service.Snapshot).Id);
        await Assert.ThrowsAsync<IOException>(() => service.FlushAsync()); Assert.Equal(bytes, store.Bytes); Assert.Equal(0, store.Writes);
        await service.InitializeAsync(default); Assert.Equal(1, store.Reads); // Defined degraded, memory-only state.
    }

    [Fact] public async Task CancelledInitializationPublishesNothingAndRetryUsesLatestQueueState()
    {
        var store = new PausedStorage(new RecognitionReviewCodec().Write([History(1)])); await using var lab = new Lab(); await lab.Offline.LoadAsync(default);
        await using var service = Service(lab, store); using var cancel = new CancellationTokenSource();
        var initialize = service.InitializeAsync(cancel.Token); await store.Entered.Task;
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize); Assert.Empty(service.Snapshot); Assert.Equal(ReviewInitializationState.Uninitialized, service.InitializationState);
        lab.Queue.Cancel(id); store.Release.SetResult(); await service.InitializeAsync(default); Legal(service);
        Assert.DoesNotContain(service.Snapshot, r => r.Id == id); Assert.Equal(2, store.Reads);
    }
}
