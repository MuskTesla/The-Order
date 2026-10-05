using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

public sealed class LifecyclePersistenceRetryTests(ITestOutputHelper output)
{
    private sealed class RecoveryCase : IAsyncDisposable
    {
        public readonly SessionTestRig Rig = new();
        public TransactionalLibrary Library = null!;
        public SessionEngine Engine = null!;
        public AppSessionHost Host = null!;
        public int Creates;
        public string Id = "";
        public Guid Writer;
        public long Total, Ticks;
        public async Task Prepare()
        {
            await Rig.Start(); await Rig.Engine.SampleAsync(); await Rig.Advance(7);
            Assert.True(await Rig.Engine.CheckpointForExitAsync());
            Id = Rig.Stored.Id; Total = Rig.Library.Snapshot.Entries[0].TotalPlaySeconds; Ticks = Rig.Native.Accumulated100ns;
            await Rig.Engine.DisposeAsync(); await Rig.Library.DisposeAsync();
            Library = Rig.Open(); Writer = Library.SessionWriterOwner;
            Host = new(() =>
            {
                Creates++;
                Engine = new(Library, Rig.Files, Rig.Platform, Rig.Positions, Rig.Clock, Rig.Tokens, autoPoll: false);
                return Task.FromResult<SessionEngine?>(Engine);
            }, () => Library.Snapshot);
        }
        public void AssertRecovered()
        {
            var e = Library.Snapshot.Entries[0]; var s = Assert.Single(e.Sessions);
            Assert.Equal(Id, s.Id); Assert.Equal("i", s.Native!.InstallationId); Assert.Equal("g", s.Native.GameId);
            Assert.Equal(Writer, s.Native.WriterOwner); Assert.Equal(1, s.Native.Recovery.Count);
            Assert.Equal(6, e.PlayCount); Assert.Equal(Total, e.TotalPlaySeconds); Assert.Equal(Ticks, s.Native.Accumulated100ns);
            Assert.Equal(1, Creates); Assert.Equal(1, Rig.Platform.Starts);
            Assert.Equal(ProcessTrackingState.RunningConfirmed, Rig.Platform.Processes[71].State);
        }
        public async ValueTask DisposeAsync()
        {
            Rig.Disk.BeforeSave = null; Rig.Disk.Fail = false;
            if (Host is not null) await Host.DisposeAsync();
            if (Library is not null) await Library.DisposeAsync();
            await Rig.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("io", false)] [InlineData("denied", false)] [InlineData("cancelled", false)]
    [InlineData("io", true)] [InlineData("denied", true)] [InlineData("cancelled", true)]
    public async Task FailedRecoveryCanRetrySameEngineOrExit(string failure, bool exit)
    {
        await using var f = new RecoveryCase(); await f.Prepare();
        f.Rig.Disk.BeforeSave = () => Task.FromException(failure switch
        {
            "denied" => new UnauthorizedAccessException(), "cancelled" => new OperationCanceledException(), _ => new IOException()
        });
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => f.Host.InitializeAsync());
            Assert.False(await f.Host.PrepareExitAsync()); Assert.False(f.Host.Ready); Assert.False(f.Host.CanMutate);
            Assert.False(f.Host.Closing); Assert.Equal(0, f.Rig.Platform.Handles);
            Assert.Equal(SessionLifecycle.RecoveryPending, f.Library.Snapshot.Entries[0].Sessions[0].Native!.Lifecycle);
        }
        var engine = f.Engine; f.Rig.Disk.BeforeSave = null; f.Rig.Clock.Advance(3600);
        if (exit) Assert.True(await f.Host.PrepareExitAsync()); else await f.Host.InitializeAsync();
        Assert.Same(engine, f.Engine); Assert.True(f.Host.Ready); Assert.Equal(!exit, f.Host.CanMutate); Assert.Null(f.Host.Error);
        f.AssertRecovered();
        Assert.Equal(exit ? SessionTracking.Detached : SessionTracking.CurrentProcessOnly, f.Engine.Snapshot.Tracking);
        var writes = f.Rig.Disk.Writes;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => f.Host.InitializeAsync()));
        Assert.Equal(writes, f.Rig.Disk.Writes); f.AssertRecovered();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentRetryAndExitShareOneRecoveryAttempt(bool exit)
    {
        await using var f = new RecoveryCase(); await f.Prepare();
        f.Rig.Disk.Fail = true; await Assert.ThrowsAsync<IOException>(() => f.Host.InitializeAsync()); f.Rig.Disk.Fail = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int attempts = 0;
        f.Rig.Disk.BeforeSave = async () => { Interlocked.Increment(ref attempts); entered.TrySetResult(); await release.Task; };
        var first = f.Host.InitializeAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 20).Select(_ => f.Host.InitializeAsync()).ToArray();
        var exiting = exit ? f.Host.PrepareExitAsync() : Task.FromResult(false);
        try
        {
            Assert.All(others, task => Assert.Same(first, task)); Assert.Equal(1, attempts);
            Assert.False(f.Host.CanMutate); Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(others); Assert.Equal(exit, await exiting); f.AssertRecovered();
        Assert.Equal(exit ? 2 : 1, attempts); Assert.Equal(!exit, f.Host.CanMutate);
    }

    [Fact]
    public async Task DisposalWaitsForRetryAndRejectsLaterAttemptsWithoutResurrection()
    {
        await using var f = new RecoveryCase(); await f.Prepare();
        f.Rig.Disk.Fail = true; await Assert.ThrowsAsync<IOException>(() => f.Host.InitializeAsync()); f.Rig.Disk.Fail = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Rig.Disk.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var retry = f.Host.InitializeAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = f.Host.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted); Assert.False(f.Host.CanMutate);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Host.InitializeAsync());
            Assert.False(await f.Host.PrepareExitAsync());
        }
        finally { release.TrySetResult(); }
        await retry; await disposal; Assert.False(f.Host.Ready); Assert.False(f.Host.CanMutate);
        Assert.Equal(0, f.Rig.Platform.Handles); Assert.Equal(1, f.Creates);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Host.InitializeAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task TerminalRetryPreservesCandidateAndFinalDelta(bool completed, bool ordinaryFirst)
    {
        await using var f = new SessionTestRig(); if (!completed) f.Tokens.BootId = null;
        await f.Start(); await f.Engine.SampleAsync(); await f.Advance(9);
        await using var host = new AppSessionHost(() => Task.FromResult<SessionEngine?>(f.Engine), () => f.Library.Snapshot);
        await host.InitializeAsync(); f.Disk.Fail = true;
        if (completed)
        {
            f.Platform.Processes[71].State = ProcessTrackingState.Exited;
            await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        }
        else Assert.False(await host.PrepareExitAsync());
        var pending = f.Engine.Snapshot.Session!; var life = completed ? SessionLifecycle.Completed : SessionLifecycle.Incomplete;
        Assert.Equal(life, pending.Native!.Lifecycle); Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
        Assert.Equal(9, pending.Seconds); Assert.Equal(1000, f.Library.Snapshot.Entries[0].TotalPlaySeconds);
        f.Clock.Advance(500);
        Assert.False(await host.PrepareExitAsync()); Assert.Equal(pending, f.Engine.Snapshot.Session);
        Assert.False(host.Closing); f.Disk.Fail = false;
        if (ordinaryFirst) await host.CheckpointAsync();
        var before = f.Disk.Writes; Assert.True(await host.PrepareExitAsync());
        Assert.Equal(before + (ordinaryFirst ? 0 : 1), f.Disk.Writes);
        var saved = f.Stored;
        Assert.Equal(pending.Native with { Revision = saved.Native!.Revision }, saved.Native);
        Assert.Equal(pending.Id, saved.Id); Assert.Equal(1009, f.Library.Snapshot.Entries[0].TotalPlaySeconds);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Single(f.Library.Snapshot.Entries[0].Sessions);
        Assert.Equal(SessionTiming.Stopped, f.Engine.Snapshot.Timing); Assert.Null(host.Error);
        var bytes = f.Disk.Main!.ToArray(); before = f.Disk.Writes;
        for (int i = 0; i < 20; i++) { Assert.True(await host.PrepareExitAsync()); await host.CheckpointAsync(); }
        Assert.Equal(before, f.Disk.Writes); Assert.Equal(bytes, f.Disk.Main);
        if (!completed) Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Platform.Processes[71].State);
    }

    [Fact]
    public async Task OlderCheckpointInFlightDoesNotAcknowledgeLaterTerminalCandidate()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(3);
        await using var host = new AppSessionHost(() => Task.FromResult<SessionEngine?>(f.Engine), () => f.Library.Snapshot);
        await host.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int saves = 0;
        f.Disk.BeforeSave = async () => { if (++saves == 1) { entered.TrySetResult(); await release.Task; } else throw new IOException(); };
        var checkpoint = host.CheckpointAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        release.SetResult(); await checkpoint; await terminal;
        Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle); Assert.Equal(SessionLifecycle.Completed, f.Engine.Snapshot.Session!.Native!.Lifecycle);
        f.Disk.BeforeSave = null; Assert.True(await host.PrepareExitAsync());
        Assert.Equal(SessionLifecycle.Completed, f.Native.Lifecycle); Assert.Equal(1003, f.Library.Snapshot.Entries[0].TotalPlaySeconds);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }

    [Fact]
    public async Task RecoveryFailureThenTerminalFailureUsesOneOwnerAndOneAccountingHistory()
    {
        await using var f = new RecoveryCase(); await f.Prepare();
        f.Rig.Disk.Fail = true; await Assert.ThrowsAsync<IOException>(() => f.Host.InitializeAsync());
        f.Rig.Disk.Fail = false; await f.Host.InitializeAsync(); f.AssertRecovered();
        await f.Engine.SampleAsync(); f.Rig.Clock.Advance(5); await f.Engine.SampleAsync();
        f.Rig.Disk.Fail = true; await f.Engine.StopObservingAsync();
        var pending = f.Engine.Snapshot.Session!;
        Assert.Equal(SessionLifecycle.Incomplete, pending.Native!.Lifecycle);
        Assert.Equal(SessionLifecycle.Open, f.Library.Snapshot.Entries[0].Sessions[0].Native!.Lifecycle);
        f.Rig.Disk.Fail = false; Assert.True(await f.Host.PrepareExitAsync());
        var e = f.Library.Snapshot.Entries[0]; var s = Assert.Single(e.Sessions);
        Assert.Equal(f.Id, s.Id); Assert.Equal(pending.Native with { Revision = s.Native!.Revision }, s.Native);
        Assert.Equal(6, e.PlayCount); Assert.Equal(f.Total + 5, e.TotalPlaySeconds);
        Assert.Equal(1, f.Creates); Assert.Equal(f.Writer, s.Native.WriterOwner); Assert.Equal(1, s.Native.Recovery.Count);
        Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Rig.Platform.Processes[71].State);
    }

    [Fact]
    public async Task RepeatedFailuresReleaseTemporaryObserversAndDisposeLifetimeOwner()
    {
        await using var f = new RecoveryCase(); await f.Prepare(); f.Rig.Disk.Fail = true;
        for (int i = 0; i < 40; i++)
        {
            await Assert.ThrowsAsync<IOException>(() => f.Host.InitializeAsync());
            Assert.False(await f.Host.PrepareExitAsync()); Assert.Equal(0, f.Rig.Platform.Handles);
        }
        f.Rig.Disk.Fail = false; await f.Host.InitializeAsync(); f.AssertRecovered();
        f.Rig.Disk.Fail = true; await f.Engine.StopObservingAsync();
        for (int i = 0; i < 40; i++) { Assert.False(await f.Host.PrepareExitAsync()); Assert.Equal(0, f.Rig.Platform.Handles); }
        f.Rig.Disk.Fail = false; Assert.True(await f.Host.PrepareExitAsync()); await f.Host.DisposeAsync();
        Assert.Equal(0, f.Rig.Platform.Handles); Assert.Equal(1, f.Creates);
        // The only engine lease is released; cleanup does not kill the synthetic process.
        await using var replacement = new SessionEngine(f.Library, f.Rig.Files, f.Rig.Platform, f.Rig.Positions, f.Rig.Clock, f.Rig.Tokens, autoPoll: false);
        Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Rig.Platform.Processes[71].State);
        output.WriteLine("80 failed recovery attempts + 40 terminal exit retries: temporary handles always 0; one engine/writer; final release verified.");
    }

    [Theory, Trait("Category", "FaultInjection")]
    [InlineData("before-replace", false)] [InlineData("after-replace", false)] [InlineData("lost-receipt", false)]
    [InlineData("before-replace", true)] [InlineData("after-replace", true)] [InlineData("lost-receipt", true)]
    public async Task TerminalExitRetriesAtomicStoreAndLostReceiptWithoutReplayingDelta(string mode, bool completed)
    {
        await using var f = new SessionTestRig();
        using var profile = new TransactionProfile(f.Disk.Main!);
        var probe = new TerminalProbe(mode);
        var persistence = new ReceiptPersistence(new IsolatedLibraryPersistence(profile.Root, probe));
        await using var library = TransactionalLibrary.Open(SessionTestRig.Codec, persistence);
        f.Tokens.BootId = null;
        var engine = new SessionEngine(library, f.Files, f.Platform, f.Positions, f.Clock, f.Tokens, autoPoll: false,
            launchOptions: new() { RunningPollInterval = TimeSpan.FromDays(1), PollInterval = TimeSpan.FromDays(1) });
        await using var host = new AppSessionHost(() => Task.FromResult<SessionEngine?>(engine), () => library.Snapshot);
        await host.InitializeAsync(); var preview = await host.PrepareAsync("g"); await host.ConfirmPositionAsync(preview.Snapshot!);
        Assert.True((await host.LaunchAsync("g")).ProcessCreated);
        for (int i = 0; i < 100 && engine.LaunchStatus.Tracking == ProcessTrackingState.Attaching; i++) await Task.Delay(5);
        await engine.DrainAsync(); await engine.SampleAsync(); f.Clock.Advance(8); await engine.SampleAsync();
        probe.Armed = true; persistence.LoseReceipt = mode == "lost-receipt";
        if (completed)
        {
            await engine.ObserveFactAsync(new(ProcessEventKind.ProcessExited, f.Platform.Snapshot!.OperationId,
                null, engine.Snapshot.Current, f.Clock.Utc, f.Clock.Utc, f.Clock.Utc));
        }
        else Assert.Equal(mode == "after-replace", await host.PrepareExitAsync());
        var pending = engine.Snapshot.Session!;
        var expectedLife = completed ? SessionLifecycle.Completed : SessionLifecycle.Incomplete;
        Assert.Equal(expectedLife, pending.Native!.Lifecycle);
        var actualDisk = SessionTestRig.Codec.Decode(File.ReadAllBytes(Path.Combine(profile.Root, "library.json")))[0];
        Assert.Equal(mode == "before-replace" ? SessionLifecycle.Open : expectedLife, actualDisk.Sessions[0].Native!.Lifecycle);
        Assert.Equal(mode == "before-replace" ? 1000 : 1008, actualDisk.TotalPlaySeconds);
        f.Clock.Advance(700); probe.Armed = false; persistence.LoseReceipt = false;
        Assert.True(await host.PrepareExitAsync()); var saved = library.Snapshot.Entries[0].Sessions[0];
        Assert.Equal(pending.Native with { Revision = saved.Native!.Revision }, saved.Native);
        Assert.Equal(1008, library.Snapshot.Entries[0].TotalPlaySeconds); Assert.Equal(6, library.Snapshot.Entries[0].PlayCount);
        var writes = persistence.Writes;
        for (int i = 0; i < 10; i++) { Assert.True(await host.PrepareExitAsync()); await host.CheckpointAsync(); }
        Assert.Equal(writes, persistence.Writes);
        var disk = SessionTestRig.Codec.Decode(File.ReadAllBytes(Path.Combine(profile.Root, "library.json")))[0];
        Assert.Equal(1008, disk.TotalPlaySeconds); Assert.Equal(6, disk.PlayCount); Assert.Single(disk.Sessions);
        Assert.Equal(saved.Native, disk.Sessions[0].Native);
        output.WriteLine($"{mode}, completed={completed}: final 8-second delta exactly once; repeated exits/checkpoints add no writes.");
    }
    private sealed class TerminalProbe(string mode) : IPersistenceProbe
    {
        public bool Armed;
        public Task ReachAsync(PersistenceTarget target, PersistenceStage stage)
            => Armed && target == PersistenceTarget.Main && stage == (mode == "before-replace" ? PersistenceStage.BeforeReplace : PersistenceStage.AfterReplace)
                && mode != "lost-receipt" ? throw new IOException("synthetic terminal persistence failure") : Task.CompletedTask;
    }
    private sealed class ReceiptPersistence(ILibraryPersistence inner) : ILibraryPersistence
    {
        public bool LoseReceipt;
        public int Writes;
        public byte[]? ReadMain() => inner.ReadMain();
        public byte[]? ReadBackup() => inner.ReadBackup();
        public async Task<PersistenceReceipt> SaveAsync(ReadOnlyMemory<byte> previous, ReadOnlyMemory<byte> candidate)
        {
            Writes++; var receipt = await inner.SaveAsync(previous, candidate);
            if (LoseReceipt) throw new IOException("synthetic lost receipt after actual replace");
            return receipt;
        }
        public void Dispose() => inner.Dispose();
    }
}
