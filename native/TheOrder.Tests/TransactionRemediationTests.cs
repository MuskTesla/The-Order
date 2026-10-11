using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

// Large fixtures and measurements run without competing test collections.
[CollectionDefinition("Transaction remediation", DisableParallelization = true)]
public sealed class TransactionRemediationCollection;

[Collection("Transaction remediation")]
public sealed class TransactionRemediationTests(ITestOutputHelper output)
{
    private static readonly LegacyLibraryCodec Codec = new();
    private const string Clock = TransactionDifferentialTests.Clock;
    private static LibraryEntry Entry(string id = "a") => new(id, "Current main", false, null, [], null, "planned", null, false, [], [], "", 0, 0, [], Clock, null, null, null, new(false, null));
    private static WorkMetadata Work() => new("200", "Catalog title", "", [], null, null, null, null, "", null, null, [], [], [], [], [], null, null, Clock);
    private static LibraryEntry History(string id, int count, int offset = 0, bool reverse = false, int seconds = 1)
    {
        var sessions = Enumerable.Range(offset, count).Select(i => new PlaySession("s-" + i.ToString(CultureInfo.InvariantCulture), Clock, Clock, seconds));
        return Entry(id) with { Sessions = [.. reverse ? sessions.Reverse() : sessions], TotalPlaySeconds = (long)count * seconds + 1234, PlayCount = count + 17 };
    }
    private static byte[] Compact(params LibraryEntry[] entries) => JsonSerializer.SerializeToUtf8Bytes(new
    { format = "kun-gal-manager", version = 1, exportedAt = Clock, games = entries.Select(LegacyLibraryCodec.Project) }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    [Theory]
    [InlineData("older")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task ValidLargeMainRemainsReadOnlyNeverFallsBackOrWrites(string backupKind)
    {
        var bytes = Compact(History("current-a", 100000), History("current-b", 100000));
        Assert.True(bytes.Length < LegacyLibraryReader.MaximumBytes);
        using var oracle = TransactionDifferentialTests.Oracle();
        var reference = oracle.RootElement.GetProperty("largeInputReference");
        Assert.True(reference.GetProperty("accepted").GetBoolean());
        Assert.Equal(bytes.Length, reference.GetProperty("compactBytes").GetInt32());
        Assert.Equal(200000, reference.GetProperty("sessions").GetInt32());
        using var profile = new TransactionProfile(bytes);
        byte[]? backup = backupKind switch { "older" => Compact(Entry("older")), "corrupt" => "{broken"u8.ToArray(), _ => null };
        if (backup is not null) File.WriteAllBytes(profile.Backup, backup);
        var assessment = Codec.Inspect(bytes, Clock);
        Assert.Equal(LibraryAccess.ValidReadOnly, assessment.Access);
        Assert.Throws<LibraryWriteLimitException>(() => Codec.Encode(assessment.Entries, Clock));
        await using var library = profile.Open(new Probe((_, _) => throw new Xunit.Sdk.XunitException("Read-only entered persistence")));
        Assert.Equal(LibraryRecovery.Main, library.Snapshot.Recovery);
        Assert.Equal(LibraryAccess.ValidReadOnly, library.Snapshot.Access);
        Assert.Equal(new[] { "current-a", "current-b" }, library.Snapshot.Entries.Select(e => e.Id));
        Assert.All(library.Snapshot.Entries, e => Assert.Equal(100000, e.Sessions.Length));
        // Even deleting a missing ID must fail as read-only BEFORE candidate validation.
        var error = await Assert.ThrowsAsync<LibraryReadOnlyException>(() => library.DeleteAsync("missing"));
        Assert.Contains("可读取", error.Message); Assert.Contains("安全保存", error.Message);
        await Assert.ThrowsAsync<LibraryReadOnlyException>(() => library.UpdateAsync("current-a", new() { Notes = "bad" }));
        Assert.Equal(bytes, File.ReadAllBytes(profile.Main));
        Assert.Equal(backup, File.Exists(profile.Backup) ? File.ReadAllBytes(profile.Backup) : null);
        output.WriteLine($"compact main bytes={bytes.Length}; access={library.Snapshot.Access}; backup={backupKind}; sessions=200000");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidWritableAndInvalidInputAreDistinct(bool corruptMain)
    {
        Assert.Equal(LibraryAccess.InvalidInput, Codec.Inspect("{broken"u8.ToArray(), Clock).Access);
        using var p = new TransactionProfile(corruptMain ? "{broken"u8.ToArray() : Compact(Entry()));
        File.WriteAllBytes(p.Backup, Compact(Entry("backup")));
        await using var library = p.Open();
        Assert.Equal(LibraryAccess.ValidWritable, library.Snapshot.Access);
        Assert.Equal(corruptMain ? LibraryRecovery.Backup : LibraryRecovery.Main, library.Snapshot.Recovery);
        await library.UpdateAsync(corruptMain ? "backup" : "a", new() { Notes = "saved" });
        Assert.Equal("saved", Codec.Decode(File.ReadAllBytes(p.Main))[0].Notes);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public async Task DefaultProductionTimestampIsUtcInvariantAcrossCalendars(string culture)
    {
        var previous = CultureInfo.CurrentCulture; var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new(culture); CultureInfo.CurrentUICulture = new(culture);
            var instant = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(8));
            using var p = new TransactionProfile();
            await using var library = TransactionalLibrary.Open(Codec, new IsolatedLibraryPersistence(p.Root), timeProvider: new FixedTime(instant));
            // Inject an instant, NOT a preformatted string: this exercises the default formatter.
            var added = (await library.AddAsync(Work())).Value;
            Assert.Equal(Clock, added.AddedAt); Assert.Equal(Clock, library.Snapshot.Entries[0].AddedAt);
            await library.UpdateAsync(added.Id, new() { Notes = "saved again" });
            foreach (var file in new[] { p.Main, p.Backup })
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                Assert.Equal(Clock, document.RootElement.GetProperty("exportedAt").GetString());
                var game = document.RootElement.GetProperty("games")[0];
                Assert.Equal(Clock, game.GetProperty("addedAt").GetString());
                Assert.Equal(Clock, game.GetProperty("metadata").GetProperty("fetchedAt").GetString());
            }
            output.WriteLine($"culture={culture}; calendar={CultureInfo.CurrentCulture.Calendar.GetType().Name}; persisted={Clock}");
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("durable")]
    [InlineData("queued")]
    [InlineData("cancel-queued")]
    [InlineData("cancel-durable")]
    [InlineData("replaced-warning")]
    [InlineData("candidate")]
    public async Task SingleThreadUiCanCloseWhileKernelDrains(string mode)
    {
        using var p = new TransactionProfile(Compact(Entry()));
        await SingleThreadUi.Run(async () =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var candidateRelease = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var tracked = new CheckedCodec(uiThread);
            var stage = mode == "replaced-warning" ? PersistenceStage.AfterReplace : PersistenceStage.BeforeTempWrite;
            var probe = new Probe(async (target, actual) =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId); Assert.Null(SynchronizationContext.Current);
                if (target == PersistenceTarget.Main && actual == stage && entered.TrySetResult())
                {
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    if (mode == "replaced-warning") throw new IOException("post-commit warning");
                }
            });
            await using var library = TransactionalLibrary.Open(tracked, new IsolatedLibraryPersistence(p.Root, probe), () => Clock);
            tracked.Enabled = true;
            if (mode == "idle") { await library.ShutdownAsync(); Assert.Equal(uiThread, Environment.CurrentManagedThreadId); return; }
            if (mode == "candidate") tracked.BeforeEncode = () => { if (entered.TrySetResult()) Assert.True(candidateRelease.Wait(TimeSpan.FromSeconds(15))); };
            var first = library.UpdateAsync("a", new() { Notes = "first" }, mode == "cancel-durable" ? cancellation.Token : default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Task<CommitResult<LibraryEntry>>? second = mode is "queued" or "cancel-queued" ? library.UpdateAsync("a", new() { Favorite = true }, cancellation.Token) : null;
            var close = library.ShutdownAsync();
            Assert.False(close.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => library.UpdateAsync("a", new() { Notes = "after close" }));
            if (mode.StartsWith("cancel-", StringComparison.Ordinal)) cancellation.Cancel();
            release.TrySetResult(); candidateRelease.Set();
            var receipt = await first;
            if (mode == "cancel-durable") Assert.True(receipt.CancellationAfterCommit);
            Assert.Equal(mode == "replaced-warning", receipt.Warning is not null);
            if (second is not null)
            {
                if (mode == "cancel-queued") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
                else await second;
            }
            await close.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
            Assert.Equal("first", library.Snapshot.Entries[0].Notes);
            Assert.Equal(mode == "queued", library.Snapshot.Entries[0].Favorite);
            Assert.Equal(Codec.Encode(library.Snapshot.Entries, Clock), File.ReadAllBytes(p.Main));
            Assert.True(tracked.WorkerCalls > 0);
            using var reopened = p.Open(); // Close completed only after the writer lease was released.
            Assert.Equal(library.Snapshot.Entries[0].Notes, reopened.Snapshot.Entries[0].Notes);
        }).WaitAsync(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task AsyncCommitRequiresTokenAndRejectsDeletedThenRestoredId()
    {
        var original = Compact(Entry()); using var p = new TransactionProfile(original); await using var library = p.Open();
        var old = await library.BeginRequestAsync("a");
        await library.DeleteAsync("a"); await library.ImportAsync(original); var before = File.ReadAllBytes(p.Main);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), old));
        Assert.Equal(before, File.ReadAllBytes(p.Main)); Assert.Null(library.Snapshot.Entries[0].Metadata);
        var parameter = typeof(TransactionalLibrary).GetMethod(nameof(TransactionalLibrary.CommitLinkAsync))!.GetParameters().Single(p => p.ParameterType == typeof(RequestToken));
        Assert.False(parameter.IsOptional);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("import")]
    [InlineData("link")]
    public async Task UiEntryPointsDoNotNormalizeOrDecodeOnCaller(string operation)
    {
        using var p = new TransactionProfile(Compact(Entry()));
        var imported = Compact(Entry("imported"));
        await SingleThreadUi.Run(async () =>
        {
            var tracked = new CheckedCodec(Environment.CurrentManagedThreadId);
            await using var library = TransactionalLibrary.Open(tracked, new IsolatedLibraryPersistence(p.Root), () => Clock);
            tracked.Enabled = true;
            switch (operation)
            {
                case "add": await library.AddAsync(Work()); break;
                case "import": await library.ImportAsync(imported); break;
                case "link": await library.CommitLinkAsync("a", Work(), await library.BeginRequestAsync("a")); break;
            }
            Assert.True(tracked.WorkerCalls > 0);
            Assert.Equal(Codec.Encode(library.Snapshot.Entries, Clock), File.ReadAllBytes(p.Main));
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CancellationDuringCandidateThenShutdownDoesNotPersist()
    {
        var original = Compact(Entry()); using var p = new TransactionProfile(original);
        await SingleThreadUi.Run(async () =>
        {
            var tracked = new CheckedCodec(Environment.CurrentManagedThreadId);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
            await using var library = TransactionalLibrary.Open(tracked, new IsolatedLibraryPersistence(p.Root), () => Clock);
            tracked.Enabled = true;
            tracked.BeforeEncode = () => { if (entered.TrySetResult()) Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
            var mutation = library.UpdateAsync("a", new() { Notes = "cancelled" }, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var shutdown = library.ShutdownAsync(); cancellation.Cancel(); release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation); await shutdown;
            Assert.Equal(original, File.ReadAllBytes(p.Main)); Assert.False(File.Exists(p.Backup)); Assert.Equal("", library.Snapshot.Entries[0].Notes);
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("commit")]
    [InlineData("both")]
    public async Task CancelledRequestEventuallyClearsPendingWithoutCancellingNewerRequest(string mode)
    {
        using var p = new TransactionProfile(Compact(Entry(), Entry("b") with { Metadata = Work() })); await using var library = p.Open();
        using var cancellation = new CancellationTokenSource();
        var target = await library.BeginRequestAsync("b", mode == "commit" ? default : cancellation.Token); cancellation.Cancel();
        if (mode != "request") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.CommitLinkAsync("b", Work(), target, cancellation: cancellation.Token));
        // The cleanup was admitted by cancellation before this barrier; a pending target would reject.
        await library.LinkAsync("a", Work()); Assert.Single(library.Snapshot.Entries);
        var old = await library.BeginRequestAsync("a"); var fresh = await library.BeginRequestAsync("a");
        await library.FinishRequestAsync(old, new(true));
        await library.CommitLinkAsync("a", Work(), fresh);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("disjoint")]
    [InlineData("partial")]
    [InlineData("reverse")]
    [InlineData("conflict")]
    [InlineData("duplicate-input")]
    public async Task EightyThousandSessionMergePreservesOrderTotalsAndLimits(string shape)
    {
        var a = History("a", 80000);
        var b = History("b", 80000, shape == "disjoint" ? 80000 : shape == "partial" ? 20000 : 0, shape == "reverse", 2) with { Metadata = Work() };
        if (shape == "conflict") b = b with { Sessions = b.Sessions.SetItem(0, b.Sessions[0] with { StartedAt = "2026-10-03T00:00:00.000Z" }) };
        if (shape == "duplicate-input") b = b with { Sessions = b.Sessions.SetItem(1, b.Sessions[0]) };
        var bytes = Compact(a, b); using var p = new TransactionProfile(bytes);
        if (shape == "duplicate-input") { Assert.Throws<InvalidDataException>(() => p.Open()); Assert.Equal(bytes, File.ReadAllBytes(p.Main)); return; }
        await using var library = p.Open(); Assert.Equal(LibraryAccess.ValidWritable, library.Snapshot.Access);
        if (shape is "disjoint" or "conflict")
        {
            // 160k disjoint union violates the unchanged 100k-per-entry contract.
            await Assert.ThrowsAsync<InvalidDataException>(() => library.LinkAsync("a", Work()));
            Assert.Equal(bytes, File.ReadAllBytes(p.Main)); Assert.False(File.Exists(p.Backup)); Assert.Equal(2, library.Snapshot.Entries.Length); return;
        }
        var timer = Stopwatch.StartNew(); await library.LinkAsync("a", Work()); timer.Stop();
        var e = Assert.Single(library.Snapshot.Entries); int overlap = shape == "partial" ? 60000 : 80000;
        Assert.Equal(160000 - overlap, e.Sessions.Length);
        Assert.Equal(a.TotalPlaySeconds + b.TotalPlaySeconds - overlap, e.TotalPlaySeconds);
        Assert.Equal(a.PlayCount + b.PlayCount - overlap, e.PlayCount);
        Assert.Equal(Enumerable.Range(0, e.Sessions.Length).Select(i => "s-" + i.ToString(CultureInfo.InvariantCulture)), e.Sessions.Select(s => s.Id));
        Assert.Equal(2, e.Sessions[^1].Seconds);
        Assert.Equal(Codec.Encode(library.Snapshot.Entries, Clock), File.ReadAllBytes(p.Main));
        output.WriteLine($"merge shape={shape}; source=80000; target=80000; inputBytes={bytes.Length}; elapsedMs={timer.Elapsed.TotalMilliseconds:F1}");
    }

    [Fact, Trait("Category", "Performance")]
    public async Task WarmedMultiRunOrdinaryTransactionAndMergeGrowth()
    {
        var ordinary = Compact(Enumerable.Range(0, 10000).Select(i => Entry("g-" + i)).ToArray());
        await Measure("ordinary-10000", ordinary, l => l.UpdateAsync("g-5000", new() { Notes = "benchmark" }));
        foreach (var size in new[] { 20000, 40000, 80000 })
            await Measure($"overlap-{size}+{size}", Compact(History("a", size), History("b", size, reverse: true, seconds: 2) with { Metadata = Work() }), l => l.LinkAsync("a", Work()));
        // Growth is recorded, not gated by a noisy absolute timing threshold.
    }
    private async Task Measure(string label, byte[] input, Func<TransactionalLibrary, Task> operation)
    {
        var measured = new List<double>();
        for (int round = 0; round < 4; round++)
        {
            using var p = new TransactionProfile(input); await using var library = p.Open();
            var timer = Stopwatch.StartNew(); await operation(library); timer.Stop();
            if (round > 0) measured.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(Codec.Encode(library.Snapshot.Entries, Clock), File.ReadAllBytes(p.Main));
        }
        measured.Sort(); output.WriteLine($"PERF {label}: warmup=1 measured=3 bytes={input.Length} medianMs={measured[1]:F1} minMs={measured[0]:F1} maxMs={measured[2]:F1}");
    }

    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider { public override DateTimeOffset GetUtcNow() => instant; }
    private sealed class Probe(Func<PersistenceTarget, PersistenceStage, Task> callback) : IPersistenceProbe
    { public Task ReachAsync(PersistenceTarget target, PersistenceStage stage) => callback(target, stage); }
    private sealed class CheckedCodec(int uiThread) : ILibraryCodec
    {
        public bool Enabled { get; set; }
        public int WorkerCalls { get; private set; }
        public Action? BeforeEncode { get; set; }
        private void Check() { if (!Enabled) return; Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId); Assert.Null(SynchronizationContext.Current); WorkerCalls++; }
        public ImmutableArray<LibraryEntry> Decode(ReadOnlyMemory<byte> bytes) { Check(); return Codec.Decode(bytes); }
        public byte[] Encode(ImmutableArray<LibraryEntry> entries, string timestamp) { Check(); BeforeEncode?.Invoke(); return Codec.Encode(entries, timestamp); }
        public LibraryDocument Inspect(ReadOnlyMemory<byte> bytes, string timestamp) { Check(); return Codec.Inspect(bytes, timestamp); }
        public string EvidenceIdentity(LibraryEntry e) { Check(); return Codec.EvidenceIdentity(e); }
        public string InstallationKey(string path) { Check(); return Codec.InstallationKey(path); }
        public bool ProtectsName(LibraryEntry e) { Check(); return Codec.ProtectsName(e); }
        public WorkMetadata NormalizeWork(WorkMetadata work) { Check(); return Codec.NormalizeWork(work); }
    }
    private sealed class SingleThreadUi : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> messages = new();
        public override void Post(SendOrPostCallback d, object? state) => messages.Add((d, state));
        public static Task Run(Func<Task> action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                var context = new SingleThreadUi(); SetSynchronizationContext(context);
                context.Post(async _ =>
                {
                    try { await action(); done.TrySetResult(); }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { context.messages.CompleteAdding(); }
                }, null);
                foreach (var (callback, state) in context.messages.GetConsumingEnumerable()) callback(state);
                context.messages.Dispose();
            }) { IsBackground = true, Name = "Synthetic single-thread UI" }.Start();
            return done.Task;
        }
    }
}
