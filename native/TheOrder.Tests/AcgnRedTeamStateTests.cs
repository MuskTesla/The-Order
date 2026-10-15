using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

// Red-team regressions use synthetic data, a dedicated temporary Test profile,
// and deterministic barriers. No production profile, network, or shared fixtures.
public sealed class AcgnRedTeamStateTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private const string ReleaseA = "11111111-1111-1111-1111-111111111111";
    private const string ReleaseB = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AniListLateBindingReadCannotUndoCompletedManualConfirmation(bool evictNewChoice)
    {
        var source = Source();
        var disk = new DelayedAniListCache(new("binding:" + source.Fingerprint, Stamp,
            Binding: new(source.Fingerprint, 101, source.Kind, "old synthetic choice")));
        var service = new AniListService(new AniProvider(), disk, () => Stamp);
        var oldRead = service.MatchAsync(source, null, false, default);
        await disk.Started.Task;
        await service.ConfirmAsync(source, Details(102), default);
        Assert.Equal(102, disk.Value.Binding!.MediaId);
        if (evictNewChoice)
            for (var id = 1000; id < 1065; id++) await service.LoadAsync(id, AniListMediaKind.Anime, false, 1, default);
        disk.Resume.SetResult();
        await oldRead;
        var current = await service.MatchAsync(source, null, false, default);
        Assert.Equal(102, current.Binding!.MediaId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MusicLateBindingReadCannotUndoCompletedManualConfirmation(bool evictNewChoice)
    {
        var source = MusicSource();
        var disk = new DelayedMusicCache(new("binding:" + source.Fingerprint, Stamp,
            Binding: new(source.Fingerprint, ReleaseA)));
        var service = new MusicBrainzService(new MusicProvider(), disk, () => Stamp);
        var oldRead = service.MatchAsync(source, false, default);
        await disk.Started.Task;
        await service.ConfirmAsync(source, Album(ReleaseB), default);
        Assert.Equal(ReleaseB, disk.Value.Binding!.ReleaseId);
        if (evictNewChoice)
            for (var id = 1000; id < 1065; id++) await service.LoadAlbumAsync(id.ToString("x8") + "-1111-1111-1111-111111111111", false, default);
        disk.Resume.SetResult();
        await oldRead;
        Assert.Equal(ReleaseB, (await service.MatchAsync(source, false, default)).ConfirmedReleaseId);
    }

    [Fact]
    public async Task AniListCancellationAfterRealAtomicReplaceKeepsMemoryAndDiskConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-acgn-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            using var cancel = new CancellationTokenSource();
            var cancelAfterReplace = false;
            var disk = new IsolatedAniListCache(profile.Configuration(), stage =>
            {
                if (cancelAfterReplace && stage == PersistenceStage.AfterReplace) cancel.Cancel();
                return Task.CompletedTask;
            });
            var source = Source();
            var service = new AniListService(new AniProvider(), disk, () => Stamp);
            await service.ConfirmAsync(source, Details(101), default);
            cancelAfterReplace = true;
            var error = await Record.ExceptionAsync(() => service.ConfirmAsync(source, Details(102), cancel.Token));
            var committed = await disk.ReadAsync("binding:" + source.Fingerprint, default);
            Assert.Equal(102, committed!.Binding!.MediaId);
            var sameSession = await service.MatchAsync(source, null, false, default);
            Assert.Equal(committed.Binding.MediaId, sameSession.Binding!.MediaId);
            Assert.Null(error); // An already committed operation must not be reported as rolled back.
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("the-order-acgn-state-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AniListCancellationBeforeAtomicReplacePreservesOldChoiceAndAllowsRetry()
    {
        await WithProfileAsync(async profile =>
        {
            using var cancel = new CancellationTokenSource();
            var cancelBeforeReplace = false;
            var disk = new IsolatedAniListCache(profile.Configuration(), stage =>
            {
                if (cancelBeforeReplace && stage == PersistenceStage.BeforeReplace) cancel.Cancel();
                return Task.CompletedTask;
            });
            var service = new AniListService(new AniProvider(), disk, () => Stamp);
            var source = Source();
            await service.ConfirmAsync(source, Details(101), default);
            cancelBeforeReplace = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConfirmAsync(source, Details(102), cancel.Token));
            Assert.Equal(101, (await disk.ReadAsync("binding:" + source.Fingerprint, default))!.Binding!.MediaId);
            Assert.Equal(101, (await service.MatchAsync(source, null, false, default)).Binding!.MediaId);
            cancelBeforeReplace = false;
            await service.ConfirmAsync(source, Details(102), default);
            Assert.Equal(102, (await service.MatchAsync(source, null, false, default)).Binding!.MediaId);
        });
    }

    [Theory]
    [InlineData(PersistenceStage.BeforeReplace, false)]
    [InlineData(PersistenceStage.AfterReplace, true)]
    public async Task MusicCancellationAtAtomicBoundaryKeepsTheCommittedChoice(PersistenceStage cancelAt, bool committed)
    {
        await WithProfileAsync(async profile =>
        {
            using var cancel = new CancellationTokenSource();
            var enabled = false;
            var disk = new IsolatedMusicBrainzCache(profile.Configuration(), stage =>
            {
                if (enabled && stage == cancelAt) cancel.Cancel();
                return Task.CompletedTask;
            });
            var service = new MusicBrainzService(new MusicProvider(), disk, () => Stamp);
            var source = MusicSource();
            await service.ConfirmAsync(source, Album(ReleaseA), default);
            enabled = true;
            var error = await Record.ExceptionAsync(() => service.ConfirmAsync(source, Album(ReleaseB), cancel.Token));
            var expected = committed ? ReleaseB : ReleaseA;
            Assert.Equal(expected, (await disk.ReadAsync("binding:" + source.Fingerprint, default))!.Binding!.ReleaseId);
            Assert.Equal(expected, (await service.MatchAsync(source, false, default)).ConfirmedReleaseId);
            if (committed) Assert.Null(error); else Assert.IsAssignableFrom<OperationCanceledException>(error);
        });
    }

    [Fact]
    public async Task AniListMetadataCommittedBeforeCancellationRemainsTheSessionCache()
    {
        await WithProfileAsync(async profile =>
        {
            using var cancel = new CancellationTokenSource();
            var enabled = false;
            var disk = new IsolatedAniListCache(profile.Configuration(), stage =>
            {
                if (enabled && stage == PersistenceStage.AfterReplace) cancel.Cancel();
                return Task.CompletedTask;
            });
            var provider = new AniProvider();
            var service = new AniListService(provider, disk, () => Stamp);
            await service.LoadAsync(101, AniListMediaKind.Anime, false, 1, default);
            provider.Fetch = id => Details(id) with { Description = "Updated committed description" };
            enabled = true;
            await service.LoadAsync(101, AniListMediaKind.Anime, true, 1, cancel.Token);
            Assert.Equal("Updated committed description", (await service.LoadAsync(101, AniListMediaKind.Anime, false, 1, default)).Details.Description);
            Assert.Equal("Updated committed description", (await disk.ReadAsync("media:0:101:1", default))!.Details!.Description);
        });
    }

    [Fact]
    public async Task BookPaginationCanReachLastTenOfTheHundredCandidateLimit()
    {
        var books = new PagedBooks();
        using var vm = new BookDetailsViewModel(new UnusedSubjectReader(), books, new EmptyBookRelations());
        vm.SetSource(new("10", BangumiRelationKind.Book, "Synthetic book", "Synthetic book", null,
            "Synthetic summary", "2020-01-01", "漫画", null, null, null, Stamp), null, true);
        await vm.ExpandAsync();
        await vm.SearchAsync();
        for (var page = 2; page <= 10; page++) await vm.NextAsync();
        Assert.Equal(91, vm.Query!.Start);
        Assert.Equal(100, vm.Candidates.Length);
        Assert.Equal(10, books.Searches);
        await vm.NextAsync();
        Assert.Equal(10, books.Searches);
    }

    [Fact]
    public async Task AniListRefreshMustRevalidateThePreviouslyConfirmedBridge()
    {
        var provider = new AniProvider();
        var service = new AniListService(provider, clock: () => Stamp);
        await service.ConfirmAsync(Source(), Details(101), default);
        using var vm = new AniListDetailsViewModel(new AnimeSubjectReader(), service);
        await vm.OpenAsync(new("10", BangumiRelationKind.Anime, "Synthetic anime", "Synthetic anime", "动画", null));
        Assert.Equal(AniListDetailState.Ready, vm.State);
        provider.Fetch = id => Details(id) with { Media = Details(id).Media with { Format = "MOVIE" } };
        await vm.RefreshAsync();
        Assert.False(vm.Displayed is { } displayed && AniListMatching.Assess(vm.Source!, displayed.Details.Media).Conflict
            && vm.State == AniListDetailState.Ready && !vm.HasUnconfirmedPreview);
        Assert.False(vm.HasTrustedBridge);
        Assert.False(vm.CanConfirm);
        Assert.Equal(101, (await service.MatchAsync(Source(), null, false, default)).Binding!.MediaId);
    }

    [Fact]
    public async Task AniListUnavailablePreviewCannotBecomeATrustedBridgeByLosingDisplayedDetails()
    {
        var provider = new AniProvider
        {
            // Same year permits inspection but a different day prevents auto binding.
            SearchValues = [Details(101).Media with { StartDate = "2020-02-01" }]
        };
        using var vm = new AniListDetailsViewModel(new AnimeSubjectReader(), new AniListService(provider, clock: () => Stamp));
        await vm.OpenAsync(new("10", BangumiRelationKind.Anime, "Synthetic anime", "Synthetic anime", "动画", null));
        await vm.PreviewAsync(101);
        Assert.True(vm.HasUnconfirmedPreview);
        provider.Fetch = _ => throw new AniListException(AniListFailure.Unavailable, "Synthetic removal");
        await vm.RefreshAsync();
        Assert.Equal(AniListDetailState.Failed, vm.State);
        // AniListDetailView uses IsBridge && !HasUnconfirmedPreview to join this
        // remaining AniList identity with Bangumi in watch/publication/episode UI.
        Assert.True(vm.Media is null || vm.HasUnconfirmedPreview);
        Assert.False(vm.HasTrustedBridge);
    }

    [Fact]
    public async Task AniListTrustRequiresConfirmationAndSurvivesACompatibleRefresh()
    {
        var provider = new AniProvider { SearchValues = [Details(101).Media with { StartDate = "2020-02-01" }] };
        using var vm = new AniListDetailsViewModel(new AnimeSubjectReader(), new AniListService(provider, clock: () => Stamp));
        await vm.OpenAsync(new("10", BangumiRelationKind.Anime, "Synthetic anime", "Synthetic anime", "动画", null));
        Assert.False(vm.HasTrustedBridge);
        await vm.PreviewAsync(101);
        await vm.RefreshAsync();
        Assert.True(vm.HasUnconfirmedPreview);
        Assert.False(vm.HasTrustedBridge);
        await vm.ConfirmAsync();
        Assert.True(vm.HasTrustedBridge);
        Assert.False(vm.HasUnconfirmedPreview);
        await vm.RefreshAsync();
        Assert.True(vm.HasTrustedBridge);
        Assert.Equal(AniListDetailState.Ready, vm.State);
        provider.Fetch = _ => throw new AniListException(AniListFailure.Unavailable, "Synthetic removal");
        await vm.RefreshAsync();
        Assert.False(vm.HasTrustedBridge);
        Assert.Null(vm.Media);
    }

    [Fact]
    public async Task AniListCancelledPreviewRefreshNeverGainsBridgeTrust()
    {
        var provider = new AniProvider { SearchValues = [Details(101).Media with { StartDate = "2020-02-01" }] };
        using var vm = new AniListDetailsViewModel(new AnimeSubjectReader(), new AniListService(provider, clock: () => Stamp));
        await vm.OpenAsync(new("10", BangumiRelationKind.Anime, "Synthetic anime", "Synthetic anime", "动画", null));
        await vm.PreviewAsync(101);
        var late = new TaskCompletionSource<AniListDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AsyncFetch = (_, _) => late.Task;
        var pending = vm.RefreshAsync();
        Assert.True(vm.HasUnconfirmedPreview);
        Assert.False(vm.HasTrustedBridge);
        vm.Cancel();
        late.SetResult(Details(101));
        await pending;
        Assert.True(vm.HasUnconfirmedPreview);
        Assert.False(vm.HasTrustedBridge);
        Assert.Equal(AniListDetailState.Preview, vm.State);
    }

    [Fact]
    public async Task AniListRelatedNavigationDoesNotBorrowRootBridgeAndRestoresItsTrustOnReturn()
    {
        var provider = new AniProvider { Fetch = id => Details(id) with { Relations = id == 101 ? [new(Details(102).Media, "SEQUEL")] : [] } };
        var service = new AniListService(provider, clock: () => Stamp);
        await service.ConfirmAsync(Source(), Details(101), default);
        using var vm = new AniListDetailsViewModel(new AnimeSubjectReader(), service);
        await vm.OpenAsync(new("10", BangumiRelationKind.Anime, "Synthetic anime", "Synthetic anime", "动画", null));
        Assert.True(vm.HasTrustedBridge);
        await vm.ExploreAsync(vm.Displayed!.Details.Relations[0]);
        Assert.False(vm.HasTrustedBridge);
        Assert.False(vm.IsBridge);
        Assert.True(vm.GoBack());
        Assert.True(vm.HasTrustedBridge);
        Assert.False(vm.HasUnconfirmedPreview);
    }

    private static async Task WithProfileAsync(Func<NativeProfileStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-acgn-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            await action(profile);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("the-order-acgn-state-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    private static AniListSource Source() => new("10", AniListMediaKind.Anime, "Synthetic anime", "Synthetic anime", "2020-01-01", "TV", 12, []);
    private static AniListDetails Details(int id) => new(new(id, null, AniListMediaKind.Anime, "Synthetic anime", "Synthetic anime", "",
        "TV", "FINISHED", null, "2020-01-01", null, 12, null, null, []), "Synthetic description", [], [], [], [], [], [], 1, false, false, Stamp)
        { HasPlaybackMetadata = true };
    private static MusicAlbumSource MusicSource() => new("10", "Synthetic album", "2020-01-01", BangumiMusicFacts.Empty);
    private static MusicBrainzAlbum Album(string id) => new(new(id, null, "Synthetic album", "Synthetic artist", "2020-01-01", null,
        "Official", null, [], 0, 0), null, null, [], [], [], null, Stamp);
    private static BookPublication Publication(int n) => new(n.ToString("D9"), "Synthetic book " + n, "Synthetic book", n.ToString(),
        [], [], [], [], [], "2020", null, [], false, Stamp);

    private sealed class AniProvider : IAniListProvider
    {
        public AniListMedia[] SearchValues = [];
        public Func<int, AniListDetails> Fetch = Details;
        public Func<int, CancellationToken, Task<AniListDetails>>? AsyncFetch;
        public Task<AniListMedia[]> SearchAsync(AniListSource source, string? query, CancellationToken cancellation) => Task.FromResult(SearchValues);
        public Task<AniListMedia> ResolveAsync(AniListReference reference, CancellationToken cancellation) => Task.FromResult(Details(reference.Id).Media);
        public Task<AniListDetails> FetchAsync(int id, AniListMediaKind kind, int peoplePage, CancellationToken cancellation) => AsyncFetch?.Invoke(id, cancellation) ?? Task.FromResult(Fetch(id));
    }
    private sealed class MusicProvider : IMusicBrainzProvider
    {
        public Task<MusicBrainzSearchResult> SearchAsync(MusicAlbumSource source, CancellationToken cancellation) => Task.FromResult(new MusicBrainzSearchResult([], 0, false, null, null));
        public Task<MusicBrainzSearchResult> EditionsAsync(string releaseGroupId, CancellationToken cancellation) => Task.FromResult(new MusicBrainzSearchResult([], 0, false, null, null));
        public Task<MusicBrainzAlbum> FetchAlbumAsync(string releaseId, CancellationToken cancellation) => Task.FromResult(Album(releaseId));
    }
    private sealed class DelayedAniListCache(AniListCacheValue value) : IAniListCache
    {
        private readonly Dictionary<string, AniListCacheValue> values = new() { [value.Key] = value };
        private readonly string originalKey = value.Key;
        public AniListCacheValue Value => values[originalKey];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public async Task<AniListCacheValue?> ReadAsync(string key, CancellationToken cancellation)
        {
            var snapshot = values.GetValueOrDefault(key);
            if (Interlocked.Increment(ref reads) == 1) { Started.SetResult(); await Resume.Task; }
            return snapshot;
        }
        public Task WriteAsync(AniListCacheValue incoming, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); values[incoming.Key] = incoming; return Task.CompletedTask; }
    }
    private sealed class DelayedMusicCache(MusicBrainzCacheValue value) : IMusicBrainzCache
    {
        private readonly Dictionary<string, MusicBrainzCacheValue> values = new() { [value.Key] = value };
        private readonly string originalKey = value.Key;
        public MusicBrainzCacheValue Value => values[originalKey];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public async Task<MusicBrainzCacheValue?> ReadAsync(string key, CancellationToken cancellation)
        {
            var snapshot = values.GetValueOrDefault(key);
            if (Interlocked.Increment(ref reads) == 1) { Started.SetResult(); await Resume.Task; }
            return snapshot;
        }
        public Task WriteAsync(MusicBrainzCacheValue incoming, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); values[incoming.Key] = incoming; return Task.CompletedTask; }
    }
    private sealed class PagedBooks : IBookBibliographyReader
    {
        public int Searches;
        public Task<BookSearchResult> SearchAsync(BookSearchQuery query, bool refresh, CancellationToken cancellation)
        {
            Searches++;
            return Task.FromResult(new BookSearchResult(new(Enumerable.Range(query.Start, 10).Select(Publication).ToArray(), 100,
                query.Start < 91 ? query.Start + 10 : null, Stamp), false, false, null));
        }
        public Task<BookPublicationBinding[]> BindingsAsync(BookSource source, CancellationToken cancellation) => Task.FromResult(Array.Empty<BookPublicationBinding>());
        public Task<BookPublicationResult> LoadAsync(string id, bool refresh, CancellationToken cancellation) => throw new NotSupportedException();
        public Task ConfirmAsync(BookSource source, BookPublication publication, CancellationToken cancellation) => throw new NotSupportedException();
        public Task RemoveAsync(BookSource source, string publicationId, CancellationToken cancellation) => throw new NotSupportedException();
    }
    private sealed class UnusedSubjectReader : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind, bool forceRefresh, CancellationToken cancellation) => throw new NotSupportedException();
    }
    private sealed class AnimeSubjectReader : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind, bool forceRefresh, CancellationToken cancellation)
            => Task.FromResult(new BangumiSubjectDetailsResult(new(subjectId, expectedKind, "Synthetic anime", "Synthetic anime", null,
                "Synthetic summary", "2020-01-01", "TV", 12, null, null, Stamp), false, false, null));
    }
    private sealed class EmptyBookRelations : IBangumiBookRelationsReader
    {
        public Task<BangumiRelationsLoadResult> LoadAsync(string id, bool refresh, CancellationToken cancellation)
            => Task.FromResult(new BangumiRelationsLoadResult(new(id, Stamp, ImmutableArray<BangumiRelationItem>.Empty), false, false, null));
    }
}
