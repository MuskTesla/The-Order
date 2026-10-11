using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

public sealed class SessionHardeningTests
{
    [Fact]
    public void AccumulatorClampsAtHistoryCapacityWithoutOverflowOrNegativeDelta()
    {
        var core = new SessionAccumulator(NativeSession.MaximumTicks - 10_000_000);
        var zero = new SessionClockStamp(0, 0, DateTimeOffset.UtcNow);
        core.Observe(zero, zero, true); core.Observe(zero with { Awake100ns = 100_000_000 }, zero with { Awake100ns = 100_000_000 }, true);
        Assert.True(core.AtCapacity); Assert.Equal(NativeSession.MaximumTicks, core.Accumulated); Assert.Null(core.Baseline);
    }
    [Fact]
    public async Task NativeTokenExpansionRespectsWholeDocumentLimit()
    {
        await using var f = new SessionTestRig(); await f.Start();
        var native = f.Native with { RecoveryToken = new string('a', 32768) };
        var histories = Enumerable.Range(0, 1050).Select(_ => native.Project(Guid.NewGuid().ToString())).ToImmutableArray();
        var entry = f.Library.Snapshot.Entries[0] with { Sessions = histories, PlayCount = 1055 };
        var before = f.Disk.Main!.ToArray();
        Assert.Throws<LibraryWriteLimitException>(() => SessionTestRig.Codec.Encode([entry], SessionTestRig.Date));
        Assert.Equal(before, f.Disk.Main);
    }
    [Fact]
    public async Task DuplicateResumeDoesNotEraseAlreadyEstablishedAwakeInterval()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SuspendAsync(); await f.Engine.ResumeFromSuspendAsync();
        await f.Engine.SampleAsync(); f.Clock.Advance(3); await f.Engine.ResumeFromSuspendAsync();
        await f.Advance(2); await f.Engine.CheckpointAsync(); Assert.Equal(5, f.Stored.Seconds);
    }
    [Fact]
    public async Task TerminalCheckpointRetryIsSuccessfulNoop()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        var before = f.Disk.Main!.ToArray(); await f.Engine.CheckpointAsync(); await f.Engine.CheckpointAsync();
        Assert.Null(f.Engine.Snapshot.Error); Assert.Equal(before, f.Disk.Main);
    }
    [Fact]
    public async Task CrashAfterMainReplaceBeforeReceiptCannotDoubleAccountOnReopen()
    {
        await using var f = new SessionTestRig(); await f.Start();
        using var profile = new TheOrder.Tests.TransactionProfile(f.Disk.Main!);
        var persistence = new LostReceiptPersistence(new TheOrder.Platform.Windows.IsolatedLibraryPersistence(profile.Root));
        using (var lib = TransactionalLibrary.Open(SessionTestRig.Codec, persistence))
        {
            var old = lib.Snapshot.Entries[0].Sessions[0]; var identity = (await lib.CaptureLaunchAsync("g")).Identity;
            var claimed = old.Native! with { WriterOwner = lib.SessionWriterOwner, Revision = old.Native!.Revision + 1 };
            await lib.CommitSessionAsync(identity, claimed.Project(old.Id), recover: true, expectedRevision: old.Native.Revision);
            persistence.LoseReceipt = true;
            var next = claimed with { Revision = claimed.Revision + 1, Accumulated100ns = 300_000_000 };
            await Assert.ThrowsAsync<SimulatedCrashException>(() => lib.CommitSessionAsync(identity, next.Project(old.Id)));
            Assert.Equal(1000, lib.Snapshot.Entries[0].TotalPlaySeconds); // Process memory never acknowledged the replace.
        }
        using var reopened = TransactionalLibrary.Open(SessionTestRig.Codec, new TheOrder.Platform.Windows.IsolatedLibraryPersistence(profile.Root));
        var stored = reopened.Snapshot.Entries[0].Sessions[0]; Assert.Equal(1030, reopened.Snapshot.Entries[0].TotalPlaySeconds);
        var owner = (await reopened.CaptureLaunchAsync("g")).Identity;
        var recovery = stored.Native! with { WriterOwner = reopened.SessionWriterOwner, Revision = stored.Native!.Revision + 1 };
        var command = recovery.Project(stored.Id);
        await reopened.CommitSessionAsync(owner, command, recover: true, expectedRevision: stored.Native.Revision);
        await reopened.CommitSessionAsync(owner, command);
        Assert.Equal(1030, reopened.Snapshot.Entries[0].TotalPlaySeconds); Assert.Equal(6, reopened.Snapshot.Entries[0].PlayCount);
    }
    [Fact]
    public async Task OneWriterCannotAccidentallyOwnTwoEngines()
    {
        await using var f = new SessionTestRig();
        Assert.Throws<SessionConflictException>(() => new SessionEngine(f.Library, f.Files, f.Platform, f.Positions, f.Clock, f.Tokens, autoPoll: false));
        await f.Start(); Assert.Equal(1, f.Platform.Starts);
    }
    [Fact]
    public async Task PendingFirstWriteProtectsAgainstDeleteAndKeepsPersonalChanges()
    {
        await using var f = new SessionTestRig(); f.Disk.Fail = true; await f.Start(); f.Disk.Fail = false;
        await Assert.ThrowsAsync<SessionConflictException>(() => f.Library.DeleteAsync("g"));
        await f.Library.UpdateAsync("g", new() { Notes = "later edit" });
        await f.Engine.CheckpointAsync(); Assert.Equal("later edit", f.Library.Snapshot.Entries[0].Notes);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Fact]
    public async Task TerminalDeleteThenImportSameIdRejectsOldIncarnation()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.StopObservingAsync();
        var identity = (await f.Library.CaptureLaunchAsync("g")).Identity; var old = f.Stored;
        await f.Library.DeleteAsync("g"); await f.Library.ImportAsync(SessionTestRig.Codec.Encode([SessionTestRig.Entry()], SessionTestRig.Date));
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => f.Library.CommitSessionAsync(identity, old));
        Assert.Empty(f.Library.Snapshot.Entries[0].Sessions);
    }
    [Fact]
    public async Task DelayedHandoffDuringCheckpointCannotOverwriteChildToken()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(2);
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Disk.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var saving = f.Engine.CheckpointAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var child = f.Platform.Add(73).Identity;
        var handoff = f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessHandoff, child, f.Platform.Root));
        release.SetResult(); await saving; await handoff; f.Disk.BeforeSave = null;
        Assert.Equal(child, f.Tokens.Open(f.Native.RecoveryToken!)!.Process);
        Assert.Equal(2, f.Stored.Seconds); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Fact]
    public async Task InstallationFailureAfterResumeCannotBeginCountingOnLaterTick()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(2);
        await f.Engine.SuspendAsync(); f.Files.Invalid = true; await f.Engine.ResumeFromSuspendAsync(); await f.Advance(100); await f.Advance(100);
        Assert.Equal(2, f.Stored.Seconds); Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
        f.Files.Invalid = false; await f.Engine.SampleAsync(); await f.Advance(2); await f.Engine.CheckpointAsync(); Assert.Equal(4, f.Stored.Seconds);
    }
    [Fact]
    public async Task FailedRecoveryCommitIsRetryableWithoutExtraPlayCount()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.DisposeAsync(); await f.Library.DisposeAsync();
        await using var library = f.Open(); await using var engine = new SessionEngine(library, f.Files, f.Platform, f.Positions, f.Clock, f.Tokens, autoPoll: false);
        f.Disk.Fail = true; await Assert.ThrowsAsync<IOException>(() => engine.InitializeAsync()); Assert.NotNull(engine.Snapshot.Error);
        f.Disk.Fail = false; await engine.InitializeAsync(); Assert.Null(engine.Snapshot.Error);
        Assert.Equal(1, engine.Snapshot.Session!.Native!.Recovery.Count); Assert.Equal(6, library.Snapshot.Entries[0].PlayCount);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("staleSession")]
    [InlineData("wrongInstallation")]
    [InlineData("nullProcess")]
    [InlineData("wrongCwd")]
    public async Task MalformedOrStaleRecoveryTokenCannotAttach(string kind)
    {
        await using var f = new SessionTestRig(); await f.Start(); var token = f.Tokens.Open(f.Native.RecoveryToken!)!;
        token = kind switch
        {
            "staleSession" => token with { SessionId = Guid.NewGuid().ToString() },
            "wrongInstallation" => token with { InstallationId = "other" },
            "nullProcess" => token with { Process = null! },
            "wrongCwd" => token with { WorkingDirectory = token.WorkingDirectory with { Identity = new("other", "file") } },
            _ => token
        };
        var n = f.Native with { RecoveryToken = kind == "missing" ? null : f.Tokens.Seal(token) };
        var e = f.Library.Snapshot.Entries[0] with { Sessions = [n.Project(f.Stored.Id)] };
        f.Disk.Main = SessionTestRig.Codec.Encode([e], SessionTestRig.Date); int before = f.Platform.Attachments;
        await f.Reopen(); Assert.Equal(SessionLifecycle.Incomplete, f.Native.Lifecycle); Assert.Equal(before, f.Platform.Attachments);
    }
    [Theory]
    [InlineData("ticks")]
    [InlineData("marker")]
    [InlineData("revision")]
    [InlineData("token")]
    [InlineData("id")]
    [InlineData("binding")]
    public async Task MalformedNativeNumbersAndBindingsFailValidation(string kind)
    {
        await using var f = new SessionTestRig(); await f.Start();
        var json = JsonNode.Parse(f.Disk.Main!)!; var s = json["games"]![0]!["sessions"]![0]!; var n = s["native"]!;
        switch (kind)
        {
            case "ticks": n["accumulated100ns"] = NativeSession.MaximumTicks + 1; break;
            case "marker": s["seconds"] = .5; break;
            case "revision": n["revision"] = -1; break;
            case "token": n["recoveryToken"] = new string('a', 32769); break;
            case "id": s["id"] = "not-an-operation"; break;
            case "binding": n["gameId"] = "other"; break;
        }
        Assert.Equal(LibraryAccess.InvalidInput, SessionTestRig.Codec.Inspect(JsonSerializer.SerializeToUtf8Bytes(json), SessionTestRig.Date).Access);
    }
    [Fact]
    public async Task NativeExtensionDoesNotLosePersonalUtf16OrLegacyHistory()
    {
        await using var f = new SessionTestRig(); await f.Start();
        var entry = f.Library.Snapshot.Entries[0] with { Notes = "x\ud800y", Name = "\udfffname", Sessions = f.Library.Snapshot.Entries[0].Sessions.Add(new("legacy", SessionTestRig.Date, SessionTestRig.Date, 10)) };
        var decoded = SessionTestRig.Codec.Decode(SessionTestRig.Codec.Encode([entry], SessionTestRig.Date))[0];
        Assert.Equal(entry.Notes, decoded.Notes); Assert.Equal(entry.Name, decoded.Name); Assert.Equal("legacy", decoded.Sessions[1].Id); Assert.Null(decoded.Sessions[1].Native);
    }
    private sealed class SimulatedCrashException : Exception;
    private sealed class LostReceiptPersistence(ILibraryPersistence inner) : ILibraryPersistence
    {
        public bool LoseReceipt;
        public byte[]? ReadMain() => inner.ReadMain();
        public byte[]? ReadBackup() => inner.ReadBackup();
        public async Task<PersistenceReceipt> SaveAsync(ReadOnlyMemory<byte> previous, ReadOnlyMemory<byte> candidate)
        { var receipt = await inner.SaveAsync(previous, candidate); if (LoseReceipt) throw new SimulatedCrashException(); return receipt; }
        public void Dispose() => inner.Dispose();
    }
}
