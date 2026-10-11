using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class FilesystemIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "the-order-identity-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFilesystemIdentity identity = new();
    private readonly List<string> links = [];
    private string Target => Path.Combine(root, "Game with long name");
    public FilesystemIdentityTests() { Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Target, "game.exe"), "synthetic-not-executable"); }
    [Theory]
    [InlineData("trailing")][InlineData("dot")][InlineData("dotdot")][InlineData("slash")][InlineData("case")]
    public void LexicalAliasesKeepInputButResolveSameIdentity(string kind)
    {
        var path = kind switch { "trailing" => Target + "\\", "dot" => Target + "\\.", "dotdot" => Target + "\\..\\Game with long name", "slash" => Target.Replace('\\', '/'), _ => Target.ToUpperInvariant() };
        var result = identity.Inspect(path); Assert.Equal(PreflightStatus.Valid, result.Status);
        Assert.Equal(path, result.Observation!.StoredPath);
        Assert.Equal(identity.Inspect(Target).Observation!.Identity, result.Observation.Identity);
    }
    [Fact] public void FileIsNotDirectoryAndMissingIsNotEmpty()
    {
        Assert.Equal(PreflightStatus.FileInsteadOfDirectory, identity.Inspect(Path.Combine(Target, "game.exe")).Status);
        Assert.Equal(PreflightStatus.NotFound, identity.Inspect(Path.Combine(root, "missing")).Status);
        Assert.Equal(PreflightStatus.OtherIOError, identity.Inspect("relative").Status);
    }
    [Fact] public void JunctionAndBrokenJunctionAreDistinct()
    {
        var link = Junction("junction", Target);
        var result = identity.Inspect(link); Assert.True(result.IsValid, result.Message);
        Assert.True(result.Observation!.ViaReparsePoint); Assert.Equal(identity.Inspect(Target).Observation!.Identity, result.Observation.Identity);
        Directory.Move(Target, Target + "-moved");
        Assert.Equal(PreflightStatus.BrokenLink, identity.Inspect(link).Status);
    }
    [Fact] public async Task AliasIsDuplicateAcrossGamesAndUnavailableHistoryIsRetained()
    {
        var link = Junction("alias", Target); var service = new InstallationPreflightService(identity);
        var duplicate = await service.CheckAsync(link, [new("old", "已入库", "install", Target)]);
        Assert.Equal(PreflightStatus.SameTargetViaAlias, duplicate.Path.Status); Assert.Equal("old", duplicate.Duplicate!.GameId);
        var unknown = await service.CheckAsync(Target, [new("gone", "离线", "missing", Path.Combine(root, "gone"))]);
        Assert.False(unknown.CanAttach); Assert.Equal(DuplicateStatus.Indeterminate, unknown.DuplicateStatus);
    }
    [Fact] public void RenamePreservesIdentityButInvalidatesPathSnapshot()
    {
        var observation = identity.Inspect(Target).Observation!; var moved = Target + "-renamed";
        Directory.Move(Target, moved); Assert.Equal(observation.Identity, identity.Inspect(moved).Observation!.Identity);
        Assert.Throws<InvalidDataException>(() => new InstallationPreflightService(identity).Revalidate(observation));
        Directory.CreateDirectory(Target); Assert.NotEqual(observation.Identity, identity.Inspect(Target).Observation!.Identity);
    }
    [Fact] public void LongPathAndHardLinkUseFullIdentity()
    {
        var longPath = Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 100)); Directory.CreateDirectory(longPath);
        Assert.True(identity.Inspect(longPath).IsValid);
        var file = Path.Combine(Target, "game.exe"); var link = Path.Combine(root, "other.exe");
        Assert.True(CreateHardLink(link, file, IntPtr.Zero));
        Assert.Equal(identity.Inspect(file, false).Observation!.Identity, identity.Inspect(link, false).Observation!.Identity);
        Assert.NotEqual(identity.Inspect(Target).Observation!.Identity, identity.Inspect(longPath).Observation!.Identity);
    }
    [Fact] public void CapabilityLabRecordsActualSupportInsteadOfAssumingAliases()
    {
        var report = new List<object>(); var expected = identity.Inspect(Target).Observation!.Identity;
        var shortName = new StringBuilder(32768);
        var length = GetShortPathName(Target, shortName, (uint)shortName.Capacity);
        if (length > 0 && !string.Equals(shortName.ToString(), Target, StringComparison.OrdinalIgnoreCase))
        { Assert.Equal(expected, identity.Inspect(shortName.ToString()).Observation!.Identity); report.Add(new { scenario = "8.3", status = "supported", reason = "Actual alternate name resolves to identical 128-bit identity." }); }
        else report.Add(new { scenario = "8.3", status = "unsupported", reason = "Volume supplied no alternate short name." });
        var symbolic = Path.Combine(root, "symbolic");
        try { Directory.CreateSymbolicLink(symbolic, Target); links.Add(symbolic); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { report.Add(new { scenario = "symlink", status = "unsupported", reason = e.GetType().Name }); }
        if (links.Contains(symbolic)) { Assert.Equal(expected, identity.Inspect(symbolic).Observation!.Identity); report.Add(new { scenario = "symlink", status = "supported", reason = "Resolved actual synthetic directory link." }); }
        var used = DriveInfo.GetDrives().Select(d => d.Name[..1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drive = Enumerable.Range('R', 9).Select(c => ((char)c).ToString()).FirstOrDefault(c => !used.Contains(c));
        if (drive is null) report.Add(new { scenario = "subst", status = "skipped", reason = "No unused drive letter." });
        else
        {
            bool mapped = Run("subst.exe", drive + ":", root) == 0;
            try
            {
                if (mapped) { Assert.Equal(expected, identity.Inspect(drive + ":\\" + Path.GetFileName(Target)).Observation!.Identity); report.Add(new { scenario = "subst", status = "supported", reason = "Temporary mapping resolves to original volume/file identity." }); }
                else report.Add(new { scenario = "subst", status = "unsupported", reason = "Mapping command refused." });
            }
            finally { if (mapped) Assert.Equal(0, Run("subst.exe", drive + ":", "/D")); }
        }
        var denied = Directory.CreateDirectory(Path.Combine(root, "denied")); var original = denied.GetAccessControl();
        var restriction = denied.GetAccessControl(); restriction.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes | FileSystemRights.ListDirectory, AccessControlType.Deny));
        try { denied.SetAccessControl(restriction); Assert.Equal(PreflightStatus.AccessDenied, identity.Inspect(denied.FullName).Status); }
        finally { var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); denied.SetAccessControl(restore); }
        report.Add(new { scenario = "access-denied", status = "supported", reason = "Synthetic directory ACL denial observed; original ACL restored." });
        report.Add(new { scenario = "UNC", status = "skipped", reason = "No authorized synthetic SMB share; do not test administrative or user shares." });
        report.Add(new { scenario = "generic-reparse", status = "skipped", reason = "No external filter fixture; unknown tag rejection reviewed, not a runtime claim." });
        var output = Environment.GetEnvironmentVariable("THE_ORDER_IDENTITY_LAB_REPORT");
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static int Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; Assert.True(process.WaitForExit(15000)); return process.ExitCode;
    }
    private string Junction(string name, string target)
    {
        var link = Path.Combine(root, name);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
        using var process = Process.Start(start)!; Assert.True(process.WaitForExit(15000)); Assert.Equal(0, process.ExitCode); links.Add(link); return link;
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("the-order-identity-", StringComparison.Ordinal)) throw new InvalidOperationException();
        foreach (var link in links) Directory.Delete(link); // Unlink first: never recurse through an alias during cleanup.
        Directory.Delete(full, true);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder result, uint capacity);
}
