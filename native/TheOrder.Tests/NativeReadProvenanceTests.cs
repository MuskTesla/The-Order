using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NativeReadProvenanceTests(ITestOutputHelper output)
{
    private static readonly ProfilePathGuard.KnownRoots Roots = new(@"C:\provenance-strings\legacy", @"C:\provenance-strings\production", @"C:\provenance-strings\development");
    private static string MainPath(ProfileFixture f) => Path.Combine(f.Root, "library.json");
    private static string BackupPath(ProfileFixture f) => MainPath(f) + ".bak";
    private static async Task Seed(NativeProfileStore owner)
    {
        using var library = TransactionalLibrary.Open(NativeProfileTests.Codec, owner.CreateLibraryPersistence());
        var entry = SessionTestRig.Entry(total: 71, count: 3) with { Notes = "old-note", Favorite = false, Categories = ["old"] };
        await library.ImportAsync(NativeProfileTests.Codec.Encode([entry], SessionTestRig.Date));
        await library.UpdateAsync("g", new() { Notes = "new-note", Favorite = true, Categories = ImmutableArray.Create("A") });
    }
    private static async Task EditAndCheckCurrent(NativeProfileStore owner)
    {
        using var library = await new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default);
        Assert.Equal("new-note", library.Snapshot.Entries[0].Notes); Assert.True(library.Snapshot.Entries[0].Favorite);
        await library.UpdateAsync("g", new() { Categories = ImmutableArray.Create("B") });
        var current = NativeProfileTests.Codec.Decode(owner.ReadComponent("library.json")!)[0];
        Assert.Equal("new-note", current.Notes); Assert.True(current.Favorite); Assert.Equal("B", Assert.Single(current.Categories));
        Assert.Equal(71, current.TotalPlaySeconds); Assert.Equal(3, current.PlayCount);
    }
    [Theory]
    [InlineData(false, 0)][InlineData(false, 1)][InlineData(false, 2)]
    [InlineData(true, 0)][InlineData(true, 1)][InlineData(true, 2)]
    public async Task GateRaceAfterBaselineCannotLoadOldBackupAndRetryPreservesNewFields(bool childProcess, int round)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        var main = File.ReadAllBytes(MainPath(f)); var backup = File.ReadAllBytes(BackupPath(f));
        FileStream? locked = null; Process? child = null; var accesses = 0; int? pid = null;
        ProfilePathGuard.Probe.Value = new(Roots, () =>
        {
            if (++accesses != 2) return; // First access establishes baseline; second is actual factory load.
            if (!childProcess) locked = new(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None);
            else
            {
                child = f.Start("a", "LockMain", false); pid = child.Id;
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(Path.Combine(f.Parent, "a.ready")))
                { if (child.HasExited || DateTime.UtcNow > deadline) throw new TimeoutException("Child lock not ready"); Thread.Sleep(10); }
            }
        });
        try
        {
            var error = await Assert.ThrowsAsync<NativeProfileException>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default));
            Assert.Equal("LibraryTemporarilyUnavailable", error.Code); Assert.Equal(2, accesses);
        }
        finally
        {
            ProfilePathGuard.Probe.Value = null; locked?.Dispose();
            if (child is not null)
            {
                File.WriteAllText(Path.Combine(f.Parent, "release"), "release");
                Assert.True(child.WaitForExit(15000)); Assert.Equal(0, child.ExitCode); child.Dispose();
            }
        }
        Assert.Equal(main, File.ReadAllBytes(MainPath(f))); Assert.Equal(backup, File.ReadAllBytes(BackupPath(f)));
        await EditAndCheckCurrent(owner); Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        owner.Dispose();
        using var second = f.Start("b", "Borrow", false); await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(0, second.ExitCode);
        output.WriteLine($"round={round}; child={childProcess}; PID={pid}; Open=LibraryTemporarilyUnavailable; original bytes preserved; retry new-note/true/B; second owner exit=0");
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BeforeBaselineLockAndCleanControlUseCurrentMain(bool lockedFirst)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner); var before = f.Hashes();
        if (lockedFirst)
        {
            using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default));
            Assert.Equal(before, f.Hashes());
        }
        await EditAndCheckCurrent(owner);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AccessFailuresAfterBaselineAreRetryableWithoutBackupOrOrphan(bool denied)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner); owner.CheckpointBackups(); var before = f.Hashes();
        var access = 0;
        ProfilePathGuard.Probe.Value = new(Roots, () =>
        {
            if (++access == 2) { if (denied) throw new UnauthorizedAccessException("synthetic transient denial"); throw new IOException("synthetic unavailable"); }
        });
        try
        {
            Assert.Equal("LibraryTemporarilyUnavailable", (await Assert.ThrowsAsync<NativeProfileException>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default))).Code);
        }
        finally { ProfilePathGuard.Probe.Value = null; }
        Assert.Equal(before, f.Hashes()); owner.Restore("library.json"); await EditAndCheckCurrent(owner);
        owner.Dispose(); using var next = f.Open(); using var writer = next.CreateLibraryPersistence();
    }
    [Theory][InlineData("malformed")][InlineData("truncated")][InlineData("structural")][InlineData("missing")]
    public async Task ProvenInvalidInputIsRestoredBeforeWritableBorrowAndBaselineUsesRestoredMain(string damage)
    {
        using var f = new ProfileFixture(); using (var owner = f.Open()) { await Seed(owner); owner.CheckpointBackups(); }
        if (damage == "missing") File.Delete(MainPath(f));
        else if (damage == "structural")
        {
            var envelope = JsonNode.Parse(File.ReadAllBytes(MainPath(f)))!;
            envelope["Data"] = Convert.ToBase64String("[]"u8.ToArray()); File.WriteAllText(MainPath(f), envelope.ToJsonString());
        }
        else File.WriteAllText(MainPath(f), damage == "truncated" ? "{\"Version\":1," : "{bad");
        using var restoredOwner = f.Open(); var backup = File.ReadAllBytes(BackupPath(f));
        using var persistence = restoredOwner.CreateLibraryPersistence();
        var authoritative = File.ReadAllBytes(MainPath(f));
        Assert.True(JsonNode.Parse(authoritative)!["RestoredFromBackup"]!.GetValue<bool>());
        Assert.Equal(backup, File.ReadAllBytes(BackupPath(f)));
        using var library = TransactionalLibrary.Open(NativeProfileTests.Codec, persistence);
        Assert.Equal(LibraryRecovery.Backup, library.Snapshot.Recovery); Assert.Equal("new-note", library.Snapshot.Entries[0].Notes);
        Assert.Equal(authoritative, File.ReadAllBytes(MainPath(f))); // Phase 2 Open itself still never writes.
        var external = JsonNode.Parse(authoritative)!;
        var entries = NativeProfileTests.Codec.Decode(Convert.FromBase64String(external["Data"]!.GetValue<string>()));
        external["Data"] = Convert.ToBase64String(NativeProfileTests.Codec.Encode([entries[0] with { Notes = "external valid edit" }], SessionTestRig.Date));
        File.WriteAllText(MainPath(f), external.ToJsonString()); var changed = File.ReadAllBytes(MainPath(f));
        Assert.Equal("LibraryChangedOutsideWriter", (await Assert.ThrowsAsync<NativeProfileException>(() => library.UpdateAsync("g", new() { Categories = ImmutableArray.Create("B") }))).Code);
        Assert.Equal(changed, File.ReadAllBytes(MainPath(f)));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MainRemovedOrAtomicallyReplacedAfterBaselineCannotSilentlyLoadBackup(bool replace)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        var initial = File.ReadAllBytes(MainPath(f)); var backup = File.ReadAllBytes(BackupPath(f)); var access = 0;
        var other = JsonNode.Parse(initial)!; var entries = NativeProfileTests.Codec.Decode(Convert.FromBase64String(other["Data"]!.GetValue<string>()));
        other["Data"] = Convert.ToBase64String(NativeProfileTests.Codec.Encode([entries[0] with { Notes = "generation B" }], SessionTestRig.Date));
        ProfilePathGuard.Probe.Value = new(Roots, () =>
        {
            if (++access != 2) return;
            if (!replace) File.Delete(MainPath(f));
            else { var temp = MainPath(f) + ".replacement"; File.WriteAllText(temp, other.ToJsonString()); File.Move(temp, MainPath(f), true); }
        });
        try { await Assert.ThrowsAsync<InvalidDataException>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default)); }
        finally { ProfilePathGuard.Probe.Value = null; }
        Assert.Equal(backup, File.ReadAllBytes(BackupPath(f)));
        if (!replace) { Assert.False(File.Exists(MainPath(f))); File.WriteAllBytes(MainPath(f), initial); }
        using var retry = TransactionalLibrary.Open(NativeProfileTests.Codec, owner.CreateLibraryPersistence());
        Assert.Equal(replace ? "generation B" : "new-note", retry.Snapshot.Entries[0].Notes);
    }
    [Fact]
    public async Task BorrowedWriterCannotManuallySelectAnOlderBackup()
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        using var writer = owner.CreateLibraryPersistence();
        Assert.Throws<InvalidDataException>(() => writer.ReadBackup());
        Assert.Equal("new-note", NativeProfileTests.Codec.Decode(writer.ReadMain()!)[0].Notes);
    }
    [Fact]
    public async Task ConcurrentFactoriesDuringLockAfterBaselineNeverReturnOldBackupOrClearWinner()
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        var persistence = owner.CreateLibraryPersistence();
        using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = Task.Run(() => Assert.Throws<NativeProfileException>(() => TransactionalLibrary.Open(NativeProfileTests.Codec, persistence)));
            var second = Task.Run(async () => await Assert.ThrowsAnyAsync<Exception>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default)));
            await Task.WhenAll(first, second); Assert.Equal("LibraryTemporarilyUnavailable", (await first).Code);
        }
        using var winner = owner.CreateLibraryPersistence();
        Assert.Equal("LibraryWriterAlreadyOwned", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
        Assert.Equal("DisposeLibraryBeforeProfile", Assert.Throws<NativeProfileException>(() => owner.Dispose()).Code);
    }
    [Fact]
    public async Task RepeatedLateAccessFailuresReleaseStreamsWritersAndTasks()
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        async Task Cycle()
        {
            var writer = owner.CreateLibraryPersistence();
            using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal("LibraryTemporarilyUnavailable", Assert.Throws<NativeProfileException>(() => TransactionalLibrary.Open(NativeProfileTests.Codec, writer)).Code);
            await EditAndCheckCurrent(owner); Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        }
        for (var i = 0; i < 5; i++) await Cycle(); GC.Collect(); GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.HandleCount; var samples = new List<int>();
        for (var i = 0; i < 30; i++) { await Cycle(); process.Refresh(); samples.Add(process.HandleCount); }
        GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh(); var after = process.HandleCount;
        output.WriteLine($"late sharing failure/open cleanup/unlock/retry/save: 30 rounds; handles={before}->{after}; samples=[{string.Join(',', samples)}]; temp=0");
        Assert.InRange(after - before, -100, 6); owner.Dispose(); using var next = f.Open(); using var borrower = next.CreateLibraryPersistence();
    }
    [Theory][InlineData(PersistenceStage.BeforeReplace)][InlineData(PersistenceStage.AfterReplace)]
    public async Task RecoveryPublicationFaultKeepsAuthorityAndOwnershipCoherent(PersistenceStage stage)
    {
        using var f = new ProfileFixture(); using (var seed = f.Open()) { await Seed(seed); seed.CheckpointBackups(); }
        File.WriteAllText(MainPath(f), "{bad"); var damaged = File.ReadAllBytes(MainPath(f)); var backup = File.ReadAllBytes(BackupPath(f));
        using (var owner = NativeProfileStore.Open(f.Request, NativeProfileTests.Codec, persistenceProbe: new RecoveryFault(stage)))
        {
            if (stage == PersistenceStage.BeforeReplace)
            {
                Assert.Throws<IOException>(() => owner.CreateLibraryPersistence());
                Assert.Equal(damaged, File.ReadAllBytes(MainPath(f)));
            }
            else
            {
                // The shared atomic primitive treats an error after replacement as committed.
                using var library = TransactionalLibrary.Open(NativeProfileTests.Codec, owner.CreateLibraryPersistence());
                Assert.Equal("new-note", library.Snapshot.Entries[0].Notes);
                Assert.Equal(LibraryRecovery.Backup, library.Snapshot.Recovery);
            }
            Assert.Equal(backup, File.ReadAllBytes(BackupPath(f))); Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        }
        using var next = f.Open(); await EditAndCheckCurrent(next);
    }
    [Theory][InlineData("corrupt")][InlineData("future")][InlineData("profile")][InlineData("store")]
    public async Task BoundedCorruptMainCannotPromoteInvalidOrForeignBackup(string kind)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); await Seed(owner);
        File.WriteAllText(MainPath(f), "{bad"); var valid = File.ReadAllBytes(BackupPath(f));
        if (kind == "corrupt") File.WriteAllText(BackupPath(f), "{bad");
        else
        {
            var candidate = JsonNode.Parse(valid)!;
            if (kind == "future") candidate["Version"] = 2;
            else candidate[kind == "profile" ? "ProfileId" : "StoreId"] = Guid.NewGuid();
            File.WriteAllText(BackupPath(f), candidate.ToJsonString());
        }
        var before = f.Hashes();
        Assert.NotNull(Record.Exception(() => owner.CreateLibraryPersistence())); Assert.Equal(before, f.Hashes());
        File.WriteAllBytes(BackupPath(f), valid); owner.Restore("library.json"); using var writer = owner.CreateLibraryPersistence();
    }
    private sealed class RecoveryFault(PersistenceStage stage) : IPersistenceProbe
    {
        public Task ReachAsync(PersistenceTarget target, PersistenceStage actual)
        { if (target == PersistenceTarget.Main && actual == stage) throw new IOException("synthetic recovery publication fault"); return Task.CompletedTask; }
    }
}
