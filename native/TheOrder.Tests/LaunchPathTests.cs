using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using Xunit;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class LaunchPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryExecutableKeepsWin32ImagePathForPerApplicationPreferences(bool extendedInput)
    {
        await using var f = new LaunchFixture(); f.Set("game");
        if (extendedInput)
            f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Executable = @"\\?\" + f.Exe }] };
        await f.Approve();
        var result = await f.Service.LaunchAsync("g");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var report = await f.Report("run");
        using var process = Process.GetProcessById(report.GetProperty("pid").GetInt32());
        var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
        Assert.True(QueryFullProcessImageName(process.Handle, 0, path, ref size));
        Assert.StartsWith("\"" + f.Exe + "\"", report.GetProperty("commandLine").GetString());
        // Observe the actual Windows-created image name, not a test copy of path conversion.
        Assert.Equal(f.Exe, path.ToString(), ignoreCase: true);
        Assert.Equal(f.Identity.Inspect(f.Exe, false).Observation!.Identity, result.Initial!.ImageIdentity);
        await f.Stop();
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref uint size);

    [Theory]
    [InlineData("ordinary")][InlineData("junction")][InlineData("symlink")]
    public async Task SavedAndFinalLaunchObservationsRejectDirectoryRetargeting(string kind)
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var original = (await f.Service.PrepareAsync("g", null)).Snapshot!;
        var moved = Path.Combine(f.Profile.Root, "relocated"); Assert.StartsWith(f.Profile.Root + "\\", moved);
        Directory.Move(f.Game, moved);
        try
        {
            if (kind == "ordinary") { Directory.CreateDirectory(f.Game); File.Copy(Path.Combine(moved, "simple_game.exe"), f.Exe); }
            else if (kind == "symlink") Directory.CreateSymbolicLink(f.Game, moved);
            else
            {
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{f.Game.Replace("'", "''")}' -Target '{moved.Replace("'", "''")}' | Out-Null");
                using var process = Process.Start(start)!; var error = await process.StandardError.ReadToEndAsync(); Assert.True(process.WaitForExit(15000)); Assert.True(process.ExitCode == 0, error);
            }
            Assert.Equal(LaunchOutcome.StaleInstallation, (await f.Service.LaunchAsync("g")).Outcome);
            Assert.Equal(LaunchOutcome.StaleInstallation, Assert.Throws<LaunchValidationException>(() => f.Platform.Start(original, default)).Outcome);
            Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
        }
        finally { if (kind != "ordinary" && Directory.Exists(f.Game)) Directory.Delete(f.Game); }
    }
    [Fact]
    public async Task InaccessibleWorkingDirectoryIsExplicitDeniedAndNeverFallsBack()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var dir = Directory.CreateDirectory(Path.Combine(f.Game, "denied-cwd")); var original = dir.GetAccessControl();
        var rules = dir.GetAccessControl(); rules.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes | FileSystemRights.ListDirectory, AccessControlType.Deny));
        f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { WorkingDirectory = dir.FullName }] };
        try { dir.SetAccessControl(rules); Assert.Equal(LaunchOutcome.AccessDenied, (await f.Service.LaunchAsync("g")).Outcome); }
        finally { var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); dir.SetAccessControl(restore); }
        Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
    }
    [Fact]
    public async Task PositionStoreCapacityRefusalPreservesPriorTrustedBytes()
    {
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var path = Path.Combine(f.Profile.Root, "native-launch-positions.dpapi"); var before = File.ReadAllBytes(path);
        var position = (await f.Store.ReadAsync(default))[0];
        var tooMany = Enumerable.Range(0, 10001).Select(i => position with { GameId = i.ToString() }).ToImmutableArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.WriteAsync(tooMany, default));
        Assert.Equal(before, File.ReadAllBytes(path));
        var tooLarge = ImmutableArray.Create(position with { GameId = new string('x', 16 * 1024 * 1024) });
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.WriteAsync(tooLarge, default));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task SubstRetargetRequiresNewExplicitReviewAndFinalStartRejectsOldSnapshot()
    {
        await using var f = new LaunchFixture(); f.Set("game");
        var used = DriveInfo.GetDrives().Select(d => d.Name[..1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drive = Enumerable.Range('R', 9).Select(c => ((char)c).ToString()).FirstOrDefault(c => !used.Contains(c));
        Assert.NotNull(drive);
        int Run(params string[] args)
        {
            var start = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; Assert.True(process.WaitForExit(10000)); return process.ExitCode;
        }
        Assert.Equal(0, Run(drive + ":", f.Profile.Root));
        try
        {
            var alias = drive + ":\\" + Path.GetFileName(f.Game);
            f.Library.Entry = f.Library.Entry with { Installations = [f.Library.Entry.Installations[0] with { Folder = alias, Executable = Path.Combine(alias, "simple_game.exe") }] };
            await f.Approve(); var original = (await f.Service.PrepareAsync("g", null)).Snapshot!;
            var replacement = Path.Combine(f.Profile.Root, "new-mapping"); var game = Directory.CreateDirectory(Path.Combine(replacement, Path.GetFileName(f.Game))).FullName;
            File.Copy(f.Exe, Path.Combine(game, "simple_game.exe"));
            Assert.Equal(0, Run(drive + ":", "/D")); Assert.Equal(0, Run(drive + ":", replacement));
            Assert.Equal(LaunchOutcome.StaleInstallation, (await f.Service.LaunchAsync("g")).Outcome);
            Assert.Equal(LaunchOutcome.StaleInstallation, Assert.Throws<LaunchValidationException>(() => f.Platform.Start(original, default)).Outcome);
            Assert.Empty(Directory.GetFiles(f.Reports, "*.json"));
        }
        finally { Assert.Equal(0, Run(drive + ":", "/D")); }
    }
    [Fact]
    public async Task ProductionInstallationEditChangesNextSnapshotOnly()
    {
        await using var f = new LaunchFixture(); f.Set("game", ["old"]);
        var codec = new NativeLibraryCodec(); File.WriteAllBytes(f.Profile.Main, codec.Encode([LaunchSafetyTests.Entry(f.Library.Entry.Installations)], TransactionDifferentialTests.Clock));
        using var library = TransactionalLibrary.Open(codec, new TheOrder.Platform.Windows.IsolatedLibraryPersistence(f.Profile.Root));
        var captured = await library.CaptureLaunchAsync("g"); var old = captured.Installations[0];
        var prepared = new PreparedInstallation(old with { Arguments = ["next"] }, f.Identity.Inspect(f.Game).Observation!, f.Identity.Inspect(f.Exe, false).Observation, null);
        await library.SaveInstallationAsync((await library.InstallationsAsync("g")).Token, prepared, true, new(f.Identity));
        Assert.True(await library.WithLaunchAdmissionAsync(captured.Identity, old.Id, () => true));
        Assert.Equal("old", captured.Installations[0].Arguments.Last());
        Assert.Equal("next", (await library.CaptureLaunchAsync("g")).Installations[0].Arguments.Single());
    }
}
