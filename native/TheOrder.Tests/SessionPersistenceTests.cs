using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class SessionPersistenceTests
{
    [Fact]
    public async Task NativeRoundTripPreservesUnknownStartRealBackwardExitAndFractionalTicks()
    {
        await using var f = new SessionTestRig(); await f.Start();
        var n = f.Native with { StartedAtUtc = null, EndedAtUtc = DateTimeOffset.Parse(SessionTestRig.Date).AddDays(-1),
            Lifecycle = SessionLifecycle.Completed, Reason = SessionReason.ExitConfirmed, Accumulated100ns = 17_654_321, RecoveryToken = null };
        var e = f.Library.Snapshot.Entries[0] with { Sessions = [n.Project(f.Stored.Id)] };
        var decoded = SessionTestRig.Codec.Decode(SessionTestRig.Codec.Encode([e], SessionTestRig.Date))[0].Sessions[0];
        Assert.Equal(n, decoded.Native); Assert.Equal(1, decoded.Seconds); Assert.Equal(decoded.StartedAt, decoded.EndedAt);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("enum")]
    [InlineData("field")]
    [InlineData("recoveryField")]
    public async Task FutureNativeSchemaIsReadOnlyNotBackupCorruption(string kind)
    {
        await using var f = new SessionTestRig(); await f.Start();
        var json = JsonNode.Parse(f.Disk.Main!)!; var n = json["games"]![0]!["sessions"]![0]!["native"]!;
        switch (kind)
        {
            case "version": n["version"] = 2; break;
            case "enum": n["lifecycle"] = "FutureState"; break;
            case "field": n["futurePrecision"] = 3; break;
            case "recoveryField": n["recovery"]!["future"] = true; break;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json);
        var disk = new SessionMemoryDisk { Main = bytes, Backup = SessionTestRig.Codec.Encode([SessionTestRig.Entry("backup")], SessionTestRig.Date) };
        await using var library = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk));
        Assert.Equal(LibraryAccess.ValidReadOnly, library.Snapshot.Access); Assert.Equal(LibraryRecovery.Main, library.Snapshot.Recovery);
        Assert.Equal("g", library.Snapshot.Entries[0].Id);
        await Assert.ThrowsAsync<LibraryReadOnlyException>(() => library.UpdateAsync("g", new() { Notes = "must not discard future" }));
        Assert.Equal(bytes, disk.Main); Assert.Equal(0, disk.Writes);
    }
    [Theory]
    [InlineData("revision")]
    [InlineData("reason")]
    [InlineData("accumulated100ns")]
    [InlineData("writerOwner")]
    [InlineData("userPaused")]
    public async Task MissingRequiredV1FieldIsNotSilentlyLegacy(string field)
    {
        await using var f = new SessionTestRig(); await f.Start();
        var json = JsonNode.Parse(f.Disk.Main!)!; json["games"]![0]!["sessions"]![0]!["native"]!.AsObject().Remove(field);
        Assert.Equal(LibraryAccess.InvalidInput, SessionTestRig.Codec.Inspect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json), SessionTestRig.Date).Access);
    }
    [Fact]
    public void OldFourFieldHistoryIdsCountsAndSurplusRemainUnchanged()
    {
        var entry = SessionTestRig.Entry() with { Sessions = [new("old-id", SessionTestRig.Date, SessionTestRig.Date, 100)] };
        var bytes = SessionTestRig.Codec.Encode([entry], SessionTestRig.Date); var disk = new SessionMemoryDisk { Main = bytes };
        using var library = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk));
        var read = library.Snapshot.Entries[0]; Assert.Null(read.Sessions[0].Native); Assert.Equal("old-id", read.Sessions[0].Id);
        Assert.Equal(1000, read.TotalPlaySeconds); Assert.Equal(5, read.PlayCount); Assert.Equal(0, disk.Writes);
    }
    [Fact]
    public async Task PartialAccountingAndRevisionReplayCannotAddTwice()
    {
        await using var f = new SessionTestRig(); await f.Start();
        var identity = (await f.Library.CaptureLaunchAsync("g")).Identity;
        var a = (f.Native with { Accumulated100ns = 150_000_000, Revision = f.Native.Revision + 1 }).Project(f.Stored.Id);
        await f.Library.CommitSessionAsync(identity, a);
        var b = (a.Native! with { Accumulated100ns = 300_000_000, Revision = a.Native.Revision + 1 }).Project(a.Id);
        await f.Library.CommitSessionAsync(identity, b); await f.Library.CommitSessionAsync(identity, b); await f.Library.CommitSessionAsync(identity, a);
        Assert.Equal(1030, f.Library.Snapshot.Entries[0].TotalPlaySeconds); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
        await Assert.ThrowsAsync<SessionConflictException>(() => f.Library.CommitSessionAsync(identity, (b.Native! with { Accumulated100ns = 400_000_000 }).Project(b.Id)));
    }
    [Fact]
    public async Task PortableExportAndImportNeverGrantRecoveryOwnership()
    {
        await using var f = new SessionTestRig(); await f.Start(); var original = f.Disk.Main!.ToArray();
        var exported = await f.Library.ExportPortableAsync(); var item = SessionTestRig.Codec.Decode(exported)[0];
        Assert.Null(item.Sessions[0].Native!.RecoveryToken); Assert.Equal(SessionReason.ImportedSnapshot, item.Sessions[0].Native!.Reason);
        Assert.Equal(original, f.Disk.Main);
        using var empty = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(new()));
        await empty.ImportAsync(original); Assert.Null(empty.Snapshot.Entries[0].Sessions[0].Native!.RecoveryToken);
        Assert.Equal(6, empty.Snapshot.Entries[0].PlayCount);
    }
    [Fact]
    public async Task BackupRecoveryNeverAttachesEvenWithValidOlderToken()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.CheckpointAsync();
        await f.Engine.DisposeAsync(); await f.Library.DisposeAsync();
        var disk = new SessionMemoryDisk { Main = [1, 2, 3], Backup = f.Disk.Main };
        await using var library = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk));
        int attachments = f.Platform.Attachments;
        await using var engine = new SessionEngine(library, f.Files, f.Platform, f.Positions, f.Clock, f.Tokens, autoPoll: false);
        await engine.InitializeAsync(); Assert.Equal(attachments, f.Platform.Attachments);
        Assert.Equal(SessionReason.BackupRecovery, library.Snapshot.Entries[0].Sessions[0].Native!.Reason);
    }
    [Fact]
    public async Task MultipleRecoveryCandidatesAreInvalidatedTogetherNotChosenArbitrarily()
    {
        await using var f = new SessionTestRig(); await f.Start(); var s = f.Stored;
        var entry = f.Library.Snapshot.Entries[0] with { Sessions = [s, s with { Id = Guid.NewGuid().ToString() }] };
        var disk = new SessionMemoryDisk { Main = SessionTestRig.Codec.Encode([entry], SessionTestRig.Date) };
        await using var library = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk));
        await using var engine = new SessionEngine(library, f.Files, f.Platform, f.Positions, f.Clock, f.Tokens, autoPoll: false);
        await engine.InitializeAsync(); Assert.Equal(1, disk.Writes);
        Assert.All(library.Snapshot.Entries[0].Sessions, x => Assert.Equal(SessionReason.RecoveryInvalid, x.Native!.Reason));
    }
    [Fact]
    public async Task DeletedInstallationRecoveryStillFinalizesHistoryWithoutInventingExit()
    {
        await using var f = new SessionTestRig(); await f.Start(); var e = f.Library.Snapshot.Entries[0] with { Installations = [], PreferredInstallationId = null };
        f.Disk.Main = SessionTestRig.Codec.Encode([e], SessionTestRig.Date);
        await f.Reopen(); Assert.Equal(SessionLifecycle.Incomplete, f.Native.Lifecycle); Assert.Null(f.Native.EndedAtUtc);
    }
    [Fact]
    public async Task ZeroSecondNativeDuplicateMergeDeductsOneCountAndPreservesSurplus()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.StopObservingAsync();
        var first = f.Library.Snapshot.Entries[0] with { Installations = [], PreferredInstallationId = null };
        var work = new WorkMetadata("200", "Title", "", [], null, null, null, null, "", null, null, [], [], [], [], [], null, null, SessionTestRig.Date);
        var second = first with { Id = "other", Metadata = work };
        // Each source is separately valid; merge input must allow a duplicate terminal invocation across entries.
        var disk = new SessionMemoryDisk { Main = SessionTestRig.Codec.Encode([first, second], SessionTestRig.Date) };
        await using var lib = TransactionalLibrary.Open(SessionTestRig.Codec, new SessionMemoryPersistence(disk));
        await lib.LinkAsync("g", work); var merged = Assert.Single(lib.Snapshot.Entries);
        Assert.Single(merged.Sessions); Assert.Equal(11, merged.PlayCount); Assert.Equal(2000, merged.TotalPlaySeconds);
    }
    public static IEnumerable<object[]> Faults() => TransactionSafetyTests.Faults();
    [Theory, MemberData(nameof(Faults)), Trait("Category", "FaultInjection")]
    public async Task SessionFinalDeltaAndCountRemainAtomicAtEveryPersistenceBoundary(PersistenceTarget target, PersistenceStage stage)
    {
        await using var f = new SessionTestRig(); await f.Start();
        var original = f.Disk.Main!;
        using var profile = new TransactionProfile(original);
        var probe = new SessionFaultProbe(target, stage);
        using (var lib = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(profile.Root, probe)))
        {
            var old = lib.Snapshot.Entries[0].Sessions[0]; var identity = (await lib.CaptureLaunchAsync("g")).Identity;
            var claimed = old.Native! with { WriterOwner = lib.SessionWriterOwner, Revision = old.Native!.Revision + 1 };
            await lib.CommitSessionAsync(identity, claimed.Project(old.Id), recover: true, expectedRevision: old.Native.Revision);
            probe.Armed = true;
            var n = claimed with { Revision = claimed.Revision + 1, Accumulated100ns = 300_000_000,
                Lifecycle = SessionLifecycle.Completed, Reason = SessionReason.ExitConfirmed,
                EndedAtUtc = claimed.CreatedObservedAtUtc.AddSeconds(30), RecoveryToken = null };
            if (stage == PersistenceStage.AfterReplace)
                Assert.NotNull((await lib.CommitSessionAsync(identity, n.Project(old.Id))).Warning);
            else await Assert.ThrowsAsync<IOException>(() => lib.CommitSessionAsync(identity, n.Project(old.Id)));
        }
        using var reopened = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(profile.Root));
        var read = reopened.Snapshot.Entries[0]; Assert.Equal(6, read.PlayCount);
        Assert.Equal(stage == PersistenceStage.AfterReplace ? 1030 : 1000, read.TotalPlaySeconds);
        Assert.Equal(stage == PersistenceStage.AfterReplace ? SessionLifecycle.Completed : SessionLifecycle.Open, read.Sessions[0].Native!.Lifecycle);
    }
    private sealed class SessionFaultProbe(PersistenceTarget target, PersistenceStage stage) : IPersistenceProbe
    {
        public bool Armed;
        public Task ReachAsync(PersistenceTarget t, PersistenceStage s) => Armed && t == target && s == stage ? throw new IOException("synthetic session write fault") : Task.CompletedTask;
    }
}
