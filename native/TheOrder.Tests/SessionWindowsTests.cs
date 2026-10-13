using System.Diagnostics;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class SessionWindowsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealWindowsAwakeClockIsMonotonicAndPowerSubscriptionIsDisposable()
    {
        var clock = new WindowsSessionClock(); var start = clock.Read(); await Task.Delay(40); var end = clock.Read();
        Assert.True(end.Awake100ns > start.Awake100ns); Assert.Equal(start.SuspendEpoch, end.SuspendEpoch);
        using var first = new WindowsSessionPowerEvents();
        using var second = new WindowsSessionPowerEvents();
        second.Dispose(); second.Dispose();
        output.WriteLine($"Real awake API delta100ns={end.Awake100ns - start.Awake100ns}; power registrations disposed (no host sleep requested).");
    }
    [Theory]
    [InlineData(300)]
    [InlineData(28800)]
    public void WindowsClockReaderDetectsSleepAndHibernateSemanticsWithoutUtcSubtraction(long sleepSeconds)
    {
        long awake = 100_000_000, bias = 0;
        var clock = new WindowsSessionClock(() => (awake, awake + bias - 1, awake + bias + 1));
        var before = clock.Read(); bias += sleepSeconds * 10_000_000; awake += 10_000_000;
        var after = clock.Read(); Assert.Equal(before.SuspendEpoch + 1, after.SuspendEpoch); Assert.Equal(10_000_000, after.Awake100ns - before.Awake100ns);
        var accumulation = new SessionAccumulator(); accumulation.Observe(before, before, true); accumulation.Observe(after, after, true);
        Assert.Equal(0, accumulation.Accumulated); // Discontinuity also drops the uncertain awake tail.
        awake += 10_000_000; var next = clock.Read(); accumulation.Observe(next, next, true); Assert.Equal(10_000_000, accumulation.Accumulated);
    }
    [Fact]
    public void DpapiTokenRequiresOriginalPhysicalProfileAndRejectsCorruption()
    {
        using var first = new TransactionProfile(); using var other = new TransactionProfile();
        var store = new WindowsSessionRecoveryTokens(first.Root); var wrong = new WindowsSessionRecoveryTokens(other.Root);
        Assert.NotNull(store.BootId);
        var fs = new WindowsFilesystemIdentity(); var folder = fs.Inspect(first.Root).Observation!;
        var exe = fs.Inspect(Environment.ProcessPath!, false).Observation!;
        var token = new SessionRecoveryIdentity(1, Guid.NewGuid().ToString(), "g", "i", store.BootId!.Value,
            store.ProfileIdentity, new(123, 123, exe.FinalPath, exe.Identity), folder, exe, folder);
        var encoded = store.Seal(token); var read = store.Open(encoded);
        Assert.NotNull(read); Assert.Equal(token.Process, read.Process); Assert.Equal(token.ProfileIdentity, read.ProfileIdentity);
        Assert.Null(wrong.Open(encoded)); Assert.Null(store.Open("invalid base64")); Assert.Null(store.Open(new string('a', 32769)));
    }
    [Theory]
    [InlineData("game")]
    [InlineData("launcher")]
    [InlineData("chain")]
    public async Task RealFiveAProcessFactsCreateSingleHistoryAndFinalExitUsesOsOccurrence(string mode)
    {
        // This scenario requires a confirmed live-parent/live-child handoff. Use
        // the existing real-observer handshake instead of assuming that two fixed
        // sleeps also establish those facts during a full-suite CPU load.
        await using var fixture = new LaunchFixture(observedHandoff: mode == "launcher"); fixture.Set(mode);
        var entry = SessionTestRig.Entry() with { Installations = fixture.Library.Entry.Installations };
        File.WriteAllBytes(Path.Combine(fixture.Profile.Root, "library.json"), SessionTestRig.Codec.Encode([entry], SessionTestRig.Date));
        await using var library = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(fixture.Profile.Root));
        var clock = new WindowsSessionClock(); var tokens = new WindowsSessionRecoveryTokens(fixture.Profile.Root);
        var platform = new WindowObservationPlatform((IGameProcessPlatform?)fixture.Observation ?? fixture.Platform);
        using var power = new WindowsSessionPowerEvents();
        await using var engine = new SessionEngine(library, fixture.Identity, platform, fixture.Store, clock, tokens, power,
            launchOptions: new() { HandoffWindow = TimeSpan.FromSeconds(1), CandidateSettle = TimeSpan.FromMilliseconds(200) });
        var prep = await engine.PrepareAsync("g"); await engine.ConfirmPositionAsync(prep.Snapshot!);
        fixture.Observation?.Requested();
        var started = await engine.LaunchAsync("g"); Assert.True(started.ProcessCreated);
        await Wait(() => engine.LaunchStatus.Tracking == ProcessTrackingState.RunningConfirmed && (mode == "game" || engine.Snapshot.Current?.Pid != started.Initial?.Pid));
        await platform.WindowObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await engine.SampleAsync(); await Task.Delay(1150); await engine.SampleAsync(); await engine.CheckpointAsync();
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await engine.LaunchAsync("other-game")).Outcome);
        var before = Assert.Single(library.Snapshot.Entries[0].Sessions);
        Assert.True(before.Seconds >= 1); Assert.Equal(started.Snapshot!.OperationId.ToString(), before.Id); Assert.Equal(6, library.Snapshot.Entries[0].PlayCount);
        await fixture.Stop(); await Wait(() => engine.Snapshot.Session?.Native?.Lifecycle == SessionLifecycle.Completed);
        var record = Assert.Single(library.Snapshot.Entries[0].Sessions);
        Assert.Equal(engine.LaunchStatus.ExitOccurredAtUtc, record.Native!.EndedAtUtc);
        Assert.Equal(1000 + record.Seconds, library.Snapshot.Entries[0].TotalPlaySeconds);
        output.WriteLine($"{mode}: session={record.Id}; count=6; seconds={record.Seconds}; exit={record.Native.EndedAtUtc:O}; no second session.");
    }
    [Fact]
    public async Task RealProcessSurvivesManagerDisposalAndSameGenerationRecoveryOnlyGetsCurrentProcessCapability()
    {
        await using var fixture = new LaunchFixture(); fixture.Set("game");
        var entry = SessionTestRig.Entry() with { Installations = fixture.Library.Entry.Installations };
        File.WriteAllBytes(Path.Combine(fixture.Profile.Root, "library.json"), SessionTestRig.Codec.Encode([entry], SessionTestRig.Date));
        var tokens = new WindowsSessionRecoveryTokens(fixture.Profile.Root); string id; ProcessIdentity identity;
        await using (var library = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(fixture.Profile.Root)))
        await using (var engine = new SessionEngine(library, fixture.Identity, fixture.Platform, fixture.Store, new WindowsSessionClock(), tokens))
        {
            var prep = await engine.PrepareAsync("g"); await engine.ConfirmPositionAsync(prep.Snapshot!);
            var start = await engine.LaunchAsync("g"); identity = start.Current!; id = start.Snapshot!.OperationId.ToString();
            await fixture.Report("run"); Assert.True(await engine.CheckpointForExitAsync());
        }
        using (var process = Process.GetProcessById(identity.Pid)) Assert.False(process.HasExited);
        await using var reopened = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(fixture.Profile.Root));
        await using var recovered = new SessionEngine(reopened, fixture.Identity, fixture.Platform, fixture.Store, new WindowsSessionClock(), tokens);
        await recovered.InitializeAsync(); Assert.Equal(id, recovered.Snapshot.Session!.Id); Assert.Equal(identity, recovered.Snapshot.Current);
        Assert.Equal(SessionTracking.CurrentProcessOnly, recovered.Snapshot.Tracking); Assert.Equal(6, reopened.Snapshot.Entries[0].PlayCount);
        await recovered.InitializeAsync(); Assert.Equal(1, recovered.Snapshot.Session.Native!.Recovery.Count);
        await fixture.Stop(); await recovered.SampleAsync();
        Assert.Equal(SessionLifecycle.Incomplete, recovered.Snapshot.Session.Native.Lifecycle); Assert.Null(recovered.Snapshot.Session.Native.EndedAtUtc);
    }
    [Fact]
    public async Task RepeatedNativeClockAndPowerResourcesHaveBoundedHandleGrowth()
    {
        for (int i = 0; i < 3; i++) { using var warm = new WindowsSessionPowerEvents(); _ = new WindowsSessionClock().Read(); }
        using var process = Process.GetCurrentProcess(); process.Refresh(); int before = process.HandleCount;
        for (int i = 0; i < 40; i++)
        {
            using var events = new WindowsSessionPowerEvents(); var clock = new WindowsSessionClock();
            for (int sample = 0; sample < 20; sample++) clock.Read();
        }
        await Task.Delay(50); process.Refresh(); int after = process.HandleCount;
        output.WriteLine($"40 native power registrations / 800 clock reads: handles {before} -> {after}.");
        Assert.True(after <= before + 12, $"Unexpected native handle growth: {before} -> {after}");
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Session fixture did not reach expected state."); await Task.Delay(25); }
    }
}
