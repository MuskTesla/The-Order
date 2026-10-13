using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;
[Collection("Launch processes")]
public sealed class P1LaunchTests
{
    [Fact] public async Task DpiAppliesOnlyToLaunchedChildAndKeepsStructuredArguments()
    {
        var original = Environment.GetEnvironmentVariable("__COMPAT_LAYER"); await using var f = new LaunchFixture(); f.Set("game", ["two words", "\"quote\""]);
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new(HighDpi: true) }] };
        await f.Approve(); var launched = await f.Service.LaunchAsync("g"); Assert.True(launched.ProcessCreated);
        var report = await f.Report("run"); Assert.Equal("HIGHDPIAWARE", report.GetProperty("compatibilityLayer").GetString(), ignoreCase: true); Assert.Equal(original, Environment.GetEnvironmentVariable("__COMPAT_LAYER"));
        Assert.Equal(new[] { "two words", "\"quote\"" }, report.GetProperty("arguments").EnumerateArray().Select(x => x.GetString())); await f.Stop();
    }
    [Fact] public async Task ChangedCompatibilityRequiresFreshPositionReview()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve(); var prior = await f.Service.PrepareAsync("g", null); Assert.Equal(LaunchOutcome.Ready, prior.Outcome);
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new(HighDpi: true) }] };
        var changed = await f.Service.PrepareAsync("g", null); Assert.Equal(LaunchOutcome.StaleInstallation, changed.Outcome); await f.Service.ConfirmPositionAsync(changed.Snapshot!); Assert.Equal(LaunchOutcome.Ready, (await f.Service.PrepareAsync("g", null)).Outcome);
    }
    [Theory][InlineData("steam-admin")][InlineData("steam-locale")][InlineData("steam-dpi")][InlineData("admin-dpi")][InlineData("missing-locale")]
    public async Task UnsupportedOrMissingHelpersDoNotCreateProcesses(string kind)
    {
        await using var f = new LaunchFixture(); f.Set("game"); var options = kind switch { "steam-admin" => new LaunchCompatibility(Administrator: true, UseSteam: true), "steam-locale" => new(UseSteam: true, LocaleEmulator: Path.Combine(f.Game, "LEProc.exe")), "steam-dpi" => new(UseSteam: true, HighDpi: true), "admin-dpi" => new(Administrator: true, HighDpi: true), _ => new(LocaleEmulator: Path.Combine(f.Game, "missing", "LEProc.exe")) };
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { SteamAppId = "480", Compatibility = options }] };
        var preparation = await f.Service.PrepareAsync("g", null); Assert.Null(preparation.Snapshot); Assert.NotEqual(LaunchOutcome.Ready, preparation.Outcome); Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
    }
    [Fact] public async Task HelperIdentityChangeInvalidatesSavedPosition()
    {
        await using var f = new LaunchFixture(); f.Set("game"); var tool = Path.Combine(f.Game, "LEProc.exe"); File.WriteAllText(tool, "never executed synthetic helper");
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new(LocaleEmulator: tool) }] };
        await f.Approve(); File.Move(tool, tool + ".old"); File.WriteAllText(tool, "replacement"); Assert.Equal(LaunchOutcome.StaleInstallation, (await f.Service.PrepareAsync("g", null)).Outcome);
    }
    [Fact] public async Task LocaleBrokerPreservesQuotedArgumentsAndTargetWorkingDirectory()
    {
        await using var f = new LaunchFixture(); f.Set("game", ["two words", "\"quoted value\""]);
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Compatibility = new(LocaleEmulator: Path.Combine(f.Game, "LEProc.exe")) }] };
        await f.Approve(); Assert.True((await f.Service.LaunchAsync("g")).ProcessCreated); var report = await f.Report("run");
        Assert.Equal(new[] { "two words", "\"quoted value\"" }, report.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(Path.GetFullPath(f.Game).TrimEnd('\\'), report.GetProperty("cwd").GetString()!.TrimEnd('\\'), ignoreCase: true); await f.Stop();
    }
    [Fact] public async Task SteamBrokerTracksOnlyTheNewExactGameImage()
    {
        await using var f = new LaunchFixture(syntheticSteam: true); f.Set("game", ["two words"]);
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { SteamAppId = "480", Compatibility = new(UseSteam: true) }] };
        await f.Approve(); Assert.True((await f.Service.LaunchAsync("g")).ProcessCreated); var report = await f.Report("run");
        Assert.Equal(new[] { "two words" }, report.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()));
        using var game = System.Diagnostics.Process.GetProcessById(report.GetProperty("pid").GetInt32()); Assert.Equal("simple_game", game.ProcessName); await f.Stop();
    }
    [Fact] public void RecentActivationIsProfileBoundAndCannotAcceptArbitraryCommands()
    {
        var request = new RecentGameActivation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D"), "Test", @"C:\test path\资料"); var command = string.Join(' ', request.Arguments.Select(Quote));
        Assert.Equal(request, RecentGameActivation.Parse(WindowsRecentGames.Split(command))); Assert.True(request.Matches(request.ProfileId, request.StoreId, "Test", request.Root)); Assert.False(request.Matches(request.ProfileId, Guid.NewGuid(), "Test", request.Root));
        Assert.Null(RecentGameActivation.Parse(["--jump-game", "cmd.exe", "--jump-root", request.Root])); Assert.Null(RecentGameActivation.Parse([.. request.Arguments, "--run", "evil"]));
        var legacy = request with { GameId = "legacy 中文 42" }; Assert.Equal(legacy, RecentGameActivation.Parse(WindowsRecentGames.Split(string.Join(' ', legacy.Arguments.Select(Quote)))));
        var bad = request.Arguments; bad[1] = "line\ncommand"; Assert.Null(RecentGameActivation.Parse(bad));
        static string Quote(string value) => "\"" + value.Replace("\\", "\\").Replace("\"", "\\\"") + "\"";
    }
    [Fact] public void RecentGamesExcludeUninstalledAndLimitFive()
    {
        var entries = Enumerable.Range(0, 10).Select(i => SessionTestRig.Entry(i.ToString()) with { LastPlayedAt = new DateTimeOffset(2026, 10, 1, i, 0, 0, TimeSpan.Zero).ToString("O") }).Append(SessionTestRig.Entry("not-installed") with { LastPlayedAt = "2026-10-12T00:00:00Z", Installations = [], PreferredInstallationId = null });
        Assert.Equal(new[] { "9", "8", "7", "6", "5" }, RecentGameActivation.Recent(entries).Select(x => x.Id));
    }
}
