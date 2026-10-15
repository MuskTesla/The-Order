using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class GlobalLaunchProcessTests
{
    [Theory]
    [InlineData(false, LaunchOverride.Inherit, false)]
    [InlineData(true, LaunchOverride.Inherit, true)]
    [InlineData(true, LaunchOverride.Disabled, false)]
    [InlineData(false, LaunchOverride.Enabled, true)]
    [InlineData(true, LaunchOverride.Enabled, true)]
    [InlineData(false, LaunchOverride.Disabled, false)]
    public async Task RealHarmlessLaunchResolvesBothToolsWithoutDuplicateCreation(bool global, LaunchOverride mode, bool expected)
    {
        await using var f = new LaunchFixture(); f.Set("game", ["two words", "\"quoted\""]);
        var magpie = Path.Combine(f.Game, "Magpie.exe"); File.Copy(f.Exe, magpie);
        var policy = new LaunchPolicy(); policy.Apply(new(global, Path.Combine(f.Game, "LEProc.exe"), global, magpie));
        var source = f.Library.Entry.Installations[0] with { Compatibility = new() { Auxiliaries = new(mode, mode) } };
        f.Library.Entry = f.Library.Entry with { Installations = [source] };
        var observed = new CountingPlatform(f.Platform);
        await using var launch = new GameLaunchService(f.Library, f.Identity, observed, f.Store,
            new() { InitialTreeWindow = TimeSpan.FromSeconds(2), HandoffWindow = TimeSpan.FromSeconds(2) }, policy);
        var prepared = await launch.PrepareAsync("g", null); Assert.NotNull(prepared.Snapshot);
        Assert.Equal(expected, prepared.Snapshot.LocaleTool is not null); Assert.Equal(expected, prepared.Snapshot.ScalingTool is not null);
        await launch.ConfirmPositionAsync(prepared.Snapshot);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => launch.LaunchAsync("g")));
        Assert.Single(results, x => x.ProcessCreated); Assert.Equal(1, observed.Creates);
        var report = await f.Report("run");
        Assert.Equal(new[] { "two words", "\"quoted\"" }, report.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()));
        if (expected) await f.Wait(() => Directory.GetFiles(f.Reports, "magpie-*.json").Length == 1);
        else Assert.Empty(Directory.GetFiles(f.Reports, "magpie-*.json"));
        var again = await launch.LaunchAsync("g"); Assert.False(again.ProcessCreated); Assert.Equal(1, observed.Creates);
        Assert.Equal(source, f.Library.Entry.Installations[0]); // Never write effective defaults into the library.
        await f.Stop();
    }
    [Fact]
    public async Task ChangedGlobalsRequireReviewOfANewToolAndRejectAnOldConfirmation()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new() { Auxiliaries = new() } }] };
        var policy = new LaunchPolicy();
        await using var launch = new GameLaunchService(f.Library, f.Identity, f.Platform, f.Store, policy: policy);
        var first = await launch.PrepareAsync("g", null); await launch.ConfirmPositionAsync(first.Snapshot!);
        policy.Apply(new(LocaleEmulatorEnabled: true, LocaleEmulatorPath: Path.Combine(f.Game, "LEProc.exe")));
        await Assert.ThrowsAsync<LaunchValidationException>(() => launch.ConfirmPositionAsync(first.Snapshot!));
        var changed = await launch.PrepareAsync("g", null); Assert.Equal(LaunchOutcome.StaleInstallation, changed.Outcome);
        Assert.NotNull(changed.Snapshot?.LocaleTool); Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
        await launch.ConfirmPositionAsync(changed.Snapshot!);
        Assert.Equal(LaunchOutcome.Ready, (await launch.PrepareAsync("g", null)).Outcome);
    }
    [Theory][InlineData("LEProc.exe")][InlineData("Magpie.exe")]
    public async Task MissingEnabledToolStopsBeforeAnyGameOrHelperCreation(string tool)
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var missing = Path.Combine(f.Game, "not-installed", tool);
        var policy = new LaunchPolicy(); policy.Apply(tool == "LEProc.exe" ? new(true, missing) : new(MagpieEnabled: true, MagpiePath: missing));
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new() { Auxiliaries = new() } }] };
        var observed = new CountingPlatform(f.Platform);
        await using var launch = new GameLaunchService(f.Library, f.Identity, observed, f.Store, policy: policy);
        var result = await launch.LaunchAsync("g"); Assert.False(result.ProcessCreated); Assert.Equal(0, observed.Creates);
        Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
    }
    [Fact]
    public async Task BrokenMagpieReportsGameCreatedWithAuxiliaryFailureInsteadOfFalseSuccess()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var magpie = Path.Combine(f.Game, "Magpie.exe"); File.WriteAllText(magpie, "owned non executable helper fixture");
        var policy = new LaunchPolicy(); policy.Apply(new(MagpieEnabled: true, MagpiePath: magpie));
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new() { Auxiliaries = new() } }] };
        await using var launch = new GameLaunchService(f.Library, f.Identity, f.Platform, f.Store, policy: policy);
        var prepared = await launch.PrepareAsync("g", null); await launch.ConfirmPositionAsync(prepared.Snapshot!);
        var result = await launch.LaunchAsync("g"); Assert.True(result.ProcessCreated); Assert.Contains("Magpie 未能打开", result.Message);
        await f.Report("run"); await f.Stop();
    }
    private sealed class CountingPlatform(IGameProcessPlatform inner) : IGameProcessPlatform
    {
        public int Creates;
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken c) { Creates++; return inner.Start(snapshot, c); }
        public IObservedProcess? Attach(ProcessIdentity id) => inner.Attach(id);
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation e, int max) => inner.FindExisting(e, max);
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> a, int m, int c) => inner.Descendants(a, m, c);
        public ProcessPresence RevalidateExternal(ProcessIdentity id) => inner.RevalidateExternal(id);
    }
}
