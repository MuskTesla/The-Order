using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NativeProfileTests(ITestOutputHelper output)
{
    internal static readonly NativeLibraryCodec Codec = new();
    [Fact]
    public async Task FreshBootstrapAndThreeReopensRetainIdentityLibraryPreferencesAndCredential()
    {
        using var f = new ProfileFixture(); Guid profileId, storeId;
        using (var profile = f.Open())
        {
            profileId = profile.Manifest.ProfileId; storeId = profile.Manifest.StoreId;
            Assert.Equal("Ready", profile.Manifest.State); Assert.Equal(NativeProfileKind.Production, profile.Manifest.ProfileKind);
            await using var library = TransactionalLibrary.Open(Codec, profile.CreateLibraryPersistence());
            Assert.Empty(library.Snapshot.Entries);
            var credential = new IsolatedCredentialStore(profile.Configuration()); Assert.False(await credential.HasCredentialAsync(default));
            var preferences = new IsolatedUiPreferencesStore(profile.Configuration()); Assert.Equal("grid", (await preferences.LoadAsync(default)).Layout);
            await library.ImportAsync(Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
            await library.UpdateAsync("g", new() { Notes = "native-owned" });
            await credential.SaveAsync("synthetic-token-1", default);
            await preferences.SaveAsync(new(NativeTheme.Dark, "list", "name", [new("v", "native view", new(Text: "synthetic"))]), default);
        }
        for (var i = 0; i < 3; i++)
        {
            using var reopened = f.Open(); Assert.Equal(profileId, reopened.Manifest.ProfileId); Assert.Equal(storeId, reopened.Manifest.StoreId);
            await using var library = TransactionalLibrary.Open(Codec, reopened.CreateLibraryPersistence());
            Assert.Equal("native-owned", Assert.Single(library.Snapshot.Entries).Notes);
            Assert.Equal(0, library.Snapshot.Entries[0].PlayCount); Assert.Equal(0, library.Snapshot.Entries[0].TotalPlaySeconds); Assert.Empty(library.Snapshot.Entries[0].Sessions);
            Assert.Equal("synthetic-token-1", await new IsolatedCredentialStore(reopened.Configuration()).ReadAsync(default));
            var preferences = await new IsolatedUiPreferencesStore(reopened.Configuration()).LoadAsync(default);
            Assert.Equal(NativeTheme.Dark, preferences.Theme); Assert.Equal("native view", Assert.Single(preferences.Filters).Name);
        }
    }
    public static IEnumerable<object[]> BootstrapFaults() => Enum.GetValues<BootstrapPoint>().Select(p => new object[] { p });
    [Theory, MemberData(nameof(BootstrapFaults))]
    public void EveryBootstrapFaultCanRestartWithoutSecondIdentity(BootstrapPoint point)
    {
        using var f = new ProfileFixture();
        Assert.Throws<IOException>(() => f.Open(new Fault(point)));
        var anchorPath = Path.Combine(f.Root, NativeProfileStore.BootstrapName);
        var anchor = File.Exists(anchorPath) ? File.ReadAllBytes(anchorPath) : null;
        using var restarted = f.Open(); Assert.Equal("Ready", restarted.Manifest.State);
        Assert.Empty(Codec.Decode(restarted.ReadComponent("library.json")!));
        if (anchor is not null) Assert.Equal(anchor, File.ReadAllBytes(anchorPath));
        Assert.Throws<IOException>(() => f.Open());
    }
    [Theory, MemberData(nameof(BootstrapFaults))]
    public async Task AbruptProcessExitAtEveryBootstrapBoundaryRecovers(BootstrapPoint point)
    {
        using var f = new ProfileFixture();
        using var child = f.Start("a", point.ToString(), false);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(86, child.ExitCode);
        var anchorPath = Path.Combine(f.Root, NativeProfileStore.BootstrapName);
        var anchor = File.Exists(anchorPath) ? File.ReadAllBytes(anchorPath) : null;
        using var restarted = f.Open(); Assert.Equal("Ready", restarted.Manifest.State);
        Assert.Empty(Codec.Decode(restarted.ReadComponent("library.json")!));
        if (anchor is not null) Assert.Equal(anchor, File.ReadAllBytes(anchorPath));
        output.WriteLine($"crash={point}; profile={restarted.Manifest.ProfileId}; store={restarted.Manifest.StoreId}; reopened=true");
    }
    [Fact]
    public async Task TwoRealProcessesConcurrentFirstStartHaveOnlyOneOwner()
    {
        using var f = new ProfileFixture(); using var a = f.Start("a", "none", true); using var b = f.Start("b", "none", true);
        await Until(() => File.Exists(Path.Combine(f.Parent, "a.started")) && File.Exists(Path.Combine(f.Parent, "b.started")));
        File.WriteAllText(Path.Combine(f.Parent, "go"), "go");
        await Until(() => a.HasExited || b.HasExited);
        Assert.True(a.HasExited ^ b.HasExited); Assert.Equal(41, a.HasExited ? a.ExitCode : b.ExitCode);
        await Until(() => File.Exists(Path.Combine(f.Parent, "a.ready")) || File.Exists(Path.Combine(f.Parent, "b.ready")));
        Assert.Throws<IOException>(() => f.Open());
        var identity = File.ReadAllBytes(Path.Combine(f.Root, NativeProfileStore.ManifestName));
        File.WriteAllText(Path.Combine(f.Parent, "release"), "release");
        await Task.WhenAll(a.WaitForExitAsync(), b.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { 0, 41 }, new[] { a.ExitCode, b.ExitCode }.Order().ToArray());
        using var reopened = f.Open(); Assert.Equal(identity, File.ReadAllBytes(Path.Combine(f.Root, NativeProfileStore.ManifestName)));
        output.WriteLine($"owners=1; profile={reopened.Manifest.ProfileId}; store={reopened.Manifest.StoreId}");
    }
    [Theory]
    [InlineData("StoreFormatVersion")][InlineData("MinimumWriterVersion")][InlineData("RequiredCapabilities")]
    [InlineData("ProfileId")][InlineData("StoreId")][InlineData("ProfileKind")][InlineData("FutureField")][InlineData("corrupt")]
    public void ManifestFutureAndIdentityMismatchRefuseWithoutOverwriting(string field)
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        var path = Path.Combine(f.Root, NativeProfileStore.ManifestName); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (field)
        {
            case "RequiredCapabilities": json[field] = new JsonArray("future-capability"); break;
            case "ProfileId": case "StoreId": json[field] = Guid.NewGuid(); break;
            case "ProfileKind": json[field] = "Development"; break;
            default: json[field] = 99; break;
        }
        File.WriteAllText(path, field == "corrupt" ? "{broken" : json.ToJsonString()); var before = f.Hashes();
        Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Theory]
    [InlineData("Version")][InlineData("ProfileId")][InlineData("StoreId")][InlineData("FutureField")][InlineData("libraryField")]
    [InlineData("libraryVersion")][InlineData("libraryFormat")]
    public void FutureComponentNeverFallsBackToOlderBackup(string field)
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        var path = Path.Combine(f.Root, "library.json"); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        if (field.StartsWith("library", StringComparison.Ordinal))
        {
            var data = JsonNode.Parse(Convert.FromBase64String(json["Data"]!.GetValue<string>()))!;
            if (field == "libraryField") data["futureImportant"] = true;
            else if (field == "libraryVersion") data["version"] = 2;
            else data["format"] = "future-native-format";
            json["Data"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data));
        }
        else if (field is "ProfileId" or "StoreId") json[field] = Guid.NewGuid(); else json[field] = 99;
        File.WriteAllText(path, json.ToJsonString()); var before = f.Hashes();
        Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Fact]
    public void SelectionAndFormalGuardsDoNotTouchFormalDirectories()
    {
        Assert.NotEqual(NativeProfilePaths.LegacyRoot, NativeProfilePaths.ProductionRoot);
        Assert.NotEqual(NativeProfilePaths.DevelopmentRoot, NativeProfilePaths.ProductionRoot);
        Assert.Equal(NativeProfileKind.Development, NativeProfilePaths.Select(NativeProfileKind.Development, null, null).Kind);
        Assert.Equal(NativeProfilePaths.ProductionRoot, NativeProfilePaths.Select(NativeProfileKind.Production, null, null).Root);
        Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Select(NativeProfileKind.Test, null, null));
        // No Exists/Open calls on formal paths: rejection occurs before filesystem inspection.
        foreach (var kind in Enum.GetValues<NativeProfileKind>())
        {
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(kind, NativeProfilePaths.ProductionRoot)));
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(kind, NativeProfilePaths.LegacyRoot, true)));
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(kind, NativeProfilePaths.LegacyRoot + ".", true)));
            Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Validate(new(kind, NativeProfilePaths.ProductionRoot + " ")));
        }
        Assert.Throws<NativeProfileException>(() => NativeProfilePaths.Select(NativeProfileKind.Development, "999", null));
    }
    [Fact]
    public async Task LegacyWritesAndOtherProfileScopesCannotContaminateNativeData()
    {
        using var f = new ProfileFixture(); var legacy = Path.Combine(f.Parent, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "library.json"), Codec.Encode([SessionTestRig.Entry()], SessionTestRig.Date));
        using var prod = f.Open(); using var dev = NativeProfileStore.Open(new(NativeProfileKind.Development, Path.Combine(f.Parent, "dev")), Codec);
        using var test = NativeProfileStore.Open(new(NativeProfileKind.Test, Path.Combine(f.Parent, "test")), Codec);
        Assert.Equal(3, new[] { prod.Manifest.StoreId, dev.Manifest.StoreId, test.Manifest.StoreId }.Distinct().Count());
        foreach (var p in new[] { prod, dev, test }) Assert.Empty(Codec.Decode(p.ReadComponent("library.json")!));
        var profiles = new[] { prod, dev, test };
        foreach (var p in profiles)
        {
            var others = profiles.Where(x => x != p).ToDictionary(x => x.Root, x => ProfileFixture.Hashes(x.Root));
            await new IsolatedCredentialStore(p.Configuration()).SaveAsync("synthetic-" + p.Manifest.ProfileKind, default);
            await new IsolatedUiPreferencesStore(p.Configuration()).SaveAsync(new(NativeTheme.Dark, "list"), default);
            await using (var library = TransactionalLibrary.Open(Codec, p.CreateLibraryPersistence()))
            {
                await library.ImportAsync(Codec.Encode([SessionTestRig.Entry(p.Manifest.ProfileKind.ToString(), 0, 0)], SessionTestRig.Date));
                var clock = new SessionFakeClock();
                await using var engine = new SessionEngine(library, new SessionFakeFilesystem(), new SessionFakePlatform(), new SessionFakePositions(), clock, new SessionFakeTokens(), autoPoll: false);
                var id = p.Manifest.ProfileKind.ToString(); await engine.InitializeAsync();
                var preview = await engine.PrepareAsync(id); await engine.ConfirmPositionAsync(preview.Snapshot!);
                Assert.True((await engine.LaunchAsync(id)).ProcessCreated); await engine.DrainAsync();
                await engine.SampleAsync(); clock.Advance(3); await engine.SampleAsync(); Assert.True(await engine.CheckpointForExitAsync());
                Assert.Equal(1, library.Snapshot.Entries[0].PlayCount); Assert.Single(library.Snapshot.Entries[0].Sessions);
            }
            p.CheckpointBackups();
            foreach (var (root, hash) in others) Assert.Equal(hash, ProfileFixture.Hashes(root));
        }
        var nativeBefore = f.Hashes(); File.WriteAllText(Path.Combine(legacy, "library.json"), "old-writer-rewrite"); Assert.Equal(nativeBefore, f.Hashes());
        var credentials = new IsolatedCredentialStore(prod.Configuration()); await credentials.SaveAsync("synthetic-update", default);
        Assert.Equal("synthetic-update", await credentials.ReadAsync(default)); await credentials.ClearAsync(default); Assert.False(await credentials.HasCredentialAsync(default));
        Assert.Equal("synthetic-Development", await new IsolatedCredentialStore(dev.Configuration()).ReadAsync(default));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BackupRestoreDrillPreservesCwdCoversPreferencesCredentialsAndIdentity(bool missing)
    {
        using var f = new ProfileFixture(); Guid id; byte[] expectedLibrary;
        var existing = Codec.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "normal", "library.json")));
        using (var profile = f.Open())
        {
            id = profile.Manifest.StoreId;
            await using (var library = TransactionalLibrary.Open(Codec, profile.CreateLibraryPersistence()))
            {
                var entry = SessionTestRig.Entry(total: 0, count: 0) with { Installations = [new("i", @"C:\fixture", @"C:\fixture\game.exe", ["--synthetic"], @"C:\fixture\files")], LocalCover = existing.First(x => x.LocalCover is not null).LocalCover };
                var remote = existing.First(x => x.Metadata is not null) with { Id = "remote", Installations = [], PreferredInstallationId = null, LocalCover = null, CoverChoice = null, Sessions = [], TotalPlaySeconds = 0, PlayCount = 0 };
                remote = remote with { Metadata = remote.Metadata! with { CoverUrl = "https://example.invalid/cover.jpg" } };
                await library.ImportAsync(Codec.Encode([entry, remote], SessionTestRig.Date));
            }
            await new IsolatedUiPreferencesStore(profile.Configuration()).SaveAsync(new(NativeTheme.Dark, "list"), default);
            await new IsolatedCredentialStore(profile.Configuration()).SaveAsync("synthetic-backup", default);
            profile.CheckpointBackups(); expectedLibrary = profile.ReadComponent("library.json")!;
        }
        foreach (var name in new[] { "library.json", "native-ui.json", "native-online-key.dpapi" })
        { if (missing) File.Delete(Path.Combine(f.Root, name)); else File.WriteAllText(Path.Combine(f.Root, name), "{corrupt"); }
        using (var restored = f.Open())
        {
            foreach (var name in new[] { "library.json", "native-ui.json", "native-online-key.dpapi" }) restored.Restore(name);
        }
        using var reopened = f.Open(); Assert.Equal(id, reopened.Manifest.StoreId); Assert.Equal(expectedLibrary, reopened.ReadComponent("library.json"));
        Assert.Equal("list", (await new IsolatedUiPreferencesStore(reopened.Configuration()).LoadAsync(default)).Layout);
        Assert.Equal("synthetic-backup", await new IsolatedCredentialStore(reopened.Configuration()).ReadAsync(default));
        var games = Codec.Decode(reopened.ReadComponent("library.json")!); var game = games.Single(x => x.Id == "g");
        Assert.Equal(@"C:\fixture\files", game.Installations[0].WorkingDirectory); Assert.NotNull(game.LocalCover); Assert.NotNull(games.Single(x => x.Id == "remote").Metadata!.CoverUrl);
    }
    [Fact]
    public void WrongKindCopiedStoreAndChangedIdentityAreNotAdopted()
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        Assert.Throws<NativeProfileException>(() => NativeProfileStore.Open(new(NativeProfileKind.Development, f.Root), Codec));
        Assert.Throws<NativeProfileException>(() => NativeProfileStore.Open(new(NativeProfileKind.Production, f.Root, ExpectedProfileId: Guid.NewGuid()), Codec));
        Assert.Throws<NativeProfileException>(() => NativeProfileStore.Open(new(NativeProfileKind.Production, f.Root, ExpectedStoreId: Guid.NewGuid()), Codec));
        var copy = Path.Combine(f.Parent, "copy"); Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(f.Root)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        Assert.Throws<NativeProfileException>(() => NativeProfileStore.Open(new(NativeProfileKind.Production, copy), Codec));
    }
    [Fact]
    public void UnmanagedOrReadyMissingCompanionsNeverResetToFresh()
    {
        using var f = new ProfileFixture(); Directory.CreateDirectory(f.Root); File.WriteAllText(Path.Combine(f.Root, "library.json"), "{}");
        Assert.Throws<NativeProfileException>(() => f.Open()); File.Delete(Path.Combine(f.Root, "library.json"));
        using (f.Open()) { }
        File.Delete(Path.Combine(f.Root, "native-ui.json")); File.Delete(Path.Combine(f.Root, "native-ui.json.bak"));
        var before = f.Hashes(); Assert.Throws<FileNotFoundException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Theory, MemberData(nameof(TransactionSafetyTests.Faults), MemberType = typeof(TransactionSafetyTests))]
    public async Task SharedAtomicPersistenceFailuresKeepMainAndMemoryConsistent(PersistenceTarget target, PersistenceStage stage)
    {
        using var f = new ProfileFixture();
        using (var profile = f.Open())
        await using (var library = TransactionalLibrary.Open(Codec, profile.CreateLibraryPersistence()))
            await library.ImportAsync(Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
        using var reopened = NativeProfileStore.Open(f.Request, Codec, persistenceProbe: new PersistenceFault(target, stage));
        await using var writer = TransactionalLibrary.Open(Codec, reopened.CreateLibraryPersistence());
        if (stage == PersistenceStage.AfterReplace) { var result = await writer.UpdateAsync("g", new() { Notes = "updated" }); Assert.NotNull(result.Warning); }
        else await Assert.ThrowsAsync<IOException>(() => writer.UpdateAsync("g", new() { Notes = "updated" }));
        Assert.Equal(writer.Snapshot.Entries[0].Notes, Codec.Decode(reopened.ReadComponent("library.json")!)[0].Notes);
    }
    [Fact]
    public void BootstrapConflictRecoveryLoopsReleaseAllHandles()
    {
        using var f = new ProfileFixture();
        void Cycle()
        {
            using var profile = f.Open(); Assert.Throws<IOException>(() => f.Open());
            profile.CheckpointBackups(); profile.Restore("library.json");
        }
        for (var i = 0; i < 5; i++) Cycle(); GC.Collect(); GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.HandleCount;
        for (var i = 0; i < 30; i++) Cycle(); GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh(); var after = process.HandleCount;
        output.WriteLine($"30 open/conflict/backup/restore/close cycles: handles {before} -> {after}"); Assert.InRange(after - before, -100, 6);
    }
    private sealed class Fault(BootstrapPoint point) : IBootstrapProbe { public void Reach(BootstrapPoint actual) { if (actual == point) throw new IOException("synthetic bootstrap failure"); } }
    private sealed class PersistenceFault(PersistenceTarget target, PersistenceStage stage) : IPersistenceProbe
    { public Task ReachAsync(PersistenceTarget t, PersistenceStage s) { if (t == target && s == stage) throw new IOException("synthetic atomic fault"); return Task.CompletedTask; } }
    internal static async Task Until(Func<bool> predicate)
    { var deadline = DateTime.UtcNow.AddSeconds(25); while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); } }
}

[CollectionDefinition("Native profile resource", DisableParallelization = true)]
public sealed class NativeProfileResourceCollection;

internal sealed class ProfileFixture : IDisposable
{
    public string Parent { get; } = Path.Combine(Path.GetTempPath(), "the-order-profile-test-" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Parent, "production-like");
    public NativeProfileRequest Request => new(NativeProfileKind.Production, Root);
    public ProfileFixture() { Directory.CreateDirectory(Parent); File.WriteAllText(Path.Combine(Parent, ".profile-probe-fixture"), "synthetic"); }
    public NativeProfileStore Open(IBootstrapProbe? probe = null) => NativeProfileStore.Open(Request, NativeProfileTests.Codec, probe);
    public string[] Hashes() => Hashes(Root);
    public static string[] Hashes(string root) => Directory.GetFiles(root).Where(p => Path.GetFileName(p) != ".the-order-writer.lock").Order().Select(p => Path.GetFileName(p) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public Process Start(string name, string fault, bool wait)
    {
        var native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var dll = Path.Combine(native, "TheOrder.ProfileProbe", "bin", configuration, "net10.0-windows10.0.26100.0", "TheOrder.ProfileProbe.dll");
        var dotnet = Path.Combine(native, "..", ".artifacts", "native-phase1", "tools", "dotnet", "dotnet.exe");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { dll, Root, name, fault, wait ? "wait" : "once" }) start.ArgumentList.Add(a);
        return Process.Start(start)!;
    }
    public void Dispose()
    {
        File.WriteAllText(Path.Combine(Parent, "release"), "release");
        // Only this fixture's verified GUID-named temp subtree is deleted.
        if (!Parent.StartsWith(Path.Combine(Path.GetTempPath(), "the-order-profile-test-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        for (var i = 0; i < 10; i++) { try { Directory.Delete(Parent, true); return; } catch (IOException) { Thread.Sleep(100); } }
    }
}
