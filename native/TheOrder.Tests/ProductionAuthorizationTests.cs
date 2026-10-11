using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class ProductionAuthorizationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void SelectedProductionRequiresExplicitAuthorizationBeforeFilesystem(bool authorized)
    {
        var request = NativeProfilePaths.Select(NativeProfileKind.Production, null, null, authorized);
        Assert.Equal(NativeProfilePaths.ProductionRoot, request.Root);
        Assert.Equal(authorized, request.ExplicitProductionAuthorization);
        var calls = 0;
        ProfilePathGuard.Probe.Value = new(new(NativeProfilePaths.LegacyRoot, NativeProfilePaths.ProductionRoot, NativeProfilePaths.DevelopmentRoot),
            () => { calls++; throw new IOException("stop at boundary without accessing formal data"); });
        try
        {
            if (authorized) { Assert.Throws<IOException>(() => NativeProfilePaths.Validate(request)); Assert.Equal(1, calls); }
            else { Assert.Equal("ProductionAuthorizationRequired", Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(request)).Code); Assert.Equal(0, calls); }
        }
        finally { ProfilePathGuard.Probe.Value = null; }
    }
    [Theory][InlineData("Development")][InlineData("Test")]
    public void ProductionAuthorizationCannotChangeProfileKind(string kind)
    {
        Assert.Equal("ProductionAuthorizationKindMismatch", Assert.Throws<NativeProfileException>(() =>
            NativeProfilePaths.Select(NativeProfileKind.Production, kind, @"C:\synthetic-authorization", true)).Code);
    }
    [Fact]
    public void AuthorizationDoesNotPersistIntoLaterRequests()
    {
        Assert.True(NativeProfilePaths.Select(NativeProfileKind.Development, "Production", null, true).ExplicitProductionAuthorization);
        Assert.False(NativeProfilePaths.Select(NativeProfileKind.Production, null, null).ExplicitProductionAuthorization);
    }
    [Theory][InlineData("legacy")][InlineData("child")][InlineData("parent")]
    public void AuthorizationDoesNotExpandTheExistingAllowedRoot(string mode)
    {
        var production = @"C:\synthetic-authorization\profiles\Production";
        var legacy = @"C:\synthetic-authorization\legacy";
        var path = mode == "legacy" ? legacy : mode == "child" ? production + @"\child" : Path.GetDirectoryName(production)!;
        var calls = 0;
        ProfilePathGuard.Probe.Value = new(new(legacy, production, @"C:\synthetic-authorization\profiles\Development"), () => calls++);
        try
        {
            var request = NativeProfilePaths.Select(NativeProfileKind.Production, null, path, true);
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(request)); Assert.Equal(0, calls);
        }
        finally { ProfilePathGuard.Probe.Value = null; }
    }
    [Fact]
    public void AuthorizedIsolatedProductionStillHasOneWriterAndStableIdentity()
    {
        using var f = new ProfileFixture();
        var request = NativeProfilePaths.Select(NativeProfileKind.Production, null, f.Root, true);
        Guid profile, store;
        using (var owner = NativeProfileStore.Open(request, NativeProfileTests.Codec))
        {
            profile = owner.Manifest.ProfileId; store = owner.Manifest.StoreId;
            using var writer = owner.CreateLibraryPersistence();
            Assert.Equal("LibraryWriterAlreadyOwned", Assert.Throws<NativeProfileException>(() => owner.CreateLibraryPersistence()).Code);
            Assert.NotNull(writer.ReadMain());
        }
        using var reopened = NativeProfileStore.Open(request, NativeProfileTests.Codec);
        Assert.Equal(profile, reopened.Manifest.ProfileId); Assert.Equal(store, reopened.Manifest.StoreId);
    }
}
