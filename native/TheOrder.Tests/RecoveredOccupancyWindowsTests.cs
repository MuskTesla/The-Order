using System.Diagnostics;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class RecoveredOccupancyWindowsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealRecoveredGameKeepsOccupancyAfterStopUntilActualExitWithoutHandleGrowth()
    {
        await using var a = new LaunchFixture(); a.Set("game");
        await using var b = new LaunchFixture(); b.Set("game");
        var entries = new[] { SessionTestRig.Entry() with { Installations = a.Library.Entry.Installations },
            SessionTestRig.Entry("b") with { Installations = b.Library.Entry.Installations } };
        File.WriteAllBytes(Path.Combine(a.Profile.Root, "library.json"), SessionTestRig.Codec.Encode([.. entries], SessionTestRig.Date));
        var tokens = new WindowsSessionRecoveryTokens(a.Profile.Root); int pid;
        await using (var library = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(a.Profile.Root)))
        await using (var engine = new SessionEngine(library, a.Identity, a.Platform, a.Store, new WindowsSessionClock(), tokens))
        {
            foreach (var id in new[] { "g", "b" }) { var prep = await engine.PrepareAsync(id); await engine.ConfirmPositionAsync(prep.Snapshot!); }
            var launched = await engine.LaunchAsync("g"); Assert.True(launched.ProcessCreated); pid = launched.Current!.Pid;
            await a.Report("run"); Assert.True(await engine.CheckpointForExitAsync());
        }
        using var game = Process.GetProcessById(pid); Assert.False(game.HasExited);
        await using var reopened = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(a.Profile.Root));
        await using var recovered = new SessionEngine(reopened, a.Identity, a.Platform, a.Store, new WindowsSessionClock(), tokens);
        await recovered.InitializeAsync(); Assert.Equal(SessionTracking.CurrentProcessOnly, recovered.Snapshot.Tracking);
        await recovered.StopObservingAsync(); Assert.False(game.HasExited);
        var terminal = reopened.Snapshot.Entries[0].Sessions.Single();
        Assert.Equal(SessionLifecycle.Incomplete, terminal.Native!.Lifecycle); Assert.Null(terminal.Native.RecoveryToken); Assert.Null(terminal.Native.EndedAtUtc);
        var saved = File.ReadAllBytes(Path.Combine(a.Profile.Root, "library.json"));
        for (int i = 0; i < 3; i++) Assert.Equal(LaunchOutcome.AlreadyRunning, (await recovered.LaunchAsync("b")).Outcome);
        using var host = Process.GetCurrentProcess(); host.Refresh(); int before = host.HandleCount;
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(LaunchOutcome.AlreadyRunning, (await recovered.LaunchAsync("b")).Outcome);
            Assert.False(game.HasExited);
        }
        host.Refresh(); int after = host.HandleCount;
        Assert.True(after <= before + 12, $"Recovered occupancy handles grew: {before} -> {after}");
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(a.Profile.Root, "library.json")));
        Assert.False(File.Exists(Path.Combine(b.Reports, "run.json")));
        Assert.Empty(reopened.Snapshot.Entries.Single(e => e.Id == "b").Sessions);
        await a.Stop(); Assert.True(game.HasExited);
        var second = await recovered.LaunchAsync("b"); Assert.True(second.ProcessCreated); await b.Report("run");
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await recovered.LaunchAsync("b")).Outcome);
        var created = reopened.Snapshot.Entries.Single(e => e.Id == "b");
        Assert.Single(created.Sessions); Assert.Equal(6, created.PlayCount);
        Assert.Equal(terminal, reopened.Snapshot.Entries.Single(e => e.Id == "g").Sessions.Single());
        await b.Stop();
        output.WriteLine($"Recovered A PID {pid}: stop/no-kill, 23 blocked B requests, handles {before}->{after}; after actual A exit B PID {second.Current!.Pid} created once.");
    }
}
