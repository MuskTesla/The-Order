using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

public sealed class TransactionSafetyTests(ITestOutputHelper output)
{
    private static LegacyLibraryCodec Codec => TransactionDifferentialTests.Codec;
    private const string Clock = TransactionDifferentialTests.Clock;
    private static LibraryEntry Entry(string id = "a") => new(id, "Ordinary title", false, null, [], null, "planned", null, false, [], [], "", 0, 0, [], Clock, null, null, null, new(false, null));
    private static byte[] Document(params LibraryEntry[] entries) => Codec.Encode([.. entries], Clock);
    private static WorkMetadata Work(string title = "new title", string id = "200") => new(id, title, "", [], null, null, null, null, "", null, null, [], [], [], [], [], null, null, Clock);
    private static Task Change(TransactionalLibrary library, string notes = "new") => library.UpdateAsync("a", new() { Notes = notes });

    public static IEnumerable<object[]> Faults() => Enum.GetValues<PersistenceTarget>().SelectMany(t => Enum.GetValues<PersistenceStage>()
        .Where(s => s != PersistenceStage.BeforeBackup || t == PersistenceTarget.Backup).Select(s => new object[] { t, s }));
    [Theory, MemberData(nameof(Faults)), Trait("Category", "FaultInjection")]
    public async Task EveryPersistenceBoundaryRetainsOneAuthoritativeVersion(PersistenceTarget target, PersistenceStage stage)
    {
        var original = Document(Entry());
        using var p = new TransactionProfile(original);
        var probe = new CallbackProbe((t, s) => { if (t == target && s == stage) throw new IOException("simulated disk/replace failure"); return Task.CompletedTask; });
        using (var library = p.Open(probe))
        {
            if (stage == PersistenceStage.AfterReplace)
            {
                var receipt = await library.UpdateAsync("a", new() { Notes = "new" });
                Assert.Equal("new", library.Snapshot.Entries[0].Notes);
                Assert.NotNull(receipt.Warning);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => Change(library));
                Assert.Equal("", library.Snapshot.Entries[0].Notes);
                Assert.Equal(original, File.ReadAllBytes(p.Main));
            }
            Assert.Equal(library.Snapshot.Entries[0].Notes, Codec.Decode(File.ReadAllBytes(p.Main))[0].Notes);
            Assert.Empty(Directory.GetFiles(p.Root, "*.tmp"));
            if (File.Exists(p.Backup)) Assert.Equal("", Codec.Decode(File.ReadAllBytes(p.Backup))[0].Notes);
        }
        using var reopened = p.Open(); Assert.Equal(stage == PersistenceStage.AfterReplace ? "new" : "", reopened.Snapshot.Entries[0].Notes);
    }
    [Theory]
    [InlineData("valid", "valid", "Main")]
    [InlineData("corrupt", "valid", "Backup")]
    [InlineData("missing", "valid", "Backup")]
    [InlineData("corrupt", "corrupt", "error")]
    [InlineData("missing", "missing", "New")]
    [InlineData("valid", "corrupt", "Main")]
    [InlineData("corrupt", "missing", "error")]
    [InlineData("missing", "corrupt", "error")]
    [InlineData("empty", "valid", "Main")]
    [InlineData("invalid-schema", "valid", "Backup")]
    [InlineData("invalid-schema", "missing", "error")]
    [InlineData("missing", "invalid-schema", "error")]
    public async Task RecoveryNeverRewritesWhileOpening(string main, string backup, string expected)
    {
        using var p = new TransactionProfile();
        byte[]? Bytes(string state, string id) => state switch { "missing" => null, "corrupt" => Encoding.UTF8.GetBytes("{broken"), "invalid-schema" => Encoding.UTF8.GetBytes("{\"format\":\"other\",\"version\":1,\"games\":[]}"), "empty" => Document(), _ => Document(Entry(id)) };
        var a = Bytes(main, "main"); var b = Bytes(backup, "backup");
        if (a is not null) File.WriteAllBytes(p.Main, a);
        if (b is not null) File.WriteAllBytes(p.Backup, b);
        if (expected == "error") Assert.Throws<InvalidDataException>(() => p.Open());
        else
        {
            using var library = p.Open(); Assert.Equal(expected, library.Snapshot.Recovery.ToString());
            Assert.Equal(main == "empty" || expected == "New" ? 0 : 1, library.Snapshot.Entries.Length);
            Assert.Equal(a, File.Exists(p.Main) ? File.ReadAllBytes(p.Main) : null);
            Assert.Equal(b, File.Exists(p.Backup) ? File.ReadAllBytes(p.Backup) : null);
            if (expected == "Backup")
            {
                await library.UpdateAsync("backup", new() { Notes = "explicit save" });
                Assert.Equal("explicit save", Codec.Decode(File.ReadAllBytes(p.Main))[0].Notes);
                Assert.Equal("", Codec.Decode(File.ReadAllBytes(p.Backup))[0].Notes);
            }
        }
    }
    [Fact]
    public async Task FirstSaveOverwriteAndInterruptedTempUseOnlyLastGood()
    {
        using var p = new TransactionProfile(); var orphan = Path.Combine(p.Root, "library.json.interrupted.tmp");
        File.WriteAllText(orphan, "{partial"); File.WriteAllText(p.Backup, "{old corrupt backup");
        Assert.Throws<InvalidDataException>(() => p.Open()); File.Delete(p.Backup);
        using var library = p.Open(); Assert.False(File.Exists(p.Main));
        var e = (await library.AddAsync(Work())).Value;
        Assert.Empty(Codec.Decode(File.ReadAllBytes(p.Backup)));
        await library.UpdateAsync(e.Id, new() { Notes = "second" });
        Assert.Equal("", Codec.Decode(File.ReadAllBytes(p.Backup))[0].Notes);
        Assert.Equal("second", Codec.Decode(File.ReadAllBytes(p.Main))[0].Notes);
        Assert.Equal("{partial", File.ReadAllText(orphan));
    }
    [Fact]
    public async Task OverlappingMutationsComposeAgainstLatestCommittedSnapshot()
    {
        using var p = new TransactionProfile(Document(Entry())); using var gate = new CommitGate(); using var library = p.Open(gate);
        var first = library.UpdateAsync("a", new() { Notes = "first" }); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = library.UpdateAsync("a", new() { Favorite = true });
        var third = library.UpdateAsync("a", new() { Rating = 0d, Categories = ImmutableArray.Create("personal") });
        Assert.False(second.IsCompleted); Assert.Equal("", library.Snapshot.Entries[0].Notes);
        gate.Release.TrySetResult(); await Task.WhenAll(first, second, third);
        var e = library.Snapshot.Entries[0]; Assert.Equal("first", e.Notes); Assert.True(e.Favorite); Assert.Equal(0d, e.PersonalRating); Assert.Equal(new[] { "personal" }, e.Categories.ToArray());
        Assert.Equal(Codec.Encode(library.Snapshot.Entries, Clock), File.ReadAllBytes(p.Main));
    }
    [Fact]
    public async Task FailedQueuedMutationDoesNotPoisonFollowingWrites()
    {
        using var p = new TransactionProfile(Document(Entry())); using var gate = new CommitGate(); using var library = p.Open(gate);
        var first = Change(library); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bad = library.UpdateAsync("a", new() { Status = "invalid" }); var good = library.UpdateAsync("a", new() { Favorite = true });
        gate.Release.TrySetResult(); await first; await Assert.ThrowsAsync<InvalidDataException>(() => bad); await good;
        Assert.True(library.Snapshot.Entries[0].Favorite); Assert.Equal("new", library.Snapshot.Entries[0].Notes);
    }
    [Fact]
    public async Task PreCancelledMutationDoesNotCreateBackup()
    {
        using var p = new TransactionProfile(Document(Entry())); using var library = p.Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.UpdateAsync("a", new() { Notes = "bad" }, new(true)));
        Assert.False(File.Exists(p.Backup)); Assert.Equal("", library.Snapshot.Entries[0].Notes);
    }
    [Fact]
    public async Task QueuedCancellationSkipsOnlyCancelledOperation()
    {
        using var p = new TransactionProfile(Document(Entry())); using var gate = new CommitGate(); using var library = p.Open(gate); using var cancellation = new CancellationTokenSource();
        var first = Change(library, "first"); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = library.UpdateAsync("a", new() { Notes = "cancelled" }, cancellation.Token);
        var next = library.UpdateAsync("a", new() { Favorite = true }); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); gate.Release.TrySetResult(); await Task.WhenAll(first, next);
        Assert.Equal("first", library.Snapshot.Entries[0].Notes); Assert.True(library.Snapshot.Entries[0].Favorite);
    }
    [Theory]
    [InlineData(PersistenceTarget.Backup, PersistenceStage.BeforeTempWrite)]
    [InlineData(PersistenceTarget.Main, PersistenceStage.DuringTempWrite)]
    [InlineData(PersistenceTarget.Main, PersistenceStage.AfterReplace)]
    public async Task CancellationDuringDurableCommitFinishesAndReportsStage(PersistenceTarget target, PersistenceStage stage)
    {
        using var p = new TransactionProfile(Document(Entry())); using var cancellation = new CancellationTokenSource();
        using var library = p.Open(new CallbackProbe((t, s) => { if (t == target && s == stage) cancellation.Cancel(); return Task.CompletedTask; }));
        var result = await library.UpdateAsync("a", new() { Notes = "committed" }, cancellation.Token);
        Assert.True(result.CancellationAfterCommit); Assert.Equal("committed", library.Snapshot.Entries[0].Notes);
        Assert.Equal("committed", Codec.Decode(File.ReadAllBytes(p.Main))[0].Notes);
    }
    [Fact]
    public async Task NewerRequestCommitRejectsOlderCompletionEvenWithoutCancellation()
    {
        using var p = new TransactionProfile(Document(Entry())); using var library = p.Open();
        var a = await library.BeginRequestAsync("a"); var b = await library.BeginRequestAsync("a");
        await library.CommitLinkAsync("a", Work("newer"), token: b); var bytes = File.ReadAllBytes(p.Main);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work("older"), token: a));
        Assert.Equal(bytes, File.ReadAllBytes(p.Main)); Assert.Equal("newer", library.Snapshot.Entries[0].Metadata!.Title);
    }
    [Fact]
    public async Task DeleteRestoreSameIdRejectsPreviousLifecycleToken()
    {
        var original = Document(Entry()); using var p = new TransactionProfile(original); using var library = p.Open();
        var token = await library.BeginRequestAsync("a"); await library.DeleteAsync("a"); await library.ImportAsync(original);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), token: token));
        Assert.Null(library.Snapshot.Entries[0].Metadata);
        var fresh = await library.BeginRequestAsync("a"); Assert.NotEqual(token.Generation, fresh.Generation);
        await library.CommitLinkAsync("a", Work(), token: fresh);
    }
    [Fact]
    public async Task PersonalEditsCoexistButRenameInvalidatesEvidence()
    {
        using var p = new TransactionProfile(Document(Entry())); using var library = p.Open();
        var token = await library.BeginRequestAsync("a"); await library.UpdateAsync("a", new() { Notes = "private", Favorite = true });
        await library.CommitLinkAsync("a", Work(), token: token); Assert.Equal("private", library.Snapshot.Entries[0].Notes);
        token = await library.BeginRequestAsync("a"); await library.UpdateAsync("a", new() { Name = "user rename" });
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), token: token));
    }
    [Fact]
    public async Task MergeCannotConsumeNewerOrPendingTargetEvenIfNewerRequestFails()
    {
        using var p = new TransactionProfile(Document(Entry(), Entry("b") with { Metadata = Work() })); using var library = p.Open();
        var a = await library.BeginRequestAsync("a"); var b = await library.BeginRequestAsync("b");
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), token: a));
        await library.FinishRequestAsync(b);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), token: a));
        Assert.Equal(2, library.Snapshot.Entries.Length);
    }
    [Fact]
    public async Task SuccessfulNoChangeLinkConsumesTokenAndReopenChangesEpoch()
    {
        using var p = new TransactionProfile(Document(Entry() with { Name = "new title", Metadata = Work() })); RequestToken token;
        using (var library = p.Open())
        {
            token = await library.BeginRequestAsync("a"); await library.CommitLinkAsync("a", Work(), token: token);
            await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work(), token: token));
        }
        using var reopened = p.Open(); await Assert.ThrowsAsync<StaleLibraryRequestException>(() => reopened.CommitLinkAsync("a", Work(), token: token));
    }
    [Fact]
    public async Task InstallationRecordsRequireCurrentTokenAndDoNotTouchGameFiles()
    {
        using var p = new TransactionProfile(Document(Entry(), Entry("b"))); using var library = p.Open();
        var location = new Installation("install", "Z:\\NotAnActualGame\\A", "Z:\\NotAnActualGame\\A\\Game.exe", [" --keep "]);
        var old = await library.BeginRequestAsync("a"); await library.AddInstallationAsync("a", location, old);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.AddInstallationAsync("a", location with { Id = "other", Folder = "Z:\\Other" }, old));
        var b = await library.BeginRequestAsync("b");
        await Assert.ThrowsAsync<InvalidDataException>(() => library.AddInstallationAsync("b", location, b));
        Assert.Equal("install", library.Snapshot.Entries[0].PreferredInstallationId);
        await library.RemoveInstallationAsync("a", "install"); Assert.Empty(library.Snapshot.Entries[0].Installations); Assert.Null(library.Snapshot.Entries[0].PreferredInstallationId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedMainOrBackupFailsWithoutFalseSave(bool backup)
    {
        var original = Document(Entry()); using var p = new TransactionProfile(original); File.WriteAllBytes(p.Backup, original); using var library = p.Open();
        using var held = new FileStream(backup ? p.Backup : p.Main, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => Change(library)); Assert.Equal("", library.Snapshot.Entries[0].Notes);
        Assert.Equal(original, File.ReadAllBytes(p.Main));
    }
    [Fact, Trait("Category", "FaultInjection")]
    public async Task PermissionDeniedAndMissingDirectoryAreNotEmptyOrSuccess()
    {
        foreach (var error in new IOException[] { new DirectoryNotFoundException("injected disappeared directory"), new IOException("injected ENOSPC") })
        {
            using var p = new TransactionProfile(Document(Entry())); using var library = p.Open(new CallbackProbe((_, _) => throw error));
            await Assert.ThrowsAnyAsync<IOException>(() => Change(library)); Assert.Equal("", library.Snapshot.Entries[0].Notes);
        }
        using var denied = new TransactionProfile(Document(Entry())); using var blocked = denied.Open(new CallbackProbe((_, _) => throw new UnauthorizedAccessException("injected permission denial")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Change(blocked)); Assert.Equal("", blocked.Snapshot.Entries[0].Notes);
    }
    [Fact]
    public void DirectoryLeasePreventsRemovalAndSecondWriter()
    {
        using var p = new TransactionProfile(Document(Entry())); using var library = p.Open();
        Assert.Throws<IOException>(() => p.Open());
        // Non-recursive rename only, entirely within this test-owned temporary root's parent.
        Assert.Throws<IOException>(() => Directory.Move(p.Root, p.Root + "-moved"));
    }
    [Fact]
    public void MissingDirectoryAndMissingMarkerFailClosed()
    {
        using var p = new TransactionProfile(); File.Delete(Path.Combine(p.Root, IsolatedLibrarySource.MarkerName));
        Assert.Throws<DataIsolationException>(() => p.Open()); Directory.Delete(p.Root);
        Assert.Throws<DirectoryNotFoundException>(() => p.Open());
        Assert.Throws<DataIsolationException>(() => new IsolatedLibraryPersistence("relative"));
    }
    [Fact]
    public async Task ActualReadOnlyMainDeniesReplacementWithoutMemoryCommit()
    {
        var original = Document(Entry()); using var p = new TransactionProfile(original); using var library = p.Open();
        File.SetAttributes(p.Main, FileAttributes.ReadOnly);
        try { await Assert.ThrowsAsync<IOException>(() => Change(library)); Assert.Equal(original, File.ReadAllBytes(p.Main)); Assert.Equal("", library.Snapshot.Entries[0].Notes); }
        finally { File.SetAttributes(p.Main, FileAttributes.Normal); }
    }
    [Theory]
    [InlineData("library.json")]
    [InlineData("library.json.bak")]
    [InlineData(".the-order-writer.lock")]
    public void WriterRejectsHardLinkedFilesBeforeAnyWrite(string target)
    {
        using var p = new TransactionProfile(Document(Entry()));
        var path = Path.Combine(p.Root, target); if (!File.Exists(path)) File.WriteAllBytes(path, Document(Entry()));
        var before = File.ReadAllBytes(path); var alias = Path.Combine(p.Root, "alias");
        Assert.True(CreateHardLink(alias, path, IntPtr.Zero));
        if (target == "library.json.bak")
        {
            using var library = p.Open();
            Assert.Throws<DataIsolationException>(() => library.UpdateAsync("a", new() { Notes = "bad" }).GetAwaiter().GetResult());
        }
        else Assert.Throws<DataIsolationException>(() => p.Open());
        Assert.Equal(before, File.ReadAllBytes(alias));
    }
    [Fact]
    public void WriterRejectsFormalPathsLexicallyWithoutOpeningThem()
    {
        var formal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KunGalManager");
        foreach (var path in new[] { formal, Path.Combine(formal, "child"), Path.GetDirectoryName(formal)! })
            Assert.Throws<DataIsolationException>(() => new IsolatedLibraryPersistence(path));
    }
    [Fact]
    public async Task SessionHistoryGrowthRetainsCumulativeTotalsAndReportsCost()
    {
        var sessions = Enumerable.Range(0, 10000).Select(i => new PlaySession($"s-{i}", Clock, Clock, 1)).ToImmutableArray();
        var entry = Entry() with { Sessions = sessions, TotalPlaySeconds = 50000, PlayCount = 30000 };
        var bytes = Document(entry); using var p = new TransactionProfile(bytes); using var library = p.Open();
        var timer = Stopwatch.StartNew(); await Change(library); timer.Stop();
        output.WriteLine($"10k sessions: inputBytes={bytes.Length}; transactionMs={timer.Elapsed.TotalMilliseconds:F1}");
        Assert.Equal(50000, library.Snapshot.Entries[0].TotalPlaySeconds); Assert.Equal(30000, library.Snapshot.Entries[0].PlayCount);
        Assert.Equal(10000, Codec.Decode(File.ReadAllBytes(p.Main))[0].Sessions.Length);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
    [Fact]
    public async Task WholeLibraryTenThousandEntryCostIsMeasuredWithoutChangingModel()
    {
        var entries = Enumerable.Range(0, 10000).Select(i => Entry($"game-{i}")).ToArray();
        var bytes = Document(entries); using var p = new TransactionProfile(bytes); using var library = p.Open();
        var clock = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes();
        await library.UpdateAsync("game-5000", new() { Notes = "changed" }); clock.Stop();
        output.WriteLine($"10k: inputBytes={bytes.Length}; transactionMs={clock.Elapsed.TotalMilliseconds:F1}; allocatedBytes={GC.GetTotalAllocatedBytes() - allocated}; count={library.Snapshot.Entries.Length}");
        Assert.Equal(10000, library.Snapshot.Entries.Length); Assert.Equal("changed", library.Snapshot.Entries[5000].Notes);
        Assert.Equal("", Codec.Decode(File.ReadAllBytes(p.Backup))[5000].Notes);
    }
    private sealed class CallbackProbe(Func<PersistenceTarget, PersistenceStage, Task> action) : IPersistenceProbe
    {
        public Task ReachAsync(PersistenceTarget target, PersistenceStage stage) => action(target, stage);
    }
    private sealed class CommitGate : IPersistenceProbe, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ReachAsync(PersistenceTarget target, PersistenceStage stage)
        {
            if (target == PersistenceTarget.Main && stage == PersistenceStage.BeforeTempWrite && !Entered.Task.IsCompleted)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        public void Dispose() => Release.TrySetResult();
    }
}
