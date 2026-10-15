using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicCacheOwnershipQualityTests
{
    private const string Artist = "84c2a1ee-f797-4760-a0d2-b20f608911a6";
    private const string OtherArtist = "9782d6e1-9a4e-4013-b60b-3e050b03a067";

    [Fact]
    public async Task ReturnedAlbumArraysCannotMutateFreshOrOfflineCache()
    {
        var now = MusicTestData.Stamp; var provider = new Provider();
        var service = new MusicBrainzService(provider, clock: () => now);
        var first = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Mutate(first.Album); var fresh = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Assert.True(fresh.FromCache); AssertOriginal(fresh.Album); Assert.Equal(1, provider.AlbumReads);
        Mutate(fresh.Album); now = now.AddDays(2); provider.Offline = true;
        var stale = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Assert.True(stale.IsStale); AssertOriginal(stale.Album);
        Mutate(stale.Album);
        AssertOriginal((await service.LoadAlbumAsync(MusicTestData.Release, false, default)).Album);
    }

    [Fact]
    public async Task ProviderRetainedAlbumCannotMutateRememberedSnapshot()
    {
        var provider = new Provider(); var service = new MusicBrainzService(provider, clock: () => MusicTestData.Stamp);
        var first = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Mutate(provider.Album);
        AssertOriginal(first.Album);
        AssertOriginal((await service.LoadAlbumAsync(MusicTestData.Release, false, default)).Album);
        Assert.Equal(1, provider.AlbumReads);
    }

    [Fact]
    public async Task ReturnedSearchCandidatesCannotMutateFreshOrOfflineCache()
    {
        var now = MusicTestData.Stamp; var provider = new Provider();
        var service = new MusicBrainzService(provider, clock: () => now);
        var first = await service.MatchAsync(MusicTestData.Source(), false, default);
        Mutate(first.Search.Candidates[0]);
        var fresh = await service.MatchAsync(MusicTestData.Source(), false, default);
        Assert.True(fresh.FromCache); AssertOriginal(Assert.Single(fresh.Search.Candidates)); Assert.Equal(1, provider.SearchReads);
        Mutate(fresh.Search.Candidates[0]); now = now.AddHours(2); provider.Offline = true;
        var stale = await service.MatchAsync(MusicTestData.Source(), false, default);
        Assert.True(stale.IsStale); AssertOriginal(Assert.Single(stale.Search.Candidates));
        Mutate(stale.Search.Candidates[0]);
        AssertOriginal(Assert.Single((await service.MatchAsync(MusicTestData.Source(), false, default)).Search.Candidates));
    }

    [Fact]
    public async Task ProviderAndCallerEditionArraysCannotMutateRememberedSnapshot()
    {
        var provider = new Provider(); var service = new MusicBrainzService(provider, clock: () => MusicTestData.Stamp);
        var first = await service.EditionsAsync(MusicTestData.Group, default);
        Mutate(provider.Search.Candidates[0]); AssertOriginal(Assert.Single(first.Candidates));
        Mutate(first.Candidates[0]);
        AssertOriginal(Assert.Single((await service.EditionsAsync(MusicTestData.Group, default)).Candidates));
        Assert.Equal(1, provider.EditionReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskAdapterRetainedReadAndWriteValuesCannotShareConsumerOrMemoryOwnership(bool seedDisk)
    {
        var key = "release:" + MusicTestData.Release; var disk = new MusicMemoryCache(); var provider = new Provider();
        if (seedDisk) disk.Values[key] = new(key, MusicTestData.Stamp, Album: Album());
        var service = new MusicBrainzService(provider, disk, () => MusicTestData.Stamp);
        var first = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Assert.Equal(seedDisk, first.FromCache); Assert.Equal(seedDisk ? 0 : 1, provider.AlbumReads);
        Mutate(first.Album); AssertOriginal(disk.Values[key].Album!);
        var fresh = await service.LoadAlbumAsync(MusicTestData.Release, false, default);
        Mutate(disk.Values[key].Album!);
        AssertOriginal(fresh.Album);
        AssertOriginal((await service.LoadAlbumAsync(MusicTestData.Release, false, default)).Album);
    }

    private static MusicBrainzReleaseCandidate Candidate() => MusicTestData.Candidate() with
    { ArtistCredits = [new("Release artist", Artist, "")], ReleaseEvents = [new("2004-08-13", "JP")] };
    private static MusicBrainzAlbum Album() => MusicTestData.Album() with
    {
        Release = Candidate(),
        Discs = MusicTestData.Album().Discs.Select(d => d with
        { Tracks = d.Tracks.Select(t => t with { ArtistCredits = [new("Track artist", Artist, "")] }).ToArray() }).ToArray()
    };
    private static void Mutate(MusicBrainzReleaseCandidate release)
    {
        release.ArtistCredits[0] = new("Changed release artist", OtherArtist, "");
        release.Labels[0] = new("Changed label", "WRONG-0001");
        release.ReleaseEvents[0] = new("2026-01-01", "US");
    }
    private static void Mutate(MusicBrainzAlbum album)
    {
        Mutate(album.Release);
        album.Discs[0].Tracks[0].ArtistCredits[0] = new("Changed track artist", OtherArtist, "");
    }
    private static void AssertOriginal(MusicBrainzReleaseCandidate release)
    {
        Assert.Equal(Artist, Assert.Single(release.ArtistCredits).ArtistId);
        Assert.Equal("KSLA-0012", release.Labels[0].CatalogNumber);
        Assert.Equal("2004-08-13", Assert.Single(release.ReleaseEvents).Date);
    }
    private static void AssertOriginal(MusicBrainzAlbum album)
    { AssertOriginal(album.Release); Assert.Equal(Artist, Assert.Single(album.Discs[0].Tracks[0].ArtistCredits).ArtistId); }
    private sealed class Provider : IMusicBrainzProvider
    {
        public MusicBrainzAlbum Album = MusicCacheOwnershipQualityTests.Album();
        public MusicBrainzSearchResult Search = new([Candidate()], 1, false, null, null);
        public bool Offline;
        public int AlbumReads, SearchReads, EditionReads;
        public Task<MusicBrainzAlbum> FetchAlbumAsync(string id, CancellationToken token)
        { AlbumReads++; return Offline ? Task.FromException<MusicBrainzAlbum>(new MusicBrainzException(MusicBrainzFailure.Offline, "Synthetic offline")) : Task.FromResult(Album); }
        public Task<MusicBrainzSearchResult> SearchAsync(MusicAlbumSource source, CancellationToken token)
        { SearchReads++; return Offline ? Task.FromException<MusicBrainzSearchResult>(new MusicBrainzException(MusicBrainzFailure.Offline, "Synthetic offline")) : Task.FromResult(Search); }
        public Task<MusicBrainzSearchResult> EditionsAsync(string id, CancellationToken token)
        { EditionReads++; return Task.FromResult(Search); }
    }
}
