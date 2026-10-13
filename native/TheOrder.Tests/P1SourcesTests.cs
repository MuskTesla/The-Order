using System.Collections.Immutable;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;
[Collection("Native profile resource")]
public sealed class P1SourcesTests
{
    private sealed class Factory(SessionMemoryDisk disk) : ITransactionalLibraryFactory
    { public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.FromResult(TransactionalLibrary.Open(new NativeLibraryCodec(), new SessionMemoryPersistence(disk))); }
    private sealed class Store : ILibrarySourcesStore
    {
        public ImmutableArray<LibrarySource> Values = []; public bool Fail;
        public Task<ImmutableArray<LibrarySource>> LoadAsync(CancellationToken cancellation) => Task.FromResult(Values);
        public Task SaveAsync(ImmutableArray<LibrarySource> value, CancellationToken cancellation) { if (Fail) throw new IOException("synthetic"); Values = value; return Task.CompletedTask; }
    }
    private sealed class Lab : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "the-order-p1-fixture-" + Guid.NewGuid().ToString("N"));
        public Lab() => Directory.CreateDirectory(Root);
        public string Folder(string relative) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(path); return path; }
        public string Game(string relative) { var path = Folder(relative); File.WriteAllText(Path.Combine(path, "game.exe"), "Synthetic, never executed"); return path; }
        public void Dispose() { if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "the-order-p1-fixture-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(Root, true); }
    }
    private static (WindowsFilesystemIdentity Fs, BoundedScanner Scanner, WindowsSteamLibraryScanner Steam) Adapters()
    { var identity = new WindowsFilesystemIdentity(); var scanner = new BoundedScanner(identity, new WindowsScanFilesystem()); return (identity, scanner, new(identity, scanner)); }
    private static OfflineLibraryClient Client(params LibraryEntry[] entries) => new(new Factory(new() { Main = new NativeLibraryCodec().Encode([.. entries], Clock) }), new LegacyClientTextRules());
    [Fact] public async Task MultipleRootsDiscoverWithoutImportAndOfflineRemovalKeepsBookshelf()
    {
        using var lab = new Lab(); var first = lab.Folder("first"); var known = lab.Game("first/known"); lab.Game("first/new"); var second = lab.Folder("second"); lab.Game("second/another");
        await using var client = Client(Entry(folder: known)); await client.LoadAsync(default); var store = new Store(); var a = Adapters();
        await using var service = new LibrarySourcesService(store, a.Fs, new WindowsScanFilesystem(), a.Scanner, a.Steam, client);
        await service.AddAsync(first, LibrarySourceKind.Folder); await service.AddAsync(second, LibrarySourceKind.Folder); await service.ScanAsync(true);
        Assert.Equal(2, service.Sources.Length); Assert.Equal(2, service.Candidates.Length); Assert.Single(client.CurrentSnapshot!.Entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.AddAsync(first, LibrarySourceKind.Folder));
        Directory.Move(first, Path.Combine(lab.Root, "offline")); await service.ScanAsync(true); Assert.Contains(service.Scans, s => s.Status == ScanStatus.Failed); Assert.Single(client.CurrentSnapshot.Entries);
        await service.ConfigureAsync(service.Sources[0].Id, null); Assert.Single(service.Sources); Assert.Single(client.CurrentSnapshot.Entries);
    }
    [Fact] public async Task SaveFailureDoesNotPublishRootsAndWatcherChangesAreDebounced()
    {
        using var lab = new Lab(); var root = lab.Folder("games"); lab.Game("games/first"); await using var client = Client(); await client.LoadAsync(default); var store = new Store(); var a = Adapters();
        await using var service = new LibrarySourcesService(store, a.Fs, new WindowsScanFilesystem(), a.Scanner, a.Steam, client);
        store.Fail = true; await Assert.ThrowsAsync<IOException>(() => service.AddAsync(root, LibrarySourceKind.Folder)); Assert.Empty(service.Sources);
        store.Fail = false; await service.AddAsync(root, LibrarySourceKind.Folder); await service.ScanAsync(true); Assert.Single(service.Candidates);
        lab.Game("games/second"); service.Changed(service.Sources[0].Id); await service.ScanAsync(); Assert.Single(service.Candidates);
        await Task.Delay(3100); await service.ScanAsync(); Assert.Equal(2, service.Candidates.Length);
        await service.ConfigureAsync(service.Sources[0].Id, false); lab.Game("games/third"); service.Changed(service.Sources[0].Id); await service.ScanAsync(); Assert.Empty(service.Candidates);
        await service.ScanAsync(true, watchedOnly: true); Assert.Empty(service.Candidates);
        await service.ScanAsync(true); Assert.Equal(3, service.Candidates.Length);
    }
    [Fact] public async Task ImportedCandidatesDisappearBeforeTheNextFilesystemScan()
    {
        using var lab = new Lab(); var root = lab.Folder("games"); var game = lab.Game("games/new");
        var disk = new SessionMemoryDisk { Main = new NativeLibraryCodec().Encode([], Clock) };
        await using var client = new OfflineLibraryClient(new Factory(disk), new LegacyClientTextRules()); await client.LoadAsync(default); var a = Adapters();
        await using var service = new LibrarySourcesService(new Store(), a.Fs, new WindowsScanFilesystem(), a.Scanner, a.Steam, client);
        await service.AddAsync(root, LibrarySourceKind.Folder); await service.ScanAsync(true); Assert.Single(service.Candidates);
        await client.Required().ImportAsync(new NativeLibraryCodec().Encode([Entry(folder: game)], Clock));
        Assert.Empty(service.Candidates); Assert.Single(service.Scans[0].Candidates);
    }
    [Fact] public async Task SteamReadsInstalledManifestsAndNeverFollowsUnselectedLibraries()
    {
        using var lab = new Lab(); var root = lab.Folder("steam"); var apps = lab.Folder("steam/steamapps"); lab.Game("steam/steamapps/common/selected"); lab.Game("steam/steamapps/common/not-installed"); lab.Game("elsewhere/private");
        File.WriteAllText(Path.Combine(apps, "appmanifest_480.acf"), "\"AppState\" { \"appid\" \"480\" \"name\" \"Synthetic VN\" \"StateFlags\" \"4\" \"installdir\" \"selected\" }");
        File.WriteAllText(Path.Combine(apps, "appmanifest_481.acf"), "\"AppState\" { \"appid\" \"481\" \"StateFlags\" \"2\" \"installdir\" \"not-installed\" }");
        File.WriteAllText(Path.Combine(apps, "appmanifest_482.acf"), "\"AppState\" { \"appid\" \"482\" \"StateFlags\" \"4\" \"installdir\" \"..\\..\\elsewhere\" }");
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "malformed/unreadable account-adjacent path; not a scan input");
        var a = Adapters(); var result = await a.Steam.ScanAsync(a.Fs.Inspect(root).Observation!, [], default); var candidate = Assert.Single(result.Candidates);
        Assert.Equal("480", candidate.SteamAppId); Assert.Equal("Synthetic VN", candidate.Name); Assert.Contains("selected", candidate.Folder.StoredPath); Assert.NotEmpty(result.Warnings);
        var repeated = await a.Steam.ScanAsync(a.Fs.Inspect(root).Observation!, TransactionalLibrary.Existing([Entry(folder: candidate.Folder.StoredPath)]), default); Assert.DoesNotContain(repeated.Candidates, c => c.Preflight.CanAttach);
    }
    [Theory][InlineData("\"a\" { \"x\" \"1\" \"X\" \"2\" }")][InlineData("\"a\" {")][InlineData("\"a\" \"unterminated")][InlineData("}")]
    public void MalformedSteamInputIsRejected(string text) => Assert.Throws<InvalidDataException>(() => ValveKeyValues.Parse(text));
    [Fact] public void VdfCommentsAndEscapesAreBounded()
    { var parsed = ValveKeyValues.Parse("// comment\n \"AppState\" { \"name\" \"A \\\"quote\\\"\" }"); Assert.Equal("A \"quote\"", parsed["AppState"].String("name")); Assert.Throws<InvalidDataException>(() => ValveKeyValues.Parse(new string('x', 1048577))); }
    [Fact] public async Task SourceStoreIsProfileBoundAndCorruptionDoesNotGetOverwritten()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var games = Path.Combine(fixture.Parent, "games"); Directory.CreateDirectory(games);
        var store = new WindowsLibrarySourcesStore(profile.Configuration(false)); var source = new LibrarySource(Guid.NewGuid().ToString("D"), LibrarySourceKind.Folder, new WindowsFilesystemIdentity().Inspect(games).Observation!);
        await store.SaveAsync([source], default); Assert.Equal(source, Assert.Single(await store.LoadAsync(default)));
        var path = Path.Combine(fixture.Root, "native-library-sources-v1.json"); var text = JsonNode.Parse(File.ReadAllText(path))!; text["Identity"] = "another profile"; File.WriteAllText(path, text.ToJsonString()); var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([], default)); Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact] public async Task SaveDiscoveryAndGuideValidationNeverReadOrExecuteGames()
    {
        using var lab = new Lab(); var root = lab.Game("game"); var save = lab.Folder("game/savedata"); lab.Folder("game/data/save"); lab.Folder("game/data/deeper/save");
        var service = new WindowsLocalResources(new WindowsFilesystemIdentity()); var paths = await service.FindSaveFoldersAsync(root); Assert.Contains(paths, p => new WindowsFilesystemIdentity().Inspect(p).Observation!.Identity == new WindowsFilesystemIdentity().Inspect(save).Observation!.Identity); Assert.Equal(2, paths.Length);
        var guide = Path.Combine(lab.Root, "guide.pdf"); File.WriteAllText(guide, "synthetic"); await service.ValidateGuideAsync(guide, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateGuideAsync(Path.Combine(root, "game.exe"), default));
    }
}
