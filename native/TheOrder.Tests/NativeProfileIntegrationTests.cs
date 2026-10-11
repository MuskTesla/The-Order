using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NativeProfileIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealSyntheticProcessSessionRestartRecoveryAndBackupUseProductionLikeStore()
    {
        using var f = new ProfileFixture(); await using var game = new LaunchFixture(); game.Set("game");
        string sessionId; long ticks; ProcessIdentity identity; Guid storeId;
        using (var profile = f.Open())
        {
            storeId = profile.Manifest.StoreId; var configuration = profile.Configuration();
            var preferences = new IsolatedUiPreferencesStore(configuration);
            await preferences.SaveAsync(new(NativeTheme.Dark, "list", "name"), default);
            await new IsolatedCredentialStore(configuration).SaveAsync("synthetic-session-credential", default);
            await using var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence());
            var entry = SessionTestRig.Entry(total: 0, count: 0) with { Installations = [game.Library.Entry.Installations[0] with { WorkingDirectory = game.Game }] };
            await library.ImportAsync(NativeProfileTests.Codec.Encode([entry], SessionTestRig.Date));
            var platform = new WindowsGameProcesses(game.Identity);
            await using var engine = new SessionEngine(library, game.Identity, platform, new IsolatedLaunchPositionStore(configuration), new WindowsSessionClock(), new WindowsSessionRecoveryTokens(configuration), autoPoll: false);
            await engine.InitializeAsync(); var preview = await engine.PrepareAsync("g"); await engine.ConfirmPositionAsync(preview.Snapshot!);
            var start = await engine.LaunchAsync("g"); Assert.True(start.ProcessCreated); identity = start.Current!; sessionId = start.Snapshot!.OperationId.ToString();
            await game.Report("run"); await NativeProfileTests.Until(() => engine.LaunchStatus.Tracking == ProcessTrackingState.RunningConfirmed);
            await engine.SampleAsync(); await Task.Delay(1300); await engine.SampleAsync(); await engine.CheckpointAsync();
            Assert.Equal(1, library.Snapshot.Entries[0].PlayCount); Assert.True(library.Snapshot.Entries[0].TotalPlaySeconds >= 1);
            Assert.True(await engine.CheckpointForExitAsync()); ticks = library.Snapshot.Entries[0].Sessions[0].Native!.Accumulated100ns;
        }
        using (var process = Process.GetProcessById(identity.Pid)) Assert.False(process.HasExited);
        byte[] final;
        using (var profile = f.Open())
        {
            Assert.Equal(storeId, profile.Manifest.StoreId); var configuration = profile.Configuration();
            await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
            {
                var platform = new WindowsGameProcesses(game.Identity);
                await using var engine = new SessionEngine(library, game.Identity, platform, new IsolatedLaunchPositionStore(configuration), new WindowsSessionClock(), new WindowsSessionRecoveryTokens(configuration), autoPoll: false);
                await engine.InitializeAsync(); var recovered = Assert.Single(library.Snapshot.Entries[0].Sessions);
                Assert.Equal(sessionId, recovered.Id); Assert.Equal(ticks, recovered.Native!.Accumulated100ns); Assert.Equal(1, library.Snapshot.Entries[0].PlayCount);
                Assert.Equal(LaunchOutcome.AlreadyRunning, (await engine.LaunchAsync("g")).Outcome);
                await game.Stop();
                for (var i = 0; i < 100 && engine.Snapshot.Session?.Native?.IsTerminal != true; i++) { await engine.SampleAsync(); await Task.Delay(30); }
                await engine.CheckpointAsync(); Assert.True(library.Snapshot.Entries[0].Sessions[0].Native!.IsTerminal);
                Assert.Equal(1, library.Snapshot.Entries[0].PlayCount);
            }
            profile.CheckpointBackups(); final = profile.ReadComponent("library.json")!;
        }
        File.WriteAllText(Path.Combine(f.Root, "library.json"), "{incomplete");
        using (var profile = f.Open()) profile.Restore("library.json");
        using var reopened = f.Open(); Assert.Equal(final, reopened.ReadComponent("library.json")); Assert.Equal(storeId, reopened.Manifest.StoreId);
        var restored = Assert.Single(NativeProfileTests.Codec.Decode(final)); Assert.Equal(game.Game, restored.Installations[0].WorkingDirectory);
        Assert.Equal(sessionId, Assert.Single(restored.Sessions).Id); Assert.Equal(1, restored.PlayCount);
        Assert.Equal("list", (await new IsolatedUiPreferencesStore(reopened.Configuration()).LoadAsync(default)).Layout);
        Assert.Equal("synthetic-session-credential", await new IsolatedCredentialStore(reopened.Configuration()).ReadAsync(default));
        output.WriteLine($"synthetic PID={identity.Pid}; store={storeId}; session={sessionId}; count={restored.PlayCount}; seconds={restored.TotalPlaySeconds}; restart+recovery+restore=true");
    }
    [Theory][InlineData(1000)][InlineData(5000)][InlineData(10000)]
    public async Task NativeProfilePerformanceSanity(int count)
    {
        using var f = new ProfileFixture();
        var entries = Enumerable.Range(0, count).Select(i => SessionTestRig.Entry("g" + i, 0, 0) with { Installations = [], PreferredInstallationId = null }).ToImmutableArray();
        using (var profile = f.Open())
        await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
            await library.ImportAsync(NativeProfileTests.Codec.Encode(entries, SessionTestRig.Date));
        var watch = Stopwatch.StartNew(); double opened, written, backup;
        using (var profile = f.Open())
        {
            await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
            {
                opened = watch.Elapsed.TotalMilliseconds; watch.Restart(); await library.UpdateAsync("g0", new() { Notes = "performance write" }); written = watch.Elapsed.TotalMilliseconds;
            }
            watch.Restart(); profile.CheckpointBackups(); backup = watch.Elapsed.TotalMilliseconds;
        }
        File.WriteAllText(Path.Combine(f.Root, "library.json"), "{broken"); watch.Restart();
        using (var profile = f.Open()) { profile.Restore("library.json"); }
        using (var profile = f.Open()) Assert.Equal(count, NativeProfileTests.Codec.Decode(profile.ReadComponent("library.json")!).Length);
        var restore = watch.Elapsed.TotalMilliseconds;
        using var process = Process.GetCurrentProcess(); process.Refresh();
        output.WriteLine(JsonSerializer.Serialize(new { count, openMs = opened, writeMs = written, backupMs = backup, restoreMs = restore, peakMiB = process.PeakWorkingSet64 / 1048576.0, bytes = Directory.GetFiles(f.Root).Sum(p => new FileInfo(p).Length) }));
        // Broad local sanity only, not a product latency SLA or a media-pressure benchmark.
        Assert.InRange(opened + written + backup + restore, 0, 60000);
    }
    [Fact]
    public async Task RecoveryFaultRetainsBackupAndStaleWriterCannotOverwriteExternalChange()
    {
        using var f = new ProfileFixture();
        using (var profile = f.Open())
        await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
            await library.ImportAsync(NativeProfileTests.Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
        File.WriteAllText(Path.Combine(f.Root, "library.json"), "{corrupt");
        var backup = File.ReadAllBytes(Path.Combine(f.Root, "library.json.bak"));
        using (var profile = NativeProfileStore.Open(f.Request, NativeProfileTests.Codec, persistenceProbe: new ReplaceFailure()))
            Assert.Throws<IOException>(() => profile.Restore("library.json"));
        Assert.Equal(backup, File.ReadAllBytes(Path.Combine(f.Root, "library.json.bak")));
        Assert.Equal("{corrupt", File.ReadAllText(Path.Combine(f.Root, "library.json")));
        using (var profile = f.Open()) profile.Restore("library.json");
        using (var profile = f.Open())
        await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
        {
            await library.ImportAsync(NativeProfileTests.Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
            File.WriteAllText(Path.Combine(f.Root, "library.json"), "external change");
            await Assert.ThrowsAsync<NativeProfileException>(() => library.UpdateAsync("g", new() { Notes = "stale" }));
            Assert.Equal("external change", File.ReadAllText(Path.Combine(f.Root, "library.json")));
        }
    }
    private sealed class ReplaceFailure : IPersistenceProbe
    { public Task ReachAsync(PersistenceTarget target, PersistenceStage stage) { if (target == PersistenceTarget.Main && stage == PersistenceStage.BeforeReplace) throw new IOException("synthetic restore fault"); return Task.CompletedTask; } }
}
