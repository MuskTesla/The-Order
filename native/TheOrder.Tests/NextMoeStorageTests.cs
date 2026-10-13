using System.Security.Cryptography;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NextMoeStorageTests
{
    [Fact] public async Task ForeignWindowsUserSyncEnvelopeIsRejectedEvenWithinSameProfile()
    {
        using var fixture = new Fixture(); var store = new WindowsPersonalSyncStore(fixture.Profile.Configuration());
        await store.SaveAsync(new("42", NextMoeOAuthIdentity.ClientId, new(), [], [], [new("200", Rating: 9)], []), default);
        var path = Directory.GetFiles(fixture.Profile.Root, "native-nextmoe-sync-*.json").Single();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; document["WindowsUser"] = "S-1-5-21-foreign-user";
        File.WriteAllText(path, document.ToJsonString()); var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<OnlineException>(() => store.LoadAsync("42", NextMoeOAuthIdentity.ClientId, default)); Assert.Equal(before, File.ReadAllBytes(path));
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Parent = Path.Combine(Path.GetTempPath(), "the-order-nextmoe-test-" + Guid.NewGuid().ToString("N"));
        internal readonly NativeProfileStore Profile;
        internal Fixture() => Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Path.Combine(Parent, "profile")), new NativeLibraryCodec());
        public void Dispose()
        {
            Profile.Dispose();
            var path = Path.GetFullPath(Parent); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("the-order-nextmoe-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture escaped temp root.");
            Directory.Delete(path, true);
        }
    }
    [Fact] public async Task RealDpapiRoundTripKeepsLegacyKeyAndLibraryUnchanged()
    {
        using var fixture = new Fixture(); var config = fixture.Profile.Configuration();
        var keys = new IsolatedCredentialStore(config); await keys.SaveAsync("synthetic-legacy-key", default);
        var beforeKey = File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "native-online-key.dpapi"));
        var beforeLibrary = File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json"));
        var record = new NextMoeAuthRecord(1, NextMoeAccessMode.Account, new("42", "合成用户"), "synthetic-refresh-secret", [.. NextMoeOAuthIdentity.CatalogScopes]);
        await new WindowsNextMoeAuthStore(config).SaveAsync(record, default);
        var loaded = await new WindowsNextMoeAuthStore(config).LoadAsync(default); Assert.Equal(record.RefreshToken, loaded!.RefreshToken); Assert.Equal(record.Account, loaded.Account);
        var encrypted = File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "native-nextmoe-auth.dpapi"));
        Assert.DoesNotContain("synthetic-refresh-secret", System.Text.Encoding.UTF8.GetString(encrypted));
        Assert.Equal(beforeKey, File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "native-online-key.dpapi")));
        Assert.Equal(beforeLibrary, File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json")));
        Assert.Equal("synthetic-legacy-key", await keys.ReadAsync(default));
    }
    [Fact] public async Task RotatingMarkerIsDurableAndCannotBeRestoredAsOldValidLogin()
    {
        using var fixture = new Fixture(); var store = new WindowsNextMoeAuthStore(fixture.Profile.Configuration());
        var pending = new NextMoeAuthRecord(1, NextMoeAccessMode.Account, new("42", "合成用户"), "synthetic-old-refresh", [.. NextMoeOAuthIdentity.CatalogScopes], true);
        await store.SaveAsync(pending, default); Assert.True((await new WindowsNextMoeAuthStore(fixture.Profile.Configuration()).LoadAsync(default))!.RefreshInProgress);
        await store.SaveAsync(pending with { RefreshToken = "synthetic-new-refresh", RefreshInProgress = false }, default);
        Assert.Equal("synthetic-new-refresh", (await store.LoadAsync(default))!.RefreshToken);
        Assert.False(File.Exists(Path.Combine(fixture.Profile.Root, "native-nextmoe-auth.dpapi.bak")));
    }
    [Fact] public async Task CopiedCredentialCannotCrossProfileOrStoreIdentity()
    {
        using var first = new Fixture(); using var second = new Fixture();
        await new WindowsNextMoeAuthStore(first.Profile.Configuration()).SaveAsync(new(1, NextMoeAccessMode.Account, new("42", "合成用户"), "synthetic-refresh", [.. NextMoeOAuthIdentity.CatalogScopes]), default);
        File.Copy(Path.Combine(first.Profile.Root, "native-nextmoe-auth.dpapi"), Path.Combine(second.Profile.Root, "native-nextmoe-auth.dpapi"));
        Assert.Equal(OnlineFailure.CredentialStorageCorrupt, (await Assert.ThrowsAsync<OnlineException>(() => new WindowsNextMoeAuthStore(second.Profile.Configuration()).LoadAsync(default))).Failure);
    }
    [Fact] public async Task CorruptCredentialNeverResetsOrFallsBackToAnonymous()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Profile.Root, "native-nextmoe-auth.dpapi");
        var data = RandomNumberGenerator.GetBytes(128); File.WriteAllBytes(path, data);
        await Assert.ThrowsAsync<OnlineException>(() => new WindowsNextMoeAuthStore(fixture.Profile.Configuration()).LoadAsync(default)); Assert.Equal(data, File.ReadAllBytes(path));
    }
    [Fact] public async Task AccountSyncCheckpointsSurviveReloadAndRemainAccountIsolated()
    {
        using var fixture = new Fixture(); var store = new WindowsPersonalSyncStore(fixture.Profile.Configuration());
        var beforeLibrary = File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json"));
        var state = new AccountSyncState("42", NextMoeOAuthIdentity.ClientId, new(), [], [], [new("200", 123, 9)], []);
        await store.SaveAsync(state, default); var loaded = await new WindowsPersonalSyncStore(fixture.Profile.Configuration()).LoadAsync("42", NextMoeOAuthIdentity.ClientId, default);
        Assert.Equal(123, loaded!.Imported.Single().Minutes); Assert.Equal(9, loaded.Imported.Single().Rating);
        Assert.Null(await store.LoadAsync("99", NextMoeOAuthIdentity.ClientId, default)); Assert.Null(await store.LoadAsync("42", "another-public-client", default));
        Assert.Equal(beforeLibrary, File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json")));
    }
    [Fact] public async Task MissingNewCompanionsDoNotChangeExistingProfileComponents()
    {
        using var fixture = new Fixture(); var config = fixture.Profile.Configuration();
        var originalFiles = Directory.GetFiles(fixture.Profile.Root);
        var files = originalFiles.Where(x => !x.EndsWith(".lock", StringComparison.Ordinal)).ToDictionary(x => Path.GetFileName(x)!, File.ReadAllBytes);
        Assert.Null(await new WindowsNextMoeAuthStore(config).LoadAsync(default)); Assert.Null(await new WindowsPersonalSyncStore(config).LoadAsync("42", NextMoeOAuthIdentity.ClientId, default));
        Assert.Equal(originalFiles.Length, Directory.GetFiles(fixture.Profile.Root).Length);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(fixture.Profile.Root, file.Key!)));
    }
    [Fact] public async Task SyncFileCopiedAcrossProfileFailsClosed()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var firstStore = new WindowsPersonalSyncStore(first.Profile.Configuration());
        await firstStore.SaveAsync(new("42", NextMoeOAuthIdentity.ClientId, new(), [], [], [], []), default);
        var file = Directory.GetFiles(first.Profile.Root, "native-nextmoe-sync-*.json").Single();
        File.Copy(file, Path.Combine(second.Profile.Root, Path.GetFileName(file)));
        await Assert.ThrowsAsync<OnlineException>(() => new WindowsPersonalSyncStore(second.Profile.Configuration()).LoadAsync("42", NextMoeOAuthIdentity.ClientId, default));
    }
}
