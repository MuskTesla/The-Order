using System.Diagnostics;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class ProfileWriterRecoveryTests(ITestOutputHelper output)
{
    private static string MainPath(ProfileFixture f) => Path.Combine(f.Root, "library.json");
    private static string Backup(ProfileFixture f) => MainPath(f) + ".bak";
    private static void Corrupt(ProfileFixture f, string kind)
    {
        if (kind == "oversize") { using var file = new FileStream(MainPath(f), FileMode.Create); file.SetLength(49L * 1024 * 1024); }
        else File.WriteAllText(MainPath(f), kind == "truncated" ? "{\"Version\":1," : "{malformed");
    }
    [Fact]
    public async Task ExternalProcessSharingConflictRollsBackRetryDisposalAndSecondProcessOwner()
    {
        using var f = new ProfileFixture(); var owner = f.Open();
        try
        {
            using (var child = f.Start("a", "LockMain", false))
            {
                await NativeProfileTests.Until(() => File.Exists(Path.Combine(f.Parent, "a.ready")) || child.HasExited);
                Assert.False(child.HasExited);
                for (var i = 0; i < 5; i++) Assert.ThrowsAny<IOException>(() => owner.CreateLibraryPersistence());
                File.WriteAllText(Path.Combine(f.Parent, "release"), "release");
                await child.WaitForExitAsync(); Assert.Equal(0, child.ExitCode);
            }
            using (var writer = owner.CreateLibraryPersistence()) Assert.NotNull(writer.ReadMain());
            // Failure -> dispose even while main remains externally unavailable.
            using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
            { Assert.ThrowsAny<IOException>(() => owner.CreateLibraryPersistence()); owner.Dispose(); }
            using var second = f.Start("b", "Borrow", false);
            await second.WaitForExitAsync(); Assert.Equal(0, second.ExitCode);
            Assert.True(File.Exists(Path.Combine(f.Parent, "b.ready")));
        }
        finally { owner.Dispose(); }
    }
    [Theory][InlineData("malformed")][InlineData("truncated")][InlineData("oversize")]
    public async Task CorruptMainRecoversWithIdentityCwdAccountingAndStaleProtection(string kind)
    {
        using var f = new ProfileFixture(); NativeProfileManifest manifest;
        var entry = SessionTestRig.Entry(total: 41, count: 2) with
        { Installations = [new Installation("i", @"C:\synthetic\game", @"C:\synthetic\game\game.exe", [], @"C:\synthetic\game\work")] };
        using (var owner = f.Open())
        {
            manifest = owner.Manifest;
            using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => owner.CreateLibraryPersistence());
            using (var library = await new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default))
                await library.ImportAsync(NativeProfileTests.Codec.Encode([entry], SessionTestRig.Date));
            owner.CheckpointBackups();
        }
        Corrupt(f, kind); var backup = File.ReadAllBytes(Backup(f));
        using (var owner = f.Open())
        {
            Assert.Equal(manifest, owner.Manifest with { RequiredCapabilities = manifest.RequiredCapabilities });
            if (kind == "oversize")
            {
                for (var i = 0; i < 3; i++) Assert.Equal("LibraryRecoveryRequired", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
                Assert.Equal(backup, File.ReadAllBytes(Backup(f)));
                owner.Restore("library.json");
            }
            using (var library = await new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(default))
            {
                Assert.Equal(LibraryRecovery.Backup, library.Snapshot.Recovery);
                Assert.Equal(entry.Installations[0].WorkingDirectory, library.Snapshot.Entries[0].Installations[0].WorkingDirectory);
                await library.UpdateAsync("g", new() { Notes = "after recovery" });
                var validMain = File.ReadAllBytes(MainPath(f)); File.WriteAllText(MainPath(f), "external mutation");
                Assert.Equal("LibraryChangedOutsideWriter", (await Assert.ThrowsAsync<NativeProfileException>(() => library.UpdateAsync("g", new() { Notes = "stale" }))).Code);
                Assert.Equal("external mutation", File.ReadAllText(MainPath(f)));
                File.WriteAllBytes(MainPath(f), validMain);
                await library.UpdateAsync("g", new() { Notes = "final" });
            }
        }
        using var reopened = f.Open(); using var writer = TransactionalLibrary.Open(NativeProfileTests.Codec, reopened.CreateLibraryPersistence());
        Assert.Equal(manifest.ProfileId, reopened.Manifest.ProfileId); Assert.Equal(manifest.StoreId, reopened.Manifest.StoreId); Assert.Equal(manifest.ProfileKind, reopened.Manifest.ProfileKind);
        var result = Assert.Single(writer.Snapshot.Entries);
        Assert.Equal("final", result.Notes); Assert.Equal(entry.Installations[0].WorkingDirectory, result.Installations[0].WorkingDirectory);
        Assert.Equal(41, result.TotalPlaySeconds); Assert.Equal(2, result.PlayCount);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }
    [Theory][InlineData("corrupt")][InlineData("future")][InlineData("foreign")][InlineData("schema")]
    public void InvalidRecoveryBackupNeverLeavesOwnershipOrOverwritesData(string kind)
    {
        using var f = new ProfileFixture(); using var owner = f.Open();
        var valid = File.ReadAllBytes(Backup(f)); Corrupt(f, "oversize");
        if (kind == "corrupt") File.WriteAllText(Backup(f), "{broken");
        else
        {
            var json = JsonNode.Parse(valid)!;
            if (kind == "future") json["Version"] = 2;
            else if (kind == "foreign") json["StoreId"] = Guid.NewGuid();
            else json["futureField"] = true;
            File.WriteAllText(Backup(f), json.ToJsonString());
        }
        var before = f.Hashes();
        for (var i = 0; i < 3; i++)
        {
            var failure = Record.Exception(() => owner.CreateLibraryPersistence()); Assert.NotNull(failure);
            Assert.DoesNotContain("AlreadyOwned", failure.Message);
            var restore = Record.Exception(() => owner.Restore("library.json")); Assert.NotNull(restore); Assert.DoesNotContain("CloseLibraryBeforeRestore", restore.Message);
        }
        Assert.Equal(before, f.Hashes());
        // Recovery failure never retains ownership; a valid backup can be reinstated and explicitly restored.
        File.WriteAllBytes(Backup(f), valid); owner.Restore("library.json");
        using (owner.CreateLibraryPersistence()) { }
        owner.Dispose(); using var second = f.Open(); using var borrower = second.CreateLibraryPersistence();
    }
    [Theory][InlineData(48L * 1024 * 1024 + 1)][InlineData(80L * 1024 * 1024)]
    public void AnyEnvelopeAboveReadCapacityRequiresRestoreWithoutBorrowing(long bytes)
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        using (var file = new FileStream(MainPath(f), FileMode.Create)) file.SetLength(bytes);
        using (var owner = f.Open())
        {
            Assert.Equal("LibraryRecoveryRequired", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
            Assert.Equal(bytes, new FileInfo(MainPath(f)).Length);
        }
        using var next = f.Open(); next.Restore("library.json"); using var writer = next.CreateLibraryPersistence(); Assert.NotNull(writer.ReadMain());
    }
    [Fact]
    public async Task ConcurrentLoserCannotReleaseWinnerAndRestoreRequiresActualBorrower()
    {
        using var f = new ProfileFixture(); using var owner = f.Open();
        for (var round = 0; round < 10; round++)
        {
            using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => owner.CreateLibraryPersistence());
            using var start = new ManualResetEventSlim();
            Task<(ILibraryPersistence? Writer, Exception? Error)> Attempt() => Task.Run(() =>
            {
                start.Wait();
                try { return (owner.CreateLibraryPersistence(), (Exception?)null); }
                catch (Exception error) { return ((ILibraryPersistence?)null, error); }
            });
            var a = Attempt(); var b = Attempt(); start.Set(); var outcomes = await Task.WhenAll(a, b);
            using var winner = Assert.Single(outcomes, o => o.Writer is not null).Writer!;
            Assert.Equal("LibraryWriterAlreadyOwned", Assert.IsType<NativeProfileException>(Assert.Single(outcomes, o => o.Error is not null).Error).Code);
            Assert.Equal("CloseLibraryBeforeRestore", Assert.Throws<NativeProfileException>(() => owner.Restore("library.json")).Code);
            Assert.Equal("DisposeLibraryBeforeProfile", Assert.Throws<NativeProfileException>(() => owner.Dispose()).Code);
            Assert.Equal("LibraryWriterAlreadyOwned", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
            winner.Dispose(); winner.Dispose(); owner.Restore("library.json");
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FailedRecoveryBeforeOrAfterLeaseTransferStillAllowsDispose(bool afterTransfer)
    {
        using var f = new ProfileFixture(); var owner = f.Open();
        var main = File.ReadAllBytes(MainPath(f)); var backup = File.ReadAllBytes(Backup(f));
        var persistence = afterTransfer ? owner.CreateLibraryPersistence() : null;
        Corrupt(f, "malformed"); File.WriteAllText(Backup(f), "{bad");
        if (afterTransfer)
            Assert.Throws<InvalidDataException>(() => TransactionalLibrary.Open(NativeProfileTests.Codec, persistence!));
        else Assert.Throws<InvalidDataException>(() => owner.CreateLibraryPersistence());
        owner.Dispose(); // No intervening successful restore/borrow may hide an orphan.
        File.WriteAllBytes(MainPath(f), main); File.WriteAllBytes(Backup(f), backup);
        using var second = f.Open(); using var writer = second.CreateLibraryPersistence();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CancellationBeforeOrDuringFactoryBorrowReturnsOwnership(bool during)
    {
        using var f = new ProfileFixture(); using var owner = f.Open(); using var cancellation = new CancellationTokenSource();
        if (!during) cancellation.Cancel();
        else ProfilePathGuard.Probe.Value = new(new(@"C:\synthetic-guard\legacy", @"C:\synthetic-guard\production", @"C:\synthetic-guard\development"), cancellation.Cancel);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IsolatedLibraryFactory(NativeProfileTests.Codec, owner.Configuration()).OpenAsync(cancellation.Token));
        }
        finally { ProfilePathGuard.Probe.Value = null; }
        owner.Restore("library.json"); using (owner.CreateLibraryPersistence()) { }
        owner.Dispose(); using var second = f.Open(); using var borrower = second.CreateLibraryPersistence();
    }
    [Fact]
    public void FailureRecoveryResourceLoopsReleaseHandlesLeasesAndTemps()
    {
        using var f = new ProfileFixture(); var disposed = 0;
        void Cycle()
        {
            using (var owner = f.Open())
            {
                using (var locked = new FileStream(MainPath(f), FileMode.Open, FileAccess.Read, FileShare.None))
                    for (var attempt = 0; attempt < 4; attempt++) Assert.ThrowsAny<IOException>(() => owner.CreateLibraryPersistence());
                using (owner.CreateLibraryPersistence()) { }
                Corrupt(f, "oversize");
                Assert.Equal("LibraryRecoveryRequired", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
                owner.Restore("library.json"); using (owner.CreateLibraryPersistence()) { }
                Corrupt(f, "malformed");
                using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, owner.CreateLibraryPersistence())) Assert.Equal(LibraryRecovery.Backup, library.Snapshot.Recovery);
                owner.Restore("library.json"); Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
            }
            disposed++;
        }
        for (var i = 0; i < 5; i++) Cycle(); GC.Collect(); GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.HandleCount; var samples = new List<int>();
        for (var i = 0; i < 30; i++) { Cycle(); process.Refresh(); samples.Add(process.HandleCount); }
        GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh(); var after = process.HandleCount;
        output.WriteLine($"failure loops=30; sharing failures=120; capacity failures=30; disposed stores={disposed}; handles={before}->{after}; samples=[{string.Join(',', samples)}]; temp files=0");
        Assert.InRange(after - before, -100, 6); Assert.Equal(35, disposed);
        using var second = f.Open(); using var writer = second.CreateLibraryPersistence();
    }
}
