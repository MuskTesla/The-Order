using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;
[Collection("Launch processes")]
public sealed class LaunchSetupTests
{
    [Fact] public async Task FolderChoiceListsExeAndCmdIncludingAuxiliaryAndNestedFilesWithoutExecuting()
    {
        using var profile = new TransactionProfile(); var folder = Path.Combine(profile.Root, "游戏"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "game.exe", "setup.exe", "start.cmd", "readme.txt" }) File.WriteAllText(Path.Combine(folder, name), "synthetic non executable");
        var nested = Path.Combine(folder, "bin"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "game64.exe"), "synthetic non executable");
        var result = await new LaunchFileDiscovery(new WindowsFilesystemIdentity(), new WindowsScanFilesystem()).ReadAsync(folder);
        Assert.False(result.Partial); Assert.Equal(4, result.Choices.Length); Assert.Contains(result.Choices, c => c.Label == "start.cmd"); Assert.Contains(result.Choices, c => c.Label == @"bin\game64.exe");
        Assert.Equal("synthetic non executable", File.ReadAllText(Path.Combine(folder, "start.cmd")));
    }
    [Fact] public async Task MissingExecutableStillOffersFirstLaunchAction()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Executable = null }] };
        await using var vm = new LaunchViewModel(f.Library, f.Service); await vm.OpenAsync("g");
        Assert.Equal(LaunchOutcome.ExecutableMissing, vm.Availability); Assert.False(vm.CanLaunch); Assert.True(vm.CanActivate);
    }
    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("GAME.EXE", true)]
    [InlineData("开始游戏.cmd", true)]
    [InlineData("unins000.exe", false)]
    [InlineData("setup.exe", false)]
    [InlineData("configure.cmd", false)]
    [InlineData("updater.exe", false)]
    [InlineData("UnityCrashHandler64.exe", false)]
    public async Task SingleGameCanBeAutomaticButKnownSupportToolRequiresExplicitChoice(string name, bool automatic)
    {
        using var profile = new TransactionProfile(); var folder = Path.Combine(profile.Root, "游戏"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name); File.WriteAllText(file, "synthetic non executable");
        var result = await new LaunchFileDiscovery(new WindowsFilesystemIdentity(), new WindowsScanFilesystem()).ReadAsync(folder);
        Assert.False(result.Partial); Assert.Equal(file, Assert.Single(result.Choices).Path);
        Assert.Equal(automatic, result.AutomaticChoice is not null);
        Assert.Null((result with { Partial = true }).AutomaticChoice);
        Assert.Null((result with { Choices = [.. result.Choices, result.Choices[0] with { Path = Path.Combine(folder, "second.exe") }] }).AutomaticChoice);
        Assert.Equal("synthetic non executable", File.ReadAllText(file));
    }
    [Fact] public async Task ActualCommandScriptPreservesLiteralPercentSpacesAndMetacharacters()
    {
        await using var f = new LaunchFixture();
        var script = Path.Combine(f.Game, "启动 %PATH% & 游戏.cmd");
        File.WriteAllText(script, "@echo off\r\n\"%~dp0simple_game.exe\" %*\r\n");
        string[] args = ["two spaces", "日本語", "中文", "%PATH%", "bang!", "a&b", "caret^pipe|", @"trailing\"];
        f.Set("game", args); f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Executable = script }] };
        await f.Approve(); var started = await f.Service.LaunchAsync("g"); Assert.True(started.ProcessCreated);
        var report = await f.Report("run"); Assert.Equal(args, report.GetProperty("arguments").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(Path.GetFullPath(f.Game), Path.GetFullPath(report.GetProperty("cwd").GetString()!));
        Assert.Equal("cmd.exe", Path.GetFileName(started.Initial!.ImagePath), ignoreCase: true);
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await f.Service.LaunchAsync("g")).Outcome);
        await f.Wait(() => f.Service.Status.Current?.Pid == report.GetProperty("pid").GetInt32() || f.Service.Status.Tracking == ProcessTrackingState.RunningLimited);
        await f.Stop();
        await f.Wait(() => f.Service.Status.Tracking == ProcessTrackingState.Exited);
    }
    [Fact] public async Task CommandScriptDoesNotInterpretQuotedArgumentAsAnotherCommand()
    {
        await using var f = new LaunchFixture(); var script = Path.Combine(f.Game, "start.cmd"); File.WriteAllText(script, "@echo off\r\nexit /b 0\r\n");
        f.Set("game", ["\" & echo unexpected"]); f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Executable = script }] };
        await f.Approve(); var result = await f.Service.LaunchAsync("g"); Assert.False(result.ProcessCreated); Assert.Equal(LaunchOutcome.LaunchFailed, result.Outcome);
    }
}
