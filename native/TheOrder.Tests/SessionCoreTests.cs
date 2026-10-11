using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

public sealed class SessionCoreTests
{
    [Fact]
    public async Task ActualLaunchOperationOwnsOneDurableSessionIncludingDuplicateResultAndFacts()
    {
        await using var f = new SessionTestRig();
        var preview = await f.Engine.PrepareAsync("g"); var started = await f.Start();
        Assert.NotEqual(preview.Snapshot!.OperationId, started.Snapshot!.OperationId);
        Assert.Equal(started.Snapshot.OperationId.ToString(), f.Stored.Id);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Equal(0, f.Stored.Seconds);
        await f.Engine.ObserveLaunchStatusAsync(started);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessStarted));
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessTrackingAttached));
        Assert.Single(f.Library.Snapshot.Entries[0].Sessions); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
        Assert.Equal(DateTimeOffset.FromFileTime(f.Platform.Root.CreationFileTime), f.Native.StartedAtUtc);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SuccessfulCreationWithUnknownIdentityOrLeaseFailureStillCounts(bool unknown, bool leaseFailure)
    {
        await using var f = new SessionTestRig(); f.Platform.UnknownStart = unknown; f.Platform.ThrowScope = leaseFailure;
        var result = await f.Start(); Assert.True(result.ProcessCreated);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Null(f.Native.StartedAtUtc);
        await f.Advance(200); Assert.Equal(0, f.Stored.Seconds); Assert.Null(f.Engine.Snapshot.Current);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrExternalLaunchHasNoOwnedHistory(bool external)
    {
        await using var f = new SessionTestRig(); f.Platform.External = external; f.Platform.Deny = !external;
        var status = await f.Start(); Assert.False(status.ProcessCreated);
        Assert.Empty(f.Library.Snapshot.Entries[0].Sessions); Assert.Equal(5, f.Library.Snapshot.Entries[0].PlayCount);
        Assert.Equal(0, f.Platform.Starts);
    }
    [Fact]
    public async Task ImmediateAuthoritativeExitCountsZeroSecondSessionAndDuplicateExitDoesNotWrite()
    {
        await using var f = new SessionTestRig(); await f.Start(); var end = f.Clock.Utc.AddMilliseconds(1);
        var exit = f.Fact(ProcessEventKind.ProcessExited, exit: end);
        await f.Engine.ObserveFactAsync(exit); int writes = f.Disk.Writes;
        await f.Engine.ObserveFactAsync(exit);
        Assert.Equal(writes, f.Disk.Writes); Assert.Equal(SessionLifecycle.Completed, f.Native.Lifecycle);
        Assert.Equal(end, f.Native.EndedAtUtc); Assert.Equal(0, f.Stored.Seconds); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Fact]
    public async Task FirstWriteFailureIsVisibleAndRetryNeverRestartsOrDoubleCounts()
    {
        await using var f = new SessionTestRig(); f.Disk.Fail = true;
        Assert.True((await f.Start()).ProcessCreated);
        Assert.Empty(f.Library.Snapshot.Entries[0].Sessions); Assert.Equal(5, f.Library.Snapshot.Entries[0].PlayCount);
        Assert.False(f.Engine.Snapshot.Durable); Assert.NotNull(f.Engine.Snapshot.Error);
        await f.Advance(500); Assert.Equal(0, f.Engine.Snapshot.Session!.Seconds);
        f.Disk.Fail = false; await f.Engine.CheckpointAsync(); await f.Engine.CheckpointAsync();
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Equal(1, f.Platform.Starts); Assert.Null(f.Engine.Snapshot.Error);
    }
    [Fact]
    public async Task ExitQueuedAfterFailedStartCanBeDurablyRetriedAsOneInvocation()
    {
        await using var f = new SessionTestRig(); f.Disk.Fail = true; await f.Start();
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        Assert.Empty(f.Library.Snapshot.Entries[0].Sessions);
        f.Disk.Fail = false; await f.Engine.CheckpointAsync();
        Assert.Equal(SessionLifecycle.Completed, f.Native.Lifecycle); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Theory]
    [InlineData(86400)]
    [InlineData(-86400)]
    [InlineData(0)]
    public async Task WallClockAndTimezoneNeverEnterDuration(int utcJump)
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync();
        f.Clock.Utc = f.Clock.Utc.AddSeconds(utcJump).ToOffset(TimeSpan.FromHours(8));
        await f.Advance(10.4); await f.Advance(.7); await f.Engine.CheckpointAsync();
        Assert.Equal(111_000_000, f.Native.Accumulated100ns); Assert.Equal(11, f.Stored.Seconds);
        Assert.Equal(1011, f.Library.Snapshot.Entries[0].TotalPlaySeconds);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        Assert.Equal(f.Clock.Utc, f.Native.EndedAtUtc); Assert.Equal(11, f.Stored.Seconds);
    }
    [Fact]
    public async Task DelayedExitDoesNotCountObservationWindowAndGoneWaitsForFiveA()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(10);
        f.Platform.Processes[71].State = ProcessTrackingState.Exited;
        await f.Advance(1); Assert.Equal(SessionLifecycle.Open, f.Engine.Snapshot.Session!.Native!.Lifecycle);
        Assert.Equal(SessionTiming.AwaitHandoff, f.Engine.Snapshot.Timing);
        var exit = f.Clock.Utc; await f.Advance(6);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: exit));
        Assert.Equal(10, f.Stored.Seconds); Assert.Equal(exit, f.Native.EndedAtUtc);
    }
    [Fact]
    public async Task InvalidExitTimestampAndOldGenerationCannotComplete()
    {
        await using var f = new SessionTestRig(); await f.Start();
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited));
        Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, current: f.Platform.Root with { CreationFileTime = 1 }, exit: f.Clock.Utc));
        Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
    }
    [Fact]
    public async Task MultiStepHandoffBridgesOnceAndNeverCreatesSecondSession()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(10);
        var id = f.Stored.Id; var start = f.Native.StartedAtUtc; var root = f.Platform.Root;
        var child = f.Platform.Add(73).Identity; f.Clock.Advance(2);
        var handoff = f.Fact(ProcessEventKind.ProcessHandoff, child, root);
        await f.Engine.ObserveFactAsync(handoff); await f.Engine.ObserveFactAsync(handoff);
        Assert.Equal(12, f.Stored.Seconds);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, root, exit: f.Clock.Utc));
        Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
        await f.Engine.SampleAsync(); var final = f.Platform.Add(74).Identity; f.Clock.Advance(1);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessHandoff, final, child));
        Assert.Equal(13, f.Stored.Seconds); Assert.Equal(id, f.Stored.Id); Assert.Equal(start, f.Native.StartedAtUtc);
        Assert.Equal("i", f.Native.InstallationId); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Theory]
    [InlineData(7, false)]
    [InlineData(2, true)]
    public async Task UnverifiedOrPausedHandoffGapIsNotBridged(int gap, bool paused)
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(10);
        if (paused) await f.Engine.PauseAsync();
        var child = f.Platform.Add(73).Identity; f.Clock.Advance(gap);
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessHandoff, child, f.Platform.Root));
        Assert.Equal(10, f.Stored.Seconds); Assert.Equal(paused, f.Native.UserPaused);
    }
    [Fact]
    public async Task LimitedReliableCountsUnknownFreezesAndSameIdentityResumesWithoutGap()
    {
        await using var f = new SessionTestRig(); await f.Start();
        await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.TrackingLimited)); await f.Engine.SampleAsync(); await f.Advance(10);
        f.Platform.Processes[71].State = ProcessTrackingState.Unknown; await f.Advance(200);
        Assert.Equal(10, f.Stored.Seconds);
        f.Platform.Processes[71].State = ProcessTrackingState.RunningConfirmed; await f.Engine.SampleAsync(); await f.Advance(5);
        await f.Engine.CheckpointAsync(); Assert.Equal(15, f.Stored.Seconds);
        f.Platform.Processes[71].State = ProcessTrackingState.Exited; await f.Engine.SampleAsync();
        Assert.Equal(SessionLifecycle.Incomplete, f.Native.Lifecycle); Assert.Null(f.Native.EndedAtUtc);
    }
    [Fact]
    public async Task PauseResumeAndExitAreIndependentFromCountAndGameLifetime()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(5);
        await f.Engine.PauseAsync(); await f.Advance(100); await f.Engine.ResumeAsync(); await f.Engine.SampleAsync(); await f.Advance(3);
        await f.Engine.PauseAsync(); await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc));
        Assert.Equal(8, f.Stored.Seconds); Assert.Equal(SessionLifecycle.Completed, f.Native.Lifecycle);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Platform.Processes[71].State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendAndHibernateExcludeGapAndPreservePause(bool paused)
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(5);
        if (paused) await f.Engine.PauseAsync();
        await f.Engine.SuspendAsync(); f.Clock.Utc = f.Clock.Utc.AddHours(8); await f.Engine.SampleAsync();
        await f.Engine.ResumeFromSuspendAsync(); await f.Engine.ResumeFromSuspendAsync();
        await f.Engine.SampleAsync(); await f.Advance(3); await f.Engine.CheckpointAsync();
        Assert.Equal(paused ? 5 : 8, f.Stored.Seconds); Assert.Equal(paused, f.Native.UserPaused);
        Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount);
    }
    [Theory]
    [InlineData(ProcessTrackingState.Unknown)]
    [InlineData(ProcessTrackingState.Exited)]
    public async Task ResumeUnknownOrGoneNeverGuessesFinalExit(ProcessTrackingState state)
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SuspendAsync();
        f.Platform.Processes[71].State = state; await f.Engine.ResumeFromSuspendAsync(); await f.Advance(100);
        Assert.Equal(0, f.Stored.Seconds); Assert.Equal(SessionLifecycle.Open, f.Native.Lifecycle);
        if (state == ProcessTrackingState.Exited)
        { await f.Engine.ObserveFactAsync(f.Fact(ProcessEventKind.ProcessExited, exit: f.Clock.Utc)); Assert.Equal(SessionLifecycle.Completed, f.Native.Lifecycle); }
    }
    [Fact]
    public async Task MissedPowerNotificationAndClockReversalBreakIntervals()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(4);
        f.Clock.Epoch++; f.Clock.Advance(200); await f.Engine.SampleAsync();
        await f.Engine.SampleAsync(); await f.Advance(2); f.Clock.Awake -= 100_000_000; await f.Engine.SampleAsync();
        await f.Engine.CheckpointAsync(); Assert.Equal(6, f.Stored.Seconds);
    }
    [Fact]
    public async Task CheckpointThresholdHardCapAndSlowDiskNeverExtrapolateAcrossWriterWait()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync();
        int writes = f.Disk.Writes; await f.Advance(54.9); Assert.Equal(writes, f.Disk.Writes);
        f.Disk.Fail = true; await f.Advance(1000);
        Assert.Equal(600_000_000, f.Engine.Snapshot.Session!.Native!.Accumulated100ns); Assert.Equal(0, f.Native.Accumulated100ns);
        await f.Advance(1000); Assert.Equal(60, f.Engine.Snapshot.Session.Seconds);
        f.Disk.Fail = false; await f.Engine.CheckpointAsync(); Assert.Equal(60, f.Stored.Seconds);
        await f.Engine.SampleAsync();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Disk.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var saving = f.Advance(55); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Clock.Advance(500); release.SetResult(); await saving;
        f.Disk.BeforeSave = null; await f.Engine.SampleAsync(); await f.Advance(2); await f.Engine.CheckpointAsync();
        Assert.Equal(117, f.Stored.Seconds);
    }
    [Fact]
    public async Task StopObservingDoesNotClaimExitOrReleaseFiveAAdmission()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.StopObservingAsync();
        Assert.Equal(SessionLifecycle.Incomplete, f.Native.Lifecycle); Assert.Null(f.Native.RecoveryToken);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("g")).Outcome); Assert.Equal(1, f.Platform.Starts);
    }
    [Fact]
    public async Task ManagerExitCheckpointIsRecoverableAndSaveFailureIsVisible()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(3);
        f.Disk.Fail = true; Assert.False(await f.Engine.CheckpointForExitAsync()); Assert.NotNull(f.Engine.Snapshot.Error);
        f.Disk.Fail = false; Assert.True(await f.Engine.CheckpointForExitAsync());
        Assert.Equal(SessionLifecycle.RecoveryPending, f.Native.Lifecycle); Assert.Null(f.Native.EndedAtUtc);
        Assert.Equal(3, f.Stored.Seconds); Assert.Equal(ProcessTrackingState.RunningConfirmed, f.Platform.Processes[71].State);
    }
    [Fact]
    public async Task PreferredAndPersonalEditsSurviveCheckpointButActiveDeletionAndReplacementAreRejected()
    {
        await using var f = new SessionTestRig(); await f.Start();
        await f.Library.UpdateAsync("g", new() { Notes = "personal", PreferredInstallationId = new(true, null) });
        await f.Engine.SampleAsync(); await f.Advance(3); await f.Engine.CheckpointAsync();
        Assert.Equal("personal", f.Library.Snapshot.Entries[0].Notes); Assert.Equal("i", f.Native.InstallationId);
        await Assert.ThrowsAsync<SessionConflictException>(() => f.Library.DeleteAsync("g"));
        await Assert.ThrowsAsync<SessionConflictException>(() => f.Library.RemoveInstallationAsync("g", "i"));
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Engine.LaunchAsync("other-game")).Outcome);
    }
    [Fact]
    public async Task SameProcessRecoveryHasNoOfflineGapOrExtraCountAndLosesScopeCapability()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(7); await f.Engine.CheckpointAsync();
        var id = f.Stored.Id; f.Clock.Advance(500); await f.Reopen();
        Assert.Equal(id, f.Stored.Id); Assert.Equal(1, f.Native.Recovery.Count); Assert.Equal(SessionTracking.CurrentProcessOnly, f.Engine.Snapshot.Tracking);
        await f.Engine.InitializeAsync(); Assert.Equal(1, f.Native.Recovery.Count);
        await f.Engine.SampleAsync(); await f.Advance(3); await f.Engine.CheckpointAsync();
        Assert.Equal(10, f.Stored.Seconds); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Equal(1010, f.Library.Snapshot.Entries[0].TotalPlaySeconds);
        f.Platform.Processes[71].State = ProcessTrackingState.Exited; await f.Engine.SampleAsync();
        Assert.Equal(SessionReason.RecoveredProcessEnded, f.Native.Reason); Assert.Null(f.Native.EndedAtUtc);
    }
    [Theory]
    [InlineData("generation")]
    [InlineData("pid")]
    [InlineData("image")]
    [InlineData("file")]
    [InlineData("gone")]
    [InlineData("boot")]
    [InlineData("profile")]
    [InlineData("token")]
    [InlineData("installation")]
    public async Task RecoveryRejectsEachIdentityMismatch(string mismatch)
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Advance(4); await f.Engine.CheckpointAsync();
        var root = f.Platform.Processes[71];
        switch (mismatch)
        {
            case "generation": root.Identity = root.Identity with { CreationFileTime = root.Identity.CreationFileTime + 1 }; break;
            case "pid": f.Platform.Processes.Remove(71); f.Platform.Add(72); break;
            case "image": root.Identity = root.Identity with { ImagePath = "other.exe" }; break;
            case "file": root.Identity = root.Identity with { ImageIdentity = new("different", "file") }; break;
            case "gone": root.State = ProcessTrackingState.Exited; break;
            case "boot": f.Tokens.BootId++; break;
            case "profile": f.Tokens.ProfileIdentity = new("other", "profile"); break;
            case "token": f.Tokens.Reject = true; break;
            case "installation": f.Files.Invalid = true; break;
        }
        await f.Reopen(); Assert.Equal(SessionLifecycle.Incomplete, f.Native.Lifecycle);
        Assert.Equal(4, f.Stored.Seconds); Assert.Equal(6, f.Library.Snapshot.Entries[0].PlayCount); Assert.Null(f.Native.EndedAtUtc); Assert.Null(f.Native.RecoveryToken);
    }
    [Fact]
    public async Task PauseSurvivesRecoveryAndIndependentWriterCannotReplayOldCheckpoint()
    {
        await using var f = new SessionTestRig(); await f.Start(); await f.Engine.PauseAsync(); var old = f.Stored;
        await f.Reopen(); Assert.True(f.Native.UserPaused); await f.Advance(400); Assert.Equal(0, f.Stored.Seconds);
        var identity = (await f.Library.CaptureLaunchAsync("g")).Identity;
        await Assert.ThrowsAsync<SessionConflictException>(() => f.Library.CommitSessionAsync(identity, old));
        await f.Engine.ResumeAsync(); await f.Engine.SampleAsync(); await f.Advance(2); await f.Engine.CheckpointAsync(); Assert.Equal(2, f.Stored.Seconds);
    }
    [Fact]
    public async Task RepeatedLifecyclesDisposeEverySamplerHandle()
    {
        for (int i = 0; i < 20; i++)
        {
            var f = new SessionTestRig(); await f.Start(); await f.Engine.SampleAsync(); await f.Reopen(); await f.Engine.SampleAsync();
            await f.DisposeAsync(); Assert.Equal(0, f.Platform.Handles);
        }
    }
}
