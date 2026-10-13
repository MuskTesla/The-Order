using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class TransientLaunchObservationTests
{
    [Theory]
    [InlineData(ProcessPresence.Inactive, true, false, ProcessTrackingState.RunningConfirmed)]
    [InlineData(ProcessPresence.Unknown, true, false, ProcessTrackingState.RunningLimited)]
    [InlineData(ProcessPresence.Active, true, false, ProcessTrackingState.RunningLimited)]
    [InlineData(ProcessPresence.Inactive, false, false, ProcessTrackingState.RunningLimited)]
    [InlineData(ProcessPresence.Inactive, true, true, ProcessTrackingState.RunningLimited)]
    public async Task FailedHelperAttachmentRequiresExactInactiveProofAndCompleteScope(ProcessPresence helper, bool complete, bool competingGameWindow, ProcessTrackingState expected)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new TransientPlatform(helper, complete, competingGameWindow);
        await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store, new()
        { PollInterval = TimeSpan.FromMilliseconds(10), RunningPollInterval = TimeSpan.FromMilliseconds(10), InitialTreeWindow = TimeSpan.FromSeconds(1), HandoffWindow = TimeSpan.FromMilliseconds(160), CandidateSettle = TimeSpan.FromMilliseconds(20) });
        Assert.True((await service.LaunchAsync("g")).ProcessCreated);
        await ProcessFactRemediationTests.Wait(() => Volatile.Read(ref platform.TreeReads) > 0);
        platform.RootExited = true;
        await ProcessFactRemediationTests.Wait(() => service.Status.Tracking == ProcessTrackingState.RunningLimited || service.Status.Current?.Pid == 900002);
        Assert.Equal(expected, service.Status.Tracking);
        if (expected == ProcessTrackingState.RunningConfirmed) Assert.Equal(900002, service.Status.Current!.Pid);
        else Assert.Equal(900001, service.Status.Current!.Pid);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
    }
    [Fact] public async Task ClosingDrainsPreparationsWhileTheirContinuationsRemovePendingTasks()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        for (int round = 0; round < 40; round++)
        {
            var platform = new ControlledPlatform();
            await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store);
            await using var vm = new LaunchViewModel(new YieldingLibrary(f.Library), service);
            var opened = vm.OpenAsync("g"); await Task.Yield();
            await Task.WhenAll(opened, vm.DisposeAsync().AsTask());
            vm.Leave(); vm.Cancel(); vm.Refresh();
            Assert.Equal(0, platform.Starts);
        }
    }
    [Theory]
    [InlineData(true, false, false, true, ProcessTrackingState.Exited)]
    [InlineData(true, true, false, true, ProcessTrackingState.RunningLimited)]
    [InlineData(false, false, false, false, ProcessTrackingState.RunningLimited)]
    [InlineData(true, false, true, false, ProcessTrackingState.RunningLimited)]
    public async Task WaitingCommandTransfersOnlyUniqueCoveredWindowAndRequiresEveryBirthForExit(bool complete, bool missedBirth, bool competingWindow, bool handsOff, ProcessTrackingState final)
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var script = Path.Combine(f.Game, "start.cmd"); File.WriteAllText(script, "@echo off\r\n");
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Executable = script }] };
        await f.Approve(); var platform = new WaitingCommandPlatform(complete, missedBirth, competingWindow);
        await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store, new()
        { PollInterval = TimeSpan.FromMilliseconds(10), RunningPollInterval = TimeSpan.FromMilliseconds(10), InitialTreeWindow = TimeSpan.FromMilliseconds(50), HandoffWindow = TimeSpan.FromMilliseconds(100), CandidateSettle = TimeSpan.FromMilliseconds(20) });
        Assert.True((await service.LaunchAsync("g")).ProcessCreated);
        if (handsOff) await ProcessFactRemediationTests.Wait(() => service.Status.Current?.Pid == 910002);
        else { await ProcessFactRemediationTests.Wait(() => Volatile.Read(ref platform.TreeReads) >= 8); Assert.Equal(910001, service.Status.Current!.Pid); }
        Assert.False(platform.Ended); Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
        platform.Ended = true;
        await ProcessFactRemediationTests.Wait(() => service.Status.Tracking is ProcessTrackingState.Exited or ProcessTrackingState.RunningLimited);
        Assert.Equal(final, service.Status.Tracking);
    }
    private sealed class YieldingLibrary(ILaunchLibrary inner) : ILaunchLibrary
    {
        public async Task<LaunchEntry> CaptureLaunchAsync(string id, CancellationToken cancellation) { await Task.Yield(); return await inner.CaptureLaunchAsync(id, cancellation); }
        public Task<T> WithLaunchAdmissionAsync<T>(GameLaunchIdentity id, string installation, Func<T> create, CancellationToken cancellation) => inner.WithLaunchAdmissionAsync(id, installation, create, cancellation);
    }
    private sealed class TransientPlatform(ProcessPresence helper, bool complete, bool competingGameWindow) : IGameProcessPlatform
    {
        public volatile bool RootExited; public int TreeReads;
        private ProcessIdentity root = null!, game = null!, auxiliary = null!;
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximum) => [];
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
        {
            root = new(900001, 100, snapshot.Executable.FinalPath, snapshot.Executable.Identity);
            game = new(900002, 150, Path.Combine(snapshot.Folder.FinalPath, "child_game.exe"), new("volume", "child"));
            auxiliary = new(900003, 151, competingGameWindow ? Path.Combine(snapshot.Folder.FinalPath, "other_game.exe") : @"C:\helper\transient.exe", new("volume", "helper"));
            return new Observer(() => new(root, RootExited ? ProcessTrackingState.Exited : ProcessTrackingState.RunningConfirmed, RootExited ? 200 : null, false), () => new Scope(() => new(complete, RootExited ? 1u : 2u, 3) { ActivePids = RootExited ? [game.Pid] : [root.Pid, game.Pid] }));
        }
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximum, int candidates)
        {
            return Interlocked.Increment(ref TreeReads) == 1
                ? new([new(root.Pid, new(game, ProcessTrackingState.RunningConfirmed, HasWindow: true)), new(root.Pid, new(auxiliary, ProcessTrackingState.RunningConfirmed, HasWindow: competingGameWindow))], true)
                : new([], true);
        }
        public IObservedProcess? Attach(ProcessIdentity id) => id == game ? new Observer(() => new(game, ProcessTrackingState.RunningConfirmed, HasWindow: true)) : null;
        public ProcessPresence RevalidateExternal(ProcessIdentity id) => id == auxiliary ? helper : ProcessPresence.Unknown;
    }
    private sealed class Observer(Func<ProcessObservation> query, Func<IProcessLifetimeScope>? scope = null) : IObservedProcess
    { public int Pid => query().Identity!.Pid; public ProcessObservation Query() => query(); public IProcessLifetimeScope? RetainLifetimeScope() => scope?.Invoke(); public void Dispose() { } }
    private sealed class Scope(Func<ProcessLifetimeObservation> query) : IProcessLifetimeScope
    { public ProcessLifetimeObservation Query() => query(); public void Dispose() { } }
    private sealed class WaitingCommandPlatform(bool complete, bool missedBirth, bool competingWindow) : IGameProcessPlatform
    {
        public volatile bool Ended; public int TreeReads;
        private ProcessIdentity root = null!, game = null!, other = null!;
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximum) => [];
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
        {
            root = new(910001, 100, @"C:\Windows\System32\cmd.exe", new("volume", "cmd"));
            game = new(910002, 150, Path.Combine(snapshot.Folder.FinalPath, "child_game.exe"), new("volume", "child"));
            other = new(910003, 151, Path.Combine(snapshot.Folder.FinalPath, "other_game.exe"), new("volume", "other"));
            return new Observer(() => Observe(root, false), () => new Scope(() => new(complete, Ended ? 0u : competingWindow ? 3u : 2u, missedBirth || competingWindow ? 3u : 2u)
            { ActivePids = Ended ? [] : competingWindow ? [root.Pid, game.Pid, other.Pid] : [root.Pid, game.Pid] }));
        }
        private ProcessObservation Observe(ProcessIdentity id, bool window) => new(id, Ended ? ProcessTrackingState.Exited : ProcessTrackingState.RunningConfirmed, Ended ? DateTimeOffset.UtcNow.ToFileTime() : null, !Ended && window);
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximum, int candidates)
        {
            Interlocked.Increment(ref TreeReads);
            return new(competingWindow ? [new(root.Pid, Observe(game, true)), new(root.Pid, Observe(other, true))] : [new(root.Pid, Observe(game, true))], true);
        }
        public IObservedProcess? Attach(ProcessIdentity id) => id == game || id == other ? new Observer(() => Observe(id, true)) : null;
        public ProcessPresence RevalidateExternal(ProcessIdentity id) => Ended ? ProcessPresence.Inactive : ProcessPresence.Active;
    }
}
