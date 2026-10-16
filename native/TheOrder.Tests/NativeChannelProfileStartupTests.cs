using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NativeChannelProfileStartupTests
{
    [Theory]
    [InlineData("legacy-beta")]
    [InlineData("missing/Profiles/Beta")]
    public void FreshInstallationWithoutLegacyBetaDirectoryKeepsRequestedProfile(string relative)
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var beta = Path.Combine(f.Parent, relative);
        var before = Directory.GetFileSystemEntries(f.Parent).Order().ToArray();
        var request = f.Request;
        var resolved = NativeChannelProfiles.ResolveAt(request, install, f.Root, beta, UnexpectedChoice);
        Assert.Same(request, resolved);
        Assert.False(Directory.Exists(beta));
        Assert.False(Directory.Exists(f.Root));
        Assert.Equal(before, Directory.GetFileSystemEntries(f.Parent).Order().ToArray());
    }

    [Fact]
    public void EmptyLegacyDirectoryDoesNotCreateOrAdoptAProfile()
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(f.Parent, "legacy-beta")).FullName;
        var request = f.Request;
        Assert.Same(request, NativeChannelProfiles.ResolveAt(request, install, f.Root, beta, UnexpectedChoice));
        Assert.Empty(Directory.GetFileSystemEntries(beta));
        Assert.False(Directory.Exists(f.Root));
    }

    [Fact]
    public void ExistingRequestedProfileSurvivesAbsentLegacyDirectoryAndReopensUnchanged()
    {
        using var f = new ProfileFixture();
        Guid profileId;
        using (var initial = f.Open()) profileId = initial.Manifest.ProfileId;
        var before = f.Hashes();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var resolved = NativeChannelProfiles.ResolveAt(f.Request, install, f.Root, Path.Combine(f.Parent, "absent-beta"), UnexpectedChoice);
        using var reopened = NativeProfileStore.Open(resolved, NativeProfileTests.Codec);
        Assert.Equal(profileId, reopened.Manifest.ProfileId);
        Assert.Equal(before, f.Hashes());
    }

    [Theory]
    [InlineData(NativeProfileKind.Production)]
    [InlineData(NativeProfileKind.Beta)]
    public void MissingExplicitlySelectedProfileMustNotFallBackOrCreateAnotherBookshelf(NativeProfileKind kind)
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var beta = Path.Combine(f.Parent, "absent-beta");
        var routing = Path.Combine(install, NativeChannelProfiles.FileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Kind = kind, ProfileId = Guid.NewGuid(), StoreId = Guid.NewGuid() });
        File.WriteAllBytes(routing, bytes);
        var error = Assert.Throws<NativeProfileException>(() => NativeChannelProfiles.ResolveAt(f.Request, install, f.Root, beta, UnexpectedChoice));
        Assert.Equal("SelectedProfileMissing", error.Code);
        Assert.Equal(bytes, File.ReadAllBytes(routing));
        Assert.False(Directory.Exists(f.Root));
        Assert.False(Directory.Exists(beta));
    }

    [Fact]
    public void MissingInstallationDirectoryIsStillAnError()
    {
        using var f = new ProfileFixture();
        Assert.Throws<DirectoryNotFoundException>(() => NativeChannelProfiles.ResolveAt(f.Request,
            Path.Combine(f.Parent, "absent-installation"), f.Root, Path.Combine(f.Parent, "absent-beta"), UnexpectedChoice));
    }

    [Fact]
    public void InvalidRoutingFileIsNotTreatedAsMissingLegacyData()
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var routing = Path.Combine(install, NativeChannelProfiles.FileName);
        File.WriteAllText(routing, "{broken");
        Assert.ThrowsAny<JsonException>(() => NativeChannelProfiles.ResolveAt(f.Request, install, f.Root,
            Path.Combine(f.Parent, "absent-beta"), UnexpectedChoice));
        Assert.Equal("{broken", File.ReadAllText(routing));
        Assert.False(Directory.Exists(f.Root));
    }

    [Fact]
    public void InvalidLegacyManifestIsNotTreatedAsAbsent()
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(f.Parent, "legacy-beta")).FullName;
        var manifest = Path.Combine(beta, NativeProfileStore.ManifestName);
        File.WriteAllText(manifest, "{broken");
        Assert.ThrowsAny<JsonException>(() => NativeChannelProfiles.ResolveAt(f.Request, install, f.Root, beta, () => true));
        Assert.Equal("{broken", File.ReadAllText(manifest));
        Assert.False(Directory.Exists(f.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDirectoryJunctionCannotBeHiddenByMissingManifest(bool hasManifest)
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(f.Parent, "junction-target")).FullName;
        if (hasManifest) File.WriteAllText(Path.Combine(target, NativeProfileStore.ManifestName), "{broken");
        var beta = Path.Combine(f.Parent, "legacy-beta-link");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + beta.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(15000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var error = Assert.Throws<NativeProfileException>(() => NativeChannelProfiles.ResolveAt(f.Request, install, f.Root, beta, UnexpectedChoice));
            Assert.Equal("ProfileLinkForbidden", error.Code);
            Assert.False(Directory.Exists(f.Root));
        }
        finally { Directory.Delete(beta); }
    }

    [Theory]
    [InlineData("legacy-beta")]
    [InlineData("blocked/Profiles/Beta")]
    public void FileInLegacyDirectoryChainIsNotTreatedAsFreshInstallation(string relative)
    {
        using var f = new ProfileFixture();
        var install = Directory.CreateDirectory(Path.Combine(f.Parent, "installation")).FullName;
        var obstruction = Path.Combine(f.Parent, relative.Split('/')[0]);
        File.WriteAllText(obstruction, "synthetic existing user file");
        var error = Assert.Throws<NativeProfileException>(() => NativeChannelProfiles.ResolveAt(f.Request, install, f.Root,
            Path.Combine(f.Parent, relative), UnexpectedChoice));
        Assert.Equal("InvalidLegacyBetaProfileDirectory", error.Code);
        Assert.Equal("synthetic existing user file", File.ReadAllText(obstruction));
        Assert.False(Directory.Exists(f.Root));
    }

    private static bool? UnexpectedChoice() => throw new InvalidOperationException("No existing legacy profile should require a choice.");
}
