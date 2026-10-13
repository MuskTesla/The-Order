using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

internal sealed class MusicTestProvider : IMusicBrainzProvider
{
    public int Searches, Albums;
    public Func<MusicAlbumSource, CancellationToken, Task<MusicBrainzSearchResult>> Search = (_, _) => Task.FromResult(MusicTestData.Search());
    public Func<string, CancellationToken, Task<MusicBrainzAlbum>> Load = (id, _) => Task.FromResult(MusicTestData.Album(id));
    public Task<MusicBrainzSearchResult> SearchAsync(MusicAlbumSource source, CancellationToken token) { Searches++; return Search(source, token); }
    public Task<MusicBrainzAlbum> FetchAlbumAsync(string id, CancellationToken token) { Albums++; return Load(id, token); }
    public Task<MusicBrainzSearchResult> EditionsAsync(string id, CancellationToken token) => Task.FromResult(MusicTestData.Search());
}
internal sealed class MusicMemoryCache : IMusicBrainzCache
{
    public Dictionary<string, MusicBrainzCacheValue> Values = [];
    public Task<MusicBrainzCacheValue?> ReadAsync(string key, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Values.GetValueOrDefault(key)); }
    public Task WriteAsync(MusicBrainzCacheValue value, CancellationToken token) { token.ThrowIfCancellationRequested(); Values[value.Key] = value; return Task.CompletedTask; }
}
public sealed class MusicBrainzServiceTests
{
    [Fact] public async Task FreshPositiveAndNegativeSearchesAreCachedWithoutExtraProviderCalls()
    {
        var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, clock: () => MusicTestData.Stamp);
        Assert.False((await service.MatchAsync(MusicTestData.Source(), false, default)).FromCache);
        Assert.True((await service.MatchAsync(MusicTestData.Source(), false, default)).FromCache); Assert.Equal(1, provider.Searches);
        provider.Search = (_, _) => Task.FromResult(new MusicBrainzSearchResult([], 0, false, null, null));
        await service.MatchAsync(MusicTestData.Source(), true, default);
        Assert.Empty((await service.MatchAsync(MusicTestData.Source(), false, default)).Search.Candidates); Assert.Equal(2, provider.Searches);
    }
    [Fact] public async Task AlbumCacheAndConfirmedChoiceSurviveNewServiceWithoutTitleBasedBinding()
    {
        var disk = new MusicMemoryCache(); var provider = new MusicTestProvider(); var source = MusicTestData.Source();
        var service = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp);
        await service.LoadAlbumAsync(MusicTestData.Release, false, default); await service.ConfirmAsync(source, MusicTestData.Album(), default);
        var restarted = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp);
        Assert.Equal(MusicTestData.Release, (await restarted.MatchAsync(source, false, default)).ConfirmedReleaseId);
        Assert.True((await restarted.LoadAlbumAsync(MusicTestData.Release, false, default)).FromCache); Assert.Equal(1, provider.Albums); Assert.Equal(0, provider.Searches);
        Assert.Null((await restarted.MatchAsync(source with { BangumiId = "617" }, false, default)).ConfirmedReleaseId);
    }
    [Theory]
    [InlineData(MusicBrainzFailure.Offline)][InlineData(MusicBrainzFailure.Timeout)][InlineData(MusicBrainzFailure.Server)][InlineData(MusicBrainzFailure.RateLimited)]
    public async Task NetworkFailuresUseBoundedStaleMetadata(MusicBrainzFailure failure)
    {
        var now = MusicTestData.Stamp; var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, clock: () => now);
        await service.LoadAlbumAsync(MusicTestData.Release, false, default); now += TimeSpan.FromDays(2);
        provider.Load = (_, _) => throw new MusicBrainzException(failure, "合成故障");
        var value = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Assert.True(value.FromCache); Assert.True(value.IsStale); Assert.Contains("已缓存", value.Warning);
    }
    [Theory]
    [InlineData(MusicBrainzFailure.Unavailable)][InlineData(MusicBrainzFailure.InvalidResponse)][InlineData(MusicBrainzFailure.ResponseTooLarge)]
    public async Task InvalidOrUnavailableResponsesNeverFallBackToOldAlbum(MusicBrainzFailure failure)
    {
        var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, clock: () => MusicTestData.Stamp);
        await service.LoadAlbumAsync(MusicTestData.Release, false, default); provider.Load = (_, _) => throw new MusicBrainzException(failure, "合成拒绝");
        await Assert.ThrowsAsync<MusicBrainzException>(() => service.LoadAlbumAsync(MusicTestData.Release, true, default));
        await Assert.ThrowsAsync<MusicBrainzException>(() => service.LoadAlbumAsync(MusicTestData.Release, false, default));
    }
    [Fact] public async Task FutureExpiredAndForeignCacheKeysAreNotAdopted()
    {
        var disk = new MusicMemoryCache(); var key = "release:" + MusicTestData.Release;
        disk.Values[key] = new(key, MusicTestData.Stamp.AddYears(1), Album: MusicTestData.Album());
        var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp);
        Assert.False((await service.LoadAlbumAsync(MusicTestData.Release, false, default)).FromCache);
        disk.Values[key] = new(key, MusicTestData.Stamp.AddDays(-8), Album: MusicTestData.Album() with { FetchedAtUtc = MusicTestData.Stamp.AddDays(-8) });
        Assert.False((await new MusicBrainzService(provider, disk, () => MusicTestData.Stamp).LoadAlbumAsync(MusicTestData.Release, false, default)).FromCache);
        disk.Values[key] = new("release:" + MusicTestData.Other, MusicTestData.Stamp, Album: MusicTestData.Album(MusicTestData.Other));
        Assert.False((await new MusicBrainzService(provider, disk, () => MusicTestData.Stamp).LoadAlbumAsync(MusicTestData.Release, false, default)).FromCache);
    }
    [Fact] public async Task CancelledLookupCannotPopulateCacheAndWrongAlbumIsRejected()
    {
        var completion = new TaskCompletionSource<MusicBrainzAlbum>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new MusicTestProvider { Load = (_, _) => completion.Task }; var disk = new MusicMemoryCache();
        var service = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp); using var stop = new CancellationTokenSource();
        var loading = service.LoadAlbumAsync(MusicTestData.Release, false, stop.Token); stop.Cancel(); completion.SetResult(MusicTestData.Album());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading); Assert.Empty(disk.Values);
        provider.Load = (_, _) => Task.FromResult(MusicTestData.Album(MusicTestData.Other));
        await Assert.ThrowsAsync<MusicBrainzException>(() => service.LoadAlbumAsync(MusicTestData.Release, false, default));
        Assert.True(Assert.Single(disk.Values).Value.Rejected); Assert.Null(Assert.Single(disk.Values).Value.Album);
    }
    [Fact] public async Task LateSameIdResponseCannotReplaceNewerCache()
    {
        var old = new TaskCompletionSource<MusicBrainzAlbum>(TaskCreationOptions.RunContinuationsAsynchronously); int count = 0;
        var provider = new MusicTestProvider { Load = (_, _) => ++count == 1 ? old.Task : Task.FromResult(MusicTestData.Album() with { GroupTitle = "new" }) };
        var service = new MusicBrainzService(provider, clock: () => MusicTestData.Stamp);
        var first = service.LoadAlbumAsync(MusicTestData.Release, true, default); await service.LoadAlbumAsync(MusicTestData.Release, true, default);
        old.SetResult(MusicTestData.Album() with { GroupTitle = "old" }); await first;
        Assert.Equal("new", (await service.LoadAlbumAsync(MusicTestData.Release, false, default)).Album.GroupTitle);
    }
    [Fact] public async Task RemovedReleaseCannotResurrectFromDiskAfterRestart()
    {
        var disk = new MusicMemoryCache(); var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp);
        await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        provider.Load = (_, _) => throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "移除");
        await Assert.ThrowsAsync<MusicBrainzException>(() => service.LoadAlbumAsync(MusicTestData.Release, true, default));
        Assert.True(disk.Values["release:" + MusicTestData.Release].Rejected);
        await Assert.ThrowsAsync<MusicBrainzException>(() => new MusicBrainzService(provider, disk, () => MusicTestData.Stamp).LoadAlbumAsync(MusicTestData.Release, false, default));
    }
}
[Collection("Native profile resource")]
public sealed class MusicBrainzCacheTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string parent = Path.Combine(Path.GetTempPath(), "the-order-music-cache-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(parent, "profile"); public string CacheRoot => Path.Combine(Root, IsolatedMusicBrainzCache.DirectoryName);
        public NativeProfileStore Profile { get; private set; }
        public Fixture() => Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec());
        public IsolatedMusicBrainzCache Store(Func<PersistenceStage, Task>? probe = null) => new(Profile.Configuration(), probe);
        public string PathFor(string key) => Path.Combine(CacheRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
        public void Reopen() { Profile.Dispose(); Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec()); }
        public void Dispose()
        {
            Profile.Dispose(); var absolute = Path.GetFullPath(parent); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!absolute.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("the-order-music-cache-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(absolute, true);
        }
    }
    private static MusicBrainzCacheValue Value() => new("release:" + MusicTestData.Release, MusicTestData.Stamp, Album: MusicTestData.Album());
    [Fact] public async Task ProcessRestartRoundTripPreservesLibraryAndProfileBytes()
    {
        using var fixture = new Fixture(); var before = ProfileFixture.Hashes(fixture.Root);
        await fixture.Store().WriteAsync(Value(), default); fixture.Reopen(); var restored = await fixture.Store().ReadAsync(Value().Key, default);
        Assert.NotNull(restored); Assert.Equal(3, restored.Album!.Discs.Length); Assert.Equal(before, ProfileFixture.Hashes(fixture.Root));
    }
    [Theory] [InlineData("future")][InlineData("hash")][InlineData("foreign")]
    public async Task FutureDamagedAndForeignEnvelopeCannotBeReadOrOverwritten(string problem)
    {
        using var fixture = new Fixture(); var store = fixture.Store(); await store.WriteAsync(Value(), default);
        var path = fixture.PathFor(Value().Key); var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (problem == "future") json["Version"] = 2; else if (problem == "foreign") json["ProfileId"] = Guid.NewGuid().ToString(); else json["Hash"] = "bad";
        File.WriteAllText(path, json.ToJsonString()); var original = File.ReadAllBytes(path);
        Assert.Null(await store.ReadAsync(Value().Key, default)); await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Value(), default));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact] public async Task CancellationBeforeAtomicReplacePreservesPreviousChoice()
    {
        using var fixture = new Fixture(); var source = MusicTestData.Source(); var key = "binding:" + source.Fingerprint;
        await fixture.Store().WriteAsync(new(key, MusicTestData.Stamp, Binding: new(source.Fingerprint, MusicTestData.Release)), default);
        using var stop = new CancellationTokenSource();
        var store = fixture.Store(stage => { if (stage == PersistenceStage.BeforeReplace) stop.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(new(key, MusicTestData.Stamp, Binding: new(source.Fingerprint, MusicTestData.Other)), stop.Token));
        Assert.Equal(MusicTestData.Release, (await fixture.Store().ReadAsync(key, default))!.Binding!.ReleaseId);
    }
}
