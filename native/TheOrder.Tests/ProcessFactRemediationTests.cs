using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class ProcessFactRemediationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DelayedMissingIntermediaryKeepsKernelScopeOccupiedUntilFinalGameExits()
    {
        await using var f = new LaunchFixture(); f.Set("delayed-gap"); await f.Approve();
        var observed = new WindowObservationPlatform(f.Platform);
        await using var service = new GameLaunchService(f.Library, f.Identity, observed, f.Store, Fast() with { InitialTreeWindow = TimeSpan.FromSeconds(1) });
        var events = new ConcurrentQueue<ProcessEvent>(); service.ProcessChanged += events.Enqueue;
        await service.LaunchAsync("g"); await f.Report("run");
        await observed.WindowObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200); // Tree polling is now off, but kernel descendant membership remains on.
        await File.WriteAllTextAsync(Path.Combine(f.Reports, "run.spawn"), "create a short-lived intermediary");
        var child = await f.Report("run-helper-game");
        var helper = await f.Report("run-helper");
        try { using var helperProcess = Process.GetProcessById(helper.GetProperty("pid").GetInt32()); await helperProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ArgumentException) { /* The short-lived helper has already been removed from the process table. */ }
        using var game = Process.GetProcessById(child.GetProperty("pid").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(f.Reports, "run.stop"), "normal launcher close");
        await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningLimited);
        Assert.False(game.HasExited);
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessExited);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
        await File.WriteAllTextAsync(Path.Combine(f.Reports, "run-helper-game.stop"), "normal game close");
        await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Proven empty scope releases admission, but cannot reconstruct an unobserved final exit time.
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Arguments = [f.Reports, "game", "again"] }] };
        var again = await service.LaunchAsync("g"); Assert.True(again.ProcessCreated); await f.Report("again");
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessExited);
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "M1-delayed-intermediary", childPid = game.Id, duplicateBlocked = true, revalidationReleased = true, fabricatedExit = false }));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(6000)]
    public async Task DirectExitPreservesOsOccurrenceDespitePublicationDelay(int delay)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var observed = new WindowObservationPlatform(f.Platform);
        await using var service = new GameLaunchService(f.Library, f.Identity, observed, f.Store,
            Fast() with { InitialTreeWindow = TimeSpan.FromSeconds(2), HandoffWindow = TimeSpan.FromMilliseconds(delay) });
        var events = new ConcurrentQueue<ProcessEvent>(); service.ProcessChanged += events.Enqueue;
        var started = await service.LaunchAsync("g"); await f.Report("run");
        using var retained = f.Platform.Attach(started.Initial!)!;
        await observed.WindowObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Stop(); await Wait(() => service.Status.Tracking == ProcessTrackingState.Exited);
        var fact = Assert.Single(events, e => e.Kind == ProcessEventKind.ProcessExited);
        var actual = DateTimeOffset.FromFileTime(retained.Query().ExitFileTime!.Value).ToUniversalTime();
        Assert.Equal(actual, fact.ExitOccurredAtUtc); Assert.Equal(actual, fact.OccurredAtUtc);
        Assert.Equal(actual, service.Status.ExitOccurredAtUtc);
        Assert.True(fact.ObservedAtUtc - actual >= TimeSpan.FromMilliseconds(delay));
        await service.DisposeAsync(); Assert.Single(events, e => e.Kind == ProcessEventKind.ProcessExited);
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "M4-direct-exit", delay, actual, fact.ObservedAtUtc, fact.ExitOccurredAtUtc }));
    }

    [Fact]
    public async Task HandoffFinalExitUsesChildTimeAndShutdownDoesNotAddExit()
    {
        await using var f = new LaunchFixture(); f.Set("launcher"); await f.Approve();
        var events = new ConcurrentQueue<ProcessEvent>(); f.Service.ProcessChanged += events.Enqueue;
        var started = await f.Service.LaunchAsync("g");
        using var parent = f.Platform.Attach(started.Initial!)!;
        await Wait(() => f.Service.Status.Tracking == ProcessTrackingState.RunningLimited || f.Service.Status.Current is { } current && current != started.Initial);
        Assert.True(f.Service.Status.Tracking == ProcessTrackingState.RunningConfirmed, JsonSerializer.Serialize(f.Service.Status));
        using var child = f.Platform.Attach(f.Service.Status.Current!)!;
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessExited);
        await f.Stop(); await Wait(() => f.Service.Status.Tracking == ProcessTrackingState.Exited);
        var fact = Assert.Single(events, e => e.Kind == ProcessEventKind.ProcessExited);
        Assert.Equal(DateTimeOffset.FromFileTime(child.Query().ExitFileTime!.Value).ToUniversalTime(), fact.ExitOccurredAtUtc);
        Assert.NotEqual(DateTimeOffset.FromFileTime(parent.Query().ExitFileTime!.Value).ToUniversalTime(), fact.ExitOccurredAtUtc);
        await f.Service.DisposeAsync(); Assert.Single(events, e => e.Kind == ProcessEventKind.ProcessExited);
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "M4-handoff-child-exit", oldPid = started.Initial!.Pid, finalPid = fact.Current!.Pid, fact.ExitOccurredAtUtc, fact.ObservedAtUtc }));
    }

    [Fact]
    public async Task RealExternalAlreadyRunningCanExitAndRelaunchWithoutSessionTakeover()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        using var external = f.Platform.Start((await f.Service.PrepareAsync("g", null)).Snapshot!, default);
        await f.Report("run");
        var events = new ConcurrentQueue<ProcessEvent>(); f.Service.ProcessChanged += events.Enqueue;
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Service.LaunchAsync("g")).Outcome);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Service.LaunchAsync("g")).Outcome);
        Assert.Empty(events);
        await f.Stop();
        Assert.Equal(ProcessPresence.Inactive, f.Platform.RevalidateExternal(external.Query().Identity!));
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Arguments = [f.Reports, "game", "again"] }] };
        var launched = await f.Service.LaunchAsync("g"); Assert.True(launched.ProcessCreated); await f.Report("again");
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessExited);
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "external-exit-relaunch", oldPid = external.Pid, newPid = launched.Initial!.Pid, sessionTakenOver = false }));
    }

    [Theory]
    [InlineData(ProcessPresence.Active, false)]
    [InlineData(ProcessPresence.Unknown, false)]
    [InlineData(ProcessPresence.Inactive, true)]
    public async Task ExternalRevalidationDoesNotInterpretUnknownAsInactive(ProcessPresence presence, bool launch)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ExternalPlatform(presence);
        await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store, Fast());
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
        var next = await service.LaunchAsync("g"); Assert.Equal(launch, next.ProcessCreated); Assert.Equal(launch ? 1 : 0, platform.Starts);
        Assert.NotEqual(ProcessTrackingState.Exited, service.Status.Tracking);
    }

    [Fact]
    public async Task RetainedExternalIdentityRejectsDifferentGeneration()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        using var process = f.Platform.Start((await f.Service.PrepareAsync("g", null)).Snapshot!, default); await f.Report("run");
        var identity = process.Query().Identity!;
        Assert.Equal(ProcessPresence.Active, f.Platform.RevalidateExternal(identity));
        Assert.Equal(ProcessPresence.Inactive, f.Platform.RevalidateExternal(identity with { CreationFileTime = identity.CreationFileTime - 1 }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EquivalentCwdConversionPreservesPhysicalIdentity(bool extended, bool symlink)
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var target = Path.Combine(f.Game, "日本語 resource directory " + new string('a', 45)); Directory.CreateDirectory(target);
        var cwd = target;
        if (symlink) { cwd = Path.Combine(f.Game, "cwd-alias"); Directory.CreateSymbolicLink(cwd, target); }
        if (extended) cwd = @"\\?\" + cwd;
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { WorkingDirectory = cwd }] };
        await f.Approve(); var launched = await f.Service.LaunchAsync("g"); Assert.True(launched.ProcessCreated);
        var actual = await f.Report("run");
        Assert.Equal(launched.Snapshot!.WorkingDirectory.Identity, f.Identity.Inspect(actual.GetProperty("cwd").GetString()!).Observation!.Identity);
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "M3-cwd-equivalence", extended, symlink, identity = launched.Snapshot.WorkingDirectory.Identity, actualCwd = actual.GetProperty("cwd").GetString() }));
    }

    [Fact]
    public async Task RepeatedRealHandoffReleasesProcessJobSnapshotAndObserverHandles()
    {
        var counts = new List<int>();
        for (int round = 0; round < 10; round++)
        {
            await using (var f = new LaunchFixture())
            {
                f.Set("launcher"); await f.Approve(); var started = await f.Service.LaunchAsync("g");
                await Wait(() => f.Service.Status.Tracking == ProcessTrackingState.RunningLimited || f.Service.Status.Current is { } child && child != started.Initial);
                Assert.True(f.Service.Status.Tracking == ProcessTrackingState.RunningConfirmed, JsonSerializer.Serialize(new { round, status = f.Service.Status }));
                await f.Service.DisposeAsync(); // Includes closing both non-killing job leases.
                using var game = Process.GetProcessById(f.Service.Status.Current!.Pid); Assert.False(game.HasExited);
                await f.Stop();
            }
            GC.Collect(); GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess(); counts.Add(self.HandleCount);
        }
        output.WriteLine(JsonSerializer.Serialize(new { scenario = "resource-audit", rounds = counts.Count, handles = counts }));
        Assert.True(counts[^1] <= counts[2] + 6, string.Join(",", counts));
    }

    private sealed class ExternalPlatform(ProcessPresence presence) : IGameProcessPlatform
    {
        private readonly ControlledPlatform inner = new(); private int finds;
        public int Starts => inner.Starts;
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation exe, int maximum) => ++finds == 1
            ? [new(20, 10, exe.FinalPath, exe.Identity)] : [];
        public ProcessPresence RevalidateExternal(ProcessIdentity identity) => presence;
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation) => inner.Start(snapshot, cancellation);
        public IObservedProcess? Attach(ProcessIdentity identity) => inner.Attach(identity);
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int max, int candidates) => inner.Descendants(ancestors, max, candidates);
    }
    [Fact]
    public async Task EmptySnapshotWithoutLifetimeCompletenessNeverConfirmsGameExit()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform();
        await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store, Fast());
        var events = new ConcurrentQueue<ProcessEvent>(); service.ProcessChanged += events.Enqueue;
        await service.LaunchAsync("g");
        await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningConfirmed);
        await Task.Delay(120); platform.RootExited = true;
        await Wait(() => service.Status.Tracking is ProcessTrackingState.Exited or ProcessTrackingState.RunningLimited);
        Assert.Equal(ProcessTrackingState.RunningLimited, service.Status.Tracking);
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessExited);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
        Assert.Equal(1, platform.Starts);
    }

    [Fact]
    public void PublicExitFactRetainsOccurrenceSeparateFromObservation()
    {
        Assert.NotNull(typeof(ProcessEvent).GetProperty("ExitOccurredAtUtc"));
        Assert.NotNull(typeof(ProcessEvent).GetProperty("ObservedAtUtc"));
        Assert.NotNull(typeof(LaunchStatus).GetProperty("ExitOccurredAtUtc"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(". ")]
    public async Task TrailingDotCwdCannotSilentlyBecomeDifferentDirectory(string suffix)
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var ordinary = Path.Combine(f.Game, "resources"); Directory.CreateDirectory(ordinary);
        var extended = @"\\?\" + ordinary + suffix; Directory.CreateDirectory(extended);
        Assert.NotEqual(f.Identity.Inspect(ordinary).Observation!.Identity, f.Identity.Inspect(extended).Observation!.Identity);
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { WorkingDirectory = extended }] };
        await f.Approve();
        var result = await f.Service.LaunchAsync("g");
        if (result.ProcessCreated) { await f.Report("run"); await f.Stop(); }
        Assert.Equal(LaunchOutcome.WorkingDirectoryInvalid, result.Outcome);
        Assert.False(File.Exists(Path.Combine(f.Reports, "run.json")));
    }

    internal static LaunchTrackingOptions Fast() => new() { PollInterval = TimeSpan.FromMilliseconds(20), RunningPollInterval = TimeSpan.FromMilliseconds(20), InitialTreeWindow = TimeSpan.FromMilliseconds(60), HandoffWindow = TimeSpan.FromMilliseconds(140), CandidateSettle = TimeSpan.FromMilliseconds(40) };
    internal static async Task Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(); await Task.Delay(20); }
    }
}
