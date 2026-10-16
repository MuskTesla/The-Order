using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class PublicBetaIsolationTests
{
    [Fact]
    public void DefaultBetaIsSeparateWithoutOpeningAnyDirectory()
    {
        var request = NativeProfilePaths.Select(NativeProfileKind.Beta, null, null);
        Assert.Equal(NativeProfileKind.Beta, request.Kind);
        Assert.Equal(NativeProfilePaths.BetaRoot, request.Root);
        Assert.False(request.ExplicitProductionAuthorization);
        Assert.False(NativeProfilePaths.Overlaps(request.Root, NativeProfilePaths.ProductionRoot));
        Assert.False(NativeProfilePaths.Overlaps(request.Root, NativeProfilePaths.LegacyRoot));
    }

    [Theory]
    [InlineData("Production", false)] [InlineData("Production", true)]
    [InlineData("Development", false)] [InlineData("Test", true)] [InlineData("Beta", true)]
    public void BetaCannotSelectStableEvenWithOldAuthorizationFlag(string kind, bool authorized)
        => Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Select(NativeProfileKind.Beta, kind, @"C:\synthetic-beta", authorized));

    [Theory]
    [InlineData("Production")] [InlineData("Test")] [InlineData("Development")]
    public void BetaRootCannotBeOpenedByOtherProfileKinds(string kind)
    {
        var count = 0;
        var roots = new ProfilePathGuard.KnownRoots(@"C:\synthetic\legacy", @"C:\synthetic\stable", @"C:\synthetic\dev", @"C:\synthetic\beta");
        ProfilePathGuard.Probe.Value = new(roots, () => { count++; });
        try {
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(Enum.Parse<NativeProfileKind>(kind), roots.Beta!, true)));
            Assert.Equal(0, count);
        } finally { ProfilePathGuard.Probe.Value = null; }
    }

    [Theory]
    [InlineData("Production")] [InlineData("Development")] [InlineData("Legacy")]
    public void BetaCannotUseAnyFormalAliasBeforeFilesystem(string scope)
    {
        var count = 0;
        var roots = new ProfilePathGuard.KnownRoots(@"C:\synthetic\legacy", @"C:\synthetic\stable", @"C:\synthetic\dev", @"C:\synthetic\beta");
        ProfilePathGuard.Probe.Value = new(roots, () => { count++; });
        var root = scope == "Production" ? roots.Production : scope == "Development" ? roots.Development : roots.Legacy;
        try {
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(NativeProfileKind.Beta, @"\\?\" + root + @"\child\..")));
            Assert.Equal(0, count);
        } finally { ProfilePathGuard.Probe.Value = null; }
    }

    [Fact]
    public async Task BetaRestartsWithoutConvertingOrSharingStableStore()
    {
        using var fixture = new ProfileFixture();
        var request = fixture.Request with { Kind = NativeProfileKind.Beta };
        Guid profileId;
        using (var profile = NativeProfileStore.Open(request, NativeProfileTests.Codec)) {
            profileId = profile.Manifest.ProfileId;
            await using var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence());
            await library.ImportAsync(NativeProfileTests.Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
            await library.UpdateAsync("g", new() { Notes = "synthetic-beta-note" });
            Assert.Equal("dev.local.kungalmanager.beta", WindowsRecentGames.AppId(profile));
        }
        var before = fixture.Hashes();
        Assert.Throws<NativeProfileException>(() => fixture.Open());
        Assert.Equal(before, fixture.Hashes());
        using var reopened = NativeProfileStore.Open(request, NativeProfileTests.Codec);
        Assert.Equal(profileId, reopened.Manifest.ProfileId);
        await using var restored = TransactionalLibrary.Open(NativeProfileTests.Codec, reopened.CreateLibraryPersistence());
        Assert.Equal("synthetic-beta-note", Assert.Single(restored.Snapshot.Entries).Notes);
        var installer = new WindowsUpdateInstaller(reopened.Configuration(), new WindowsUpdatePackages(reopened.Configuration()));
        Assert.False(installer.Availability.CanInstall);
    }

    [Fact]
    public async Task PreviewUpdateNeverQueriesOrInstallsStableFeed()
    {
        var fake = new AppUpdateTests.Fake();
        using var vm = new AppUpdateViewModel(fake, fake, fake, "1.2.0-beta.1");
        await vm.CheckAsync(); await vm.CheckAutomaticallyAsync(); await vm.ReadReceiptAsync();
        var shutdown = false;
        await vm.InstallAsync(_ => { shutdown = true; return Task.FromResult(true); });
        Assert.False(shutdown); Assert.False(vm.CanInstall); Assert.Null(vm.Latest);
        Assert.Equal(0, fake.Checks); Assert.Equal(0, fake.Downloads); Assert.Contains("Pre-release", vm.Message);
    }

    [Theory]
    [InlineData("1.2.0-beta.1")] [InlineData("v1.2.0-beta.1")]
    public void StableVersionComparisonNeverOffersBeta(string version)
    {
        Assert.False(AppUpdateIdentity.TryVersion(version, out _));
        Assert.False(AppUpdateIdentity.IsNewer(version, "1.1.0"));
        Assert.False(AppUpdateIdentity.ValidDownload(new("https://github.com/Ph-Rank/The-Order/releases/download/v1.2.0-beta.1/The-Order-1.2.0-beta.1-Setup-x64.exe"), version));
    }
}
