using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class LaunchSafetyTests
{
    [Fact]
    public async Task ConcurrentLaunchOtherGameAndImmutableSnapshotUseOneCreation()
    {
        await using var f = new LaunchFixture(); f.Set("game", ["original"]); await f.Approve();
        var platform = new ControlledPlatform { Hold = true };
        await using var service = Service(f, platform);
        var first = service.LaunchAsync("g"); await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LaunchOutcome.AlreadyLaunching, (await service.LaunchAsync("g")).Outcome);
        Assert.Equal(LaunchOutcome.AlreadyLaunching, (await service.LaunchAsync("another-game")).Outcome);
        f.Set("game", ["edited-after-click"]); platform.Release.Set();
        var result = await first;
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal("original", platform.Snapshot!.Installation.Arguments.Last());
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("another-game")).Outcome);
        Assert.Equal(1, platform.Starts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialQueryFailureIsStartedLimitedAndCannotCreateAgain(bool throws)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform { Unknown = true, Throws = throws };
        await using var service = Service(f, platform);
        var result = await service.LaunchAsync("g");
        Assert.True(result.ProcessCreated); Assert.Equal(LaunchOutcome.StartedButTrackingLimited, result.Outcome);
        await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningLimited);
        Assert.NotEqual(ProcessTrackingState.Exited, service.Status.Tracking);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
        Assert.Equal(1, platform.Starts);
    }
    [Theory]
    [InlineData("helper.exe", true, true)]
    [InlineData("updater.exe", true, true)]
    [InlineData("game.exe", false, true)]
    [InlineData("game.exe", true, false)]
    [InlineData("outside", true, true)]
    public async Task UnreliableOrAuxiliaryContinuationNeverBecomesTrackedGame(string name, bool window, bool attach)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform { RootExited = true, ChildName = name, ChildWindow = window, AttachAllowed = attach };
        await using var service = Service(f, platform);
        var events = new List<ProcessEvent>(); service.ProcessChanged += e => { lock (events) events.Add(e); };
        await service.LaunchAsync("g"); await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningLimited);
        Assert.DoesNotContain(events, e => e.Kind == ProcessEventKind.ProcessHandoff);
        Assert.Equal(LaunchOutcome.StartedButTrackingLimited, service.Status.Outcome);
        Assert.True(platform.Queries <= 12);
    }
    [Fact]
    public async Task LaterQueryFailureDoesNotBecomeExitOrReleaseSingleActiveAdmission()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform(); await using var service = Service(f, platform);
        await service.LaunchAsync("g"); await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningConfirmed);
        platform.Unknown = true;
        await Wait(() => service.Status.Tracking == ProcessTrackingState.RunningLimited);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await service.LaunchAsync("g")).Outcome);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrShutdownBeforeCreationDoesNotCreate(bool shutdown)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform { Hold = true }; await using var service = Service(f, platform);
        using var cancel = new CancellationTokenSource(); var task = service.LaunchAsync("g", cancellation: cancel.Token);
        await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? dispose = shutdown ? service.DisposeAsync().AsTask() : null;
        if (!shutdown) cancel.Cancel(); platform.Release.Set();
        Assert.Equal(LaunchOutcome.Cancelled, (await task).Outcome);
        if (dispose is not null) await dispose;
        Assert.Equal(0, platform.Starts);
    }
    [Fact]
    public async Task OsDenialHasNoTrackingAndPreservesSpecificUacMessage()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform { Deny = true }; await using var service = Service(f, platform);
        var result = await service.LaunchAsync("g");
        Assert.Equal(LaunchOutcome.AccessDenied, result.Outcome); Assert.False(result.ProcessCreated);
        Assert.Contains("UAC", result.Message); Assert.Equal(ProcessTrackingState.None, result.Tracking);
    }
    [Fact]
    public async Task FinalPreStartRevalidationRejectsChangedFileAfterPreparation()
    {
        await using var f = new LaunchFixture(); f.Set("game"); var p = await f.Service.PrepareAsync("g", null);
        File.Move(f.Exe, f.Exe + ".old"); File.Copy(f.Exe + ".old", f.Exe);
        var error = Assert.Throws<LaunchValidationException>(() => f.Platform.Start(p.Snapshot!, default));
        Assert.Equal(LaunchOutcome.StaleInstallation, error.Outcome);
        Assert.False(File.Exists(Path.Combine(f.Reports, "run.json")));
    }
    [Fact]
    public async Task UiNavigationDoesNotAcceptOldLaunchResultOrStopCreatedProcess()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform { Hold = true }; await using var service = Service(f, platform);
        await using var vm = new LaunchViewModel(f.Library, service);
        await vm.OpenAsync("g"); var task = vm.LaunchAsync(); await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Leave(); platform.Release.Set(); await task; vm.Refresh();
        Assert.Null(vm.GameId); Assert.Equal("选择本地安装。", vm.Message);
        Assert.Equal(1, platform.Starts); Assert.True(service.Status.ProcessCreated);
    }
    [Fact]
    public async Task UiAlreadyRunningKeepsCancellationOfOriginalObservation()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform(); await using var service = Service(f, platform);
        await using var vm = new LaunchViewModel(f.Library, service); await vm.OpenAsync("g");
        await vm.LaunchAsync(); await vm.LaunchAsync();
        Assert.Equal(LaunchOutcome.AlreadyRunning, vm.LastRequestOutcome); Assert.Equal(1, platform.Starts);
        vm.Cancel(); await Wait(() => service.Status.Tracking == ProcessTrackingState.Detached);
        Assert.Equal(LaunchOutcome.StartedButTrackingLimited, service.Status.Outcome);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateViewCallbacksAfterShutdownDoNotTouchDisposedCancellationSources(bool started)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var platform = new ControlledPlatform(); await using var service = Service(f, platform);
        await using var vm = new LaunchViewModel(f.Library, service);
        await vm.OpenAsync("g");
        if (started) await vm.LaunchAsync();
        await vm.DisposeAsync();
        // WinUI unload follows host disposal when the drained window finally closes.
        vm.Leave(); vm.Cancel(); vm.Refresh();
        Assert.Equal(started ? 1 : 0, platform.Starts);
        if (started) Assert.Equal(ProcessTrackingState.Detached, service.Status.Tracking);
    }
    [Fact]
    public async Task LaunchAFinishingAfterSwitchToBDoesNotReplaceBState()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        f.Library.OtherEntries["b"] = f.Library.Entry with { Identity = new(Guid.NewGuid(), Guid.NewGuid(), "b") };
        var platform = new ControlledPlatform { Hold = true }; await using var service = Service(f, platform);
        await using var vm = new LaunchViewModel(f.Library, service); await vm.OpenAsync("g");
        var request = vm.LaunchAsync(); await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.OpenAsync("b"); var bMessage = vm.Message;
        platform.Release.Set(); await request; vm.Refresh();
        Assert.Equal("b", vm.GameId); Assert.Equal(bMessage, vm.Message);
        Assert.Equal("g", service.Status.Snapshot!.Game.GameId); Assert.True(service.Status.ProcessCreated);
    }
    [Fact]
    public async Task ProductionLibraryAdmissionRetainsSnapshotThroughEditButRejectsDeleteReimportAndInstallAba()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var entry = Entry(f.Library.Entry.Installations);
        var codec = TransactionDifferentialTests.Codec;
        var bytes = codec.Encode([entry], TransactionDifferentialTests.Clock);
        File.WriteAllBytes(f.Profile.Main, bytes);
        using var library = f.Profile.Open();
        var snapshot = await library.CaptureLaunchAsync("g");
        await library.UpdateAsync("g", new PersonalPatch { Notes = "new personal edit" });
        Assert.Equal(snapshot.Identity, (await library.CaptureLaunchAsync("g")).Identity);
        var before = File.ReadAllBytes(f.Profile.Main);
        Assert.True(await library.WithLaunchAdmissionAsync(snapshot.Identity, "i", () => true));
        Assert.Equal(before, File.ReadAllBytes(f.Profile.Main));
        Assert.Equal(720, library.Snapshot.Entries[0].TotalPlaySeconds); Assert.Equal(3, library.Snapshot.Entries[0].PlayCount);
        await library.RemoveInstallationAsync("g", "i");
        var request = await library.BeginRequestAsync("g");
        await library.AddInstallationAsync("g", entry.Installations[0], request);
        await Assert.ThrowsAsync<LaunchValidationException>(() => library.WithLaunchAdmissionAsync(snapshot.Identity, "i", () => true));
        var afterInstall = await library.CaptureLaunchAsync("g");
        await library.DeleteAsync("g"); await library.ImportAsync(bytes);
        await Assert.ThrowsAsync<LaunchValidationException>(() => library.WithLaunchAdmissionAsync(afterInstall.Identity, "i", () => true));
    }
    internal static LibraryEntry Entry(ImmutableArray<Installation> installations) => new("g", "Synthetic", true, null, installations, "i", "planned", 8, true,
        ["keep"], ["tag"], "notes", 720, 3, [], TransactionDifferentialTests.Clock, null, null, null, new(false, null));
    private static GameLaunchService Service(LaunchFixture f, ControlledPlatform platform) => new(f.Library, f.Identity, platform, f.Store,
        new() { PollInterval = TimeSpan.FromMilliseconds(25), RunningPollInterval = TimeSpan.FromMilliseconds(25), InitialTreeWindow = TimeSpan.FromMilliseconds(100), HandoffWindow = TimeSpan.FromMilliseconds(180), CandidateSettle = TimeSpan.FromMilliseconds(60), MaximumTreePolls = 12 });
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

internal sealed class ControlledPlatform : IGameProcessPlatform
{
    public bool Hold, Unknown, Throws, RootExited, ChildWindow, AttachAllowed = true, Deny, Multiple;
    public string? ChildName;
    public int Starts, Queries;
    public LaunchSnapshot? Snapshot;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim Release = new(false);
    public ImmutableArray<ProcessIdentity> FindExisting(PathObservation exe, int maximum)
    { Entered.TrySetResult(); if (Hold && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return []; }
    public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Deny) throw new LaunchValidationException(LaunchOutcome.AccessDenied, "此程序需要 UAC 权限；未执行。");
        Snapshot = snapshot; Starts++;
        var id = new ProcessIdentity(900001, 100, snapshot.Executable.FinalPath, snapshot.Executable.Identity);
        return new Observed(900001, () => Throws ? throw new IOException("query denied") : new(Unknown ? null : id,
            Unknown ? ProcessTrackingState.Unknown : RootExited ? ProcessTrackingState.Exited : ProcessTrackingState.RunningConfirmed, RootExited ? 200 : null, !RootExited));
    }
    public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maxProcesses, int maxCandidates)
    {
        Queries++;
        if (ChildName is null) return new([], true);
        var image = ChildName == "outside" ? @"C:\not-the-installation\game.exe" : Path.Combine(Snapshot!.Folder.FinalPath, ChildName);
        var one = new ProcessTreeCandidate(900001, new(new(900002, 150, image, new("volume", "child")), ProcessTrackingState.RunningConfirmed, HasWindow: ChildWindow));
        return new(Multiple ? [one, new(900001, one.Observation with { Identity = one.Observation.Identity! with { Pid = 900003 } })] : [one], true);
    }
    public IObservedProcess? Attach(ProcessIdentity identity) => AttachAllowed ? new Observed(identity.Pid, () => new(identity, ProcessTrackingState.RunningConfirmed, HasWindow: ChildWindow)) : null;
    private sealed class Observed(int pid, Func<ProcessObservation> query) : IObservedProcess
    { public int Pid => pid; public ProcessObservation Query() => query(); public void Dispose() { } }
}
