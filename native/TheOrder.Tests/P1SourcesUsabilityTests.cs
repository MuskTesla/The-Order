using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class P1SourcesUsabilityTests
{
    private sealed class Store : ILibrarySourcesStore
    {
        public ImmutableArray<LibrarySource> Values = []; public bool Fail;
        public Task<ImmutableArray<LibrarySource>> LoadAsync(CancellationToken cancellation) => Task.FromResult(Values);
        public Task SaveAsync(ImmutableArray<LibrarySource> sources, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (Fail) throw new IOException("synthetic failure"); Values = sources; return Task.CompletedTask; }
    }
    private sealed class Factory(SessionMemoryDisk disk) : ITransactionalLibraryFactory
    { public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.FromResult(TransactionalLibrary.Open(new NativeLibraryCodec(), new SessionMemoryPersistence(disk))); }
    private sealed class Lab : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "the-order-source-usability-" + Guid.NewGuid().ToString("N"));
        public Lab() => Directory.CreateDirectory(Root);
        public string Folder(string relative) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(path); return path; }
        public string Game(string relative) { var path = Folder(relative); File.WriteAllText(Path.Combine(path, "game.exe"), "Owned synthetic bytes; never execute."); return path; }
        public string Steam(string id, string folder)
        {
            var game = Game("steam/steamapps/common/" + folder); var apps = Path.Combine(Root, "steam/steamapps");
            File.WriteAllText(Path.Combine(apps, "appmanifest_" + id + ".acf"), "\"AppState\" { \"appid\" \"" + id + "\" \"name\" \"Same title\" \"StateFlags\" \"4\" \"installdir\" \"" + folder + "\" }"); return game;
        }
        public void Dispose() { if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "the-order-source-usability-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(Root, true); }
    }
    private static TitleIndex Index()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
            gzip.Write(Encoding.UTF8.GetBytes("{\"version\":1,\"records\":[{\"names\":[\"Same title\"],\"references\":[\"steam:480\",\"vndb:v5\"]}]}"));
        var data = bytes.ToArray(); return new(() => new MemoryStream(data, false));
    }
    private static OfflineLibraryClient Client() => new(new Factory(new() { Main = new NativeLibraryCodec().Encode([], Clock) }), new LegacyClientTextRules());
    private static LibrarySourcesService Service(ILibrarySourcesStore store, OfflineLibraryClient client, ISteamLibraryScanner? steam = null)
    {
        var fs = new WindowsFilesystemIdentity(); var scan = new BoundedScanner(fs, new WindowsScanFilesystem());
        return new(store, fs, new WindowsScanFilesystem(), scan, steam ?? new WindowsSteamLibraryScanner(fs, scan, Index()), client);
    }
    private sealed class BlockFirstSteamScan : ISteamLibraryScanner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public async Task<ScanResult> ScanAsync(PathObservation root, ImmutableArray<ExistingInstallation> existing, CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref calls) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellation); }
            return new(ScanStatus.NoCandidates, [], new(1, 0, 0, root.StoredPath), [], "Synthetic scan complete.");
        }
    }
    [Fact] public async Task ManualSourceScanWaitsForBackgroundAndRunsBeforeReportingCompletion()
    {
        using var lab = new Lab(); await using var client = Client(); await client.LoadAsync(default);
        var scanner = new BlockFirstSteamScan(); await using var service = Service(new Store(), client, scanner);
        await service.AddAsync(lab.Folder("steam"), LibrarySourceKind.Steam);
        var background = service.ScanAsync(true, watchedOnly: true);
        try
        {
            await scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.ScanAsync(true, watchedOnly: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, scanner.Calls); // Repeated periodic checks still skip a busy service.
            var manual = service.ScanAsync(true, waitForTurn: true);
            Assert.False(manual.IsCompleted);
            scanner.Release.TrySetResult();
            await Task.WhenAll(background, manual).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, scanner.Calls);
            Assert.Single(service.Scans);
        }
        finally { scanner.Release.TrySetResult(); await background.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact] public async Task CancelledManualSourceScanReleasesItsWaitAndAllowsTheNextScan()
    {
        using var lab = new Lab(); await using var client = Client(); await client.LoadAsync(default);
        var scanner = new BlockFirstSteamScan(); await using var service = Service(new Store(), client, scanner);
        await service.AddAsync(lab.Folder("steam"), LibrarySourceKind.Steam);
        var background = service.ScanAsync(true, watchedOnly: true);
        try
        {
            await scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var manual = service.ScanAsync(true, cancellation.Token, waitForTurn: true);
            Assert.False(manual.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, scanner.Calls);
            Assert.False(background.IsCompleted);
            scanner.Release.TrySetResult();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            await service.ScanAsync(true, waitForTurn: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, scanner.Calls);
        }
        finally { scanner.Release.TrySetResult(); await background.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact] public async Task SteamFilterUsesExactCatalogIdAndUnconfirmedOptInPersists()
    {
        using var lab = new Lab(); lab.Steam("480", "vn"); var unknown = lab.Steam("481", "ordinary");
        await using var client = Client(); await client.LoadAsync(default); var store = new Store();
        await using (var service = Service(store, client))
        {
            await service.AddAsync(lab.Folder("steam"), LibrarySourceKind.Steam); await service.ScanAsync(true);
            Assert.Equal("480", Assert.Single(service.Candidates).SteamAppId);
            Assert.True(service.Candidates[0].SteamCatalogMatch);
            Assert.False(service.Scans[0].Candidates.Single(c => c.SteamAppId == "481").SteamCatalogMatch);
            await service.ConfigureDiscoveryAsync(service.Sources[0].Id, true); Assert.Equal(2, service.Candidates.Length);
        }
        await using (var resumed = Service(store, client))
        {
            await resumed.InitializeAsync(); await resumed.ScanAsync(true); Assert.Equal(2, resumed.Candidates.Length);
            await resumed.ConfigureDiscoveryAsync(resumed.Sources[0].Id, false); Assert.Single(resumed.Candidates);
        }
        Assert.Equal("Owned synthetic bytes; never execute.", File.ReadAllText(Path.Combine(unknown, "game.exe")));
    }
    [Fact] public async Task IgnoreSelectionSurvivesRescanRestartAndCanBeRestored()
    {
        using var lab = new Lab(); lab.Game("games/first"); lab.Game("games/second");
        await using var client = Client(); await client.LoadAsync(default); var store = new Store();
        await using (var service = Service(store, client))
        {
            await service.AddAsync(lab.Folder("games"), LibrarySourceKind.Folder); await service.ScanAsync(true);
            var first = service.Candidates.Single(c => c.Name == "first"); await service.IgnoreAsync([first]);
            Assert.Equal("second", Assert.Single(service.Candidates).Name); await service.ScanAsync(true); Assert.Single(service.Candidates);
        }
        await using var resumed = Service(store, client); await resumed.InitializeAsync(); await resumed.ScanAsync(true); Assert.Single(resumed.Candidates);
        await resumed.IgnoreAsync(resumed.Candidates); Assert.Empty(resumed.Candidates); Assert.Equal(2, resumed.Sources[0].IgnoredCandidates.Length);
        await resumed.ConfigureDiscoveryAsync(resumed.Sources[0].Id, restoreIgnored: true); Assert.Equal(2, resumed.Candidates.Length);
        Assert.Empty(resumed.Sources[0].IgnoredCandidates); Assert.Empty(client.CurrentSnapshot!.Entries);
    }
    [Fact] public async Task IgnoringOverlappingSourcesPersistsAllKeysAtomicallyAndSurvivesRestart()
    {
        using var lab = new Lab(); lab.Steam("480", "vn");
        await using var client = Client(); await client.LoadAsync(default); var store = new Store();
        await using (var service = Service(store, client))
        {
            await service.AddAsync(lab.Folder("steam"), LibrarySourceKind.Steam);
            await service.AddAsync(lab.Folder("steam/steamapps/common"), LibrarySourceKind.Folder);
            await service.ScanAsync(true);
            Assert.Equal(2, service.Scans.Sum(s => s.Candidates.Length));
            Assert.Single(service.Candidates);
            store.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => service.IgnoreAsync(service.Candidates));
            Assert.All(service.Sources, s => Assert.Empty(s.IgnoredCandidates));
            Assert.All(store.Values, s => Assert.Empty(s.IgnoredCandidates));
            Assert.Single(service.Candidates);
            store.Fail = false;
            await service.IgnoreAsync(service.Candidates);
            Assert.Empty(service.Candidates);
            Assert.All(service.Sources, s => Assert.Single(s.IgnoredCandidates));
            await service.ScanAsync(true); Assert.Empty(service.Candidates);
        }
        await using var resumed = Service(store, client);
        await resumed.InitializeAsync(); await resumed.ScanAsync(true); Assert.Empty(resumed.Candidates);
        await resumed.ConfigureDiscoveryAsync(resumed.Sources[1].Id, restoreIgnored: true);
        Assert.Single(resumed.Candidates);
        Assert.Single(resumed.Sources[0].IgnoredCandidates);
        Assert.Empty(client.CurrentSnapshot!.Entries);
    }
    [Fact] public async Task IgnoringPreservesStateOnFailureCancellationOrStaleIdentity()
    {
        using var lab = new Lab(); var game = lab.Game("games/first"); await using var client = Client(); await client.LoadAsync(default); var store = new Store();
        await using var service = Service(store, client); await service.AddAsync(lab.Folder("games"), LibrarySourceKind.Folder); await service.ScanAsync(true);
        var candidate = Assert.Single(service.Candidates); store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.IgnoreAsync([candidate])); Assert.Single(service.Candidates); Assert.Empty(service.Sources[0].IgnoredCandidates);
        store.Fail = false; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IgnoreAsync([candidate], cancel.Token)); Assert.Single(service.Candidates);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.IgnoreAsync([candidate with { SteamAppId = "999" }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.IgnoreAsync([candidate, candidate]));
        Directory.Move(game, game + "-old"); lab.Game("games/first");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.IgnoreAsync([candidate])); Assert.Empty(service.Sources[0].IgnoredCandidates);
    }
    [Fact] public async Task ReplacedFolderAndNewGamesAreNotSuppressedByOldIgnore()
    {
        using var lab = new Lab(); var game = lab.Game("games/first"); await using var client = Client(); await client.LoadAsync(default);
        await using var service = Service(new Store(), client); await service.AddAsync(lab.Folder("games"), LibrarySourceKind.Folder); await service.ScanAsync(true);
        await service.IgnoreAsync(service.Candidates); Assert.Empty(service.Candidates);
        Directory.Move(game, lab.Folder("held") + "/old"); lab.Game("games/first"); lab.Game("games/new");
        await service.ScanAsync(true); Assert.Equal(2, service.Candidates.Length);
    }
    [Fact] public async Task IndexMembershipIsExactAndCancellationDoesNotLoadPartialData()
    {
        var index = Index(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.ContainsReferenceAsync("steam:480", cancel.Token)); Assert.Equal(0, index.RecordCount);
        Assert.True(await index.ContainsReferenceAsync("steam:480", default));
        Assert.False(await index.ContainsReferenceAsync("Steam:480", default)); Assert.False(await index.ContainsReferenceAsync("steam:0480", default));
        Assert.False(await index.ContainsReferenceAsync("steam:481", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => index.ContainsReferenceAsync(new string('x', 101), default));
    }
    [Fact] public async Task OldSourceRecordsLoadAndNewSettingsRoundTripWithoutChangingLibrary()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var games = Path.Combine(fixture.Parent, "games"); Directory.CreateDirectory(games);
        var store = new WindowsLibrarySourcesStore(profile.Configuration(false));
        var source = new LibrarySource(Guid.NewGuid().ToString("D"), LibrarySourceKind.Steam, new WindowsFilesystemIdentity().Inspect(games).Observation!);
        await store.SaveAsync([source], default); var path = Path.Combine(fixture.Root, "native-library-sources-v1.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!; var row = json["Sources"]![0]!.AsObject(); row.Remove("ShowUnconfirmedSteam"); row.Remove("IgnoredCandidates"); File.WriteAllText(path, json.ToJsonString());
        var old = Assert.Single(await store.LoadAsync(default)); Assert.False(old.ShowUnconfirmedSteam); Assert.Empty(old.IgnoredCandidates);
        var next = old with { ShowUnconfirmedSteam = true, IgnoredCandidates = [new string('A', 64)] }; await store.SaveAsync([next], default);
        var loaded = Assert.Single(await store.LoadAsync(default)); Assert.True(loaded.ShowUnconfirmedSteam); Assert.Equal(next.IgnoredCandidates.ToArray(), loaded.IgnoredCandidates.ToArray());
        var bytes = File.ReadAllBytes(path); await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([next with { IgnoredCandidates = ["bad"] }], default)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
