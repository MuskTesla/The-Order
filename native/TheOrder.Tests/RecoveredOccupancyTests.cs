using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

public sealed class RecoveredOccupancyTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("unknown")]
    [InlineData("attachFailure")]
    [InlineData("queryFailure")]
    [InlineData("unavailable")]
    [InlineData("fallbackFailure")]
    [InlineData("imageMismatch")]
    [InlineData("fileMismatch")]
    [InlineData("installationMismatch")]
    public async Task TerminalRecoveryStillBlocksWithoutWritingAccountingOrKilling(string state)
    {
        await using var f = await Fixture.Create();
        var process = f.Rig.Platform.Processes[71];
        switch (state)
        {
            case "unknown": process.State = ProcessTrackingState.Unknown; break;
            case "attachFailure": f.Probe.AttachFailure = true; break;
            case "queryFailure": f.Probe.QueryFailure = true; break;
            case "unavailable": f.Probe.Unavailable = true; break;
            case "fallbackFailure": f.Probe.Unavailable = f.Probe.FallbackFailure = true; break;
            case "imageMismatch": process.Identity = process.Identity with { ImagePath = @"C:\other.exe" }; break;
            case "fileMismatch": process.Identity = process.Identity with { ImageIdentity = new("changed", "file") }; break;
            case "installationMismatch": f.Rig.Files.Invalid = true; break;
        }
        var before = f.Rig.Disk.Main!.ToArray(); var writes = f.Rig.Disk.Writes;
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
            Assert.Equal(0, f.Rig.Platform.Handles);
        }
        Assert.Equal(before, f.Rig.Disk.Main); Assert.Equal(writes, f.Rig.Disk.Writes);
        Assert.Equal(1, f.Rig.Platform.Starts); Assert.NotEqual(ProcessTrackingState.Exited, process.State);
        Assert.Equal(SessionLifecycle.Incomplete, f.Engine.Snapshot.Session!.Native!.Lifecycle);
        Assert.Null(f.Engine.Snapshot.Session.Native.EndedAtUtc); Assert.Null(f.Engine.Snapshot.Session.Native.RecoveryToken);
        Assert.Equal(7, f.Engine.Snapshot.Session.Seconds); Assert.Null(f.Engine.Snapshot.Error);
        var b = f.Library.Snapshot.Entries.Single(e => e.Id == "b");
        Assert.Empty(b.Sessions); Assert.Equal(5, b.PlayCount); Assert.Equal(1000, b.TotalPlaySeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositiveExitOrPidGenerationReuseReleasesOnlyOldReservation(bool reusedPid)
    {
        await using var f = await Fixture.Create();
        var old = f.Rig.Platform.Processes[71];
        if (reusedPid) old.Identity = old.Identity with { CreationFileTime = old.Identity.CreationFileTime + 1 };
        else old.State = ProcessTrackingState.Exited;
        var result = await f.Engine.LaunchAsync("b"); Assert.True(result.ProcessCreated);
        Assert.Equal(2, f.Rig.Platform.Starts);
        var b = f.Library.Snapshot.Entries.Single(e => e.Id == "b");
        Assert.Single(b.Sessions); Assert.Equal(6, b.PlayCount); Assert.Equal(1000, b.TotalPlaySeconds);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
        Assert.Equal(2, f.Rig.Platform.Starts);
        Assert.Equal(7, f.Library.Snapshot.Entries.Single(e => e.Id == "g").Sessions.Single().Seconds);
    }

    [Fact]
    public async Task SessionAndPersonalTransactionsCannotRetireOccupancy()
    {
        await using var f = await Fixture.Create(stop: false);
        f.Rig.Disk.Fail = true; await f.Engine.StopObservingAsync(); Assert.NotNull(f.Engine.Snapshot.Error);
        f.Rig.Disk.Fail = false; await f.Engine.CheckpointAsync();
        await f.Library.UpdateAsync("g", new() { Notes = "after terminal checkpoint" });
        await f.Engine.CheckpointAsync(); await f.Engine.SampleAsync();
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
        // Removing terminal history is a library action, not evidence that the original process exited.
        await f.Library.DeleteAsync("g");
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
        f.Rig.Platform.Processes[71].State = ProcessTrackingState.Exited;
        Assert.True((await f.Engine.LaunchAsync("b")).ProcessCreated);
    }

    [Theory]
    [InlineData(ProcessTrackingState.RunningConfirmed)]
    [InlineData(ProcessTrackingState.Unknown)]
    [InlineData(ProcessTrackingState.Exited)]
    public async Task ConcurrentLaunchesShareRecheckAndNormalSingleOwnedAdmission(ProcessTrackingState state)
    {
        await using var f = await Fixture.Create(); f.Rig.Platform.Processes[71].State = state;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Probe.BeforeAttach = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var first = f.Engine.LaunchAsync("b");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(LaunchOutcome.AlreadyLaunching, (await f.Engine.LaunchAsync("b")).Outcome);
        }
        finally { release.Set(); }
        var result = await first; f.Probe.BeforeAttach = null;
        Assert.Equal(state == ProcessTrackingState.Exited ? LaunchOutcome.Started : LaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(state == ProcessTrackingState.Exited ? 2 : 1, f.Rig.Platform.Starts);
        Assert.Equal(state == ProcessTrackingState.Exited ? 1 : 0, f.Library.Snapshot.Entries.Single(e => e.Id == "b").Sessions.Length);
    }

    [Fact]
    public async Task RepeatedActiveAndUnknownProbesRetainReservationUntilPositiveExit()
    {
        await using var f = await Fixture.Create(); var process = f.Rig.Platform.Processes[71];
        for (int i = 0; i < 30; i++)
        {
            f.Probe.QueryFailure = i % 2 != 0;
            Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
            Assert.Equal(0, f.Rig.Platform.Handles);
        }
        f.Probe.QueryFailure = false; process.State = ProcessTrackingState.Exited;
        Assert.True((await f.Engine.LaunchAsync("b")).ProcessCreated);
        Assert.Equal(2, f.Rig.Platform.Starts);
        Assert.Single(f.Library.Snapshot.Entries.Single(e => e.Id == "b").Sessions);
    }

    [Fact]
    public async Task CancelledAttemptAndEngineCleanupDoNotLeakProbeHandles()
    {
        await using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(LaunchOutcome.Cancelled, (await f.Engine.LaunchAsync("b", cancellation: cancel.Token)).Outcome);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("b")).Outcome);
        await f.Engine.DisposeAsync(); Assert.Equal(0, f.Rig.Platform.Handles);
        Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Rig.Platform.Processes[71].State);
    }

    [Fact]
    public async Task OrdinaryOwnedStopObservingKeepsExistingFiveAReservation()
    {
        await using var rig = new SessionTestRig(Fixture.Document()); await Fixture.ApproveB(rig.Engine); await rig.Start();
        await rig.Engine.StopObservingAsync();
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await rig.Engine.LaunchAsync("b")).Outcome);
        Assert.Equal(1, rig.Platform.Starts); Assert.Empty(rig.Library.Snapshot.Entries.Single(e => e.Id == "b").Sessions);
    }

    private sealed class Fixture(SessionTestRig rig, TransactionalLibrary library, SessionEngine engine, ProbePlatform probe) : IAsyncDisposable
    {
        public SessionTestRig Rig => rig;
        public TransactionalLibrary Library => library;
        public SessionEngine Engine => engine;
        public ProbePlatform Probe => probe;
        public static byte[] Document() => SessionTestRig.Codec.Encode([SessionTestRig.Entry(), SessionTestRig.Entry("b") with
        { Installations = [new("ib", @"C:\occupancy-b", @"C:\occupancy-b\game.exe", [])], PreferredInstallationId = "ib" }], SessionTestRig.Date);
        public static async Task ApproveB(SessionEngine engine)
        { var prep = await engine.PrepareAsync("b"); await engine.ConfirmPositionAsync(prep.Snapshot!); }
        public static async Task<Fixture> Create(bool stop = true)
        {
            var rig = new SessionTestRig(Document()); await ApproveB(rig.Engine); await rig.Start();
            await rig.Engine.SampleAsync(); await rig.Advance(7); await rig.Engine.CheckpointAsync();
            await rig.Engine.DisposeAsync(); await rig.Library.DisposeAsync();
            var library = rig.Open(); var probe = new ProbePlatform(rig.Platform);
            var engine = new SessionEngine(library, rig.Files, probe, rig.Positions, rig.Clock, rig.Tokens, autoPoll: false);
            await engine.InitializeAsync(); Assert.Equal(SessionTracking.CurrentProcessOnly, engine.Snapshot.Tracking);
            if (stop) await engine.StopObservingAsync();
            return new(rig, library, engine, probe);
        }
        public async ValueTask DisposeAsync()
        { await engine.DisposeAsync(); await library.DisposeAsync(); await rig.DisposeAsync(); Assert.Equal(0, rig.Platform.Handles); }
    }

    private sealed class ProbePlatform(SessionFakePlatform inner) : IGameProcessPlatform
    {
        public bool AttachFailure, QueryFailure, Unavailable, FallbackFailure;
        public Action? BeforeAttach;
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation) => inner.Start(snapshot, cancellation);
        public IObservedProcess? Attach(ProcessIdentity identity)
        {
            BeforeAttach?.Invoke();
            if (AttachFailure) throw new IOException("synthetic identity query failure");
            if (Unavailable) return null;
            var observed = inner.Attach(identity);
            return observed is null ? null : new ProbeObserver(observed, () => QueryFailure);
        }
        public ProcessPresence RevalidateExternal(ProcessIdentity identity)
        {
            if (FallbackFailure) throw new IOException("synthetic generation query failure");
            if (Unavailable) return ProcessPresence.Unknown;
            // Match Windows' absence/generation/exit proof; image mismatch alone is not exit proof.
            if (!inner.Processes.TryGetValue(identity.Pid, out var state)
                || state.Identity.CreationFileTime != identity.CreationFileTime || state.State == ProcessTrackingState.Exited)
                return ProcessPresence.Inactive;
            return state.State == ProcessTrackingState.Unknown ? ProcessPresence.Unknown : ProcessPresence.Active;
        }
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximumProcesses, int maximumCandidates) => inner.Descendants(ancestors, maximumProcesses, maximumCandidates);
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximumProcesses) => inner.FindExisting(executable, maximumProcesses);
        private sealed class ProbeObserver(IObservedProcess inner, Func<bool> fail) : IObservedProcess
        {
            public int Pid => inner.Pid;
            public ProcessObservation Query() => fail() ? throw new IOException("synthetic retained query failure") : inner.Query();
            public void Dispose() => inner.Dispose();
        }
    }
}
