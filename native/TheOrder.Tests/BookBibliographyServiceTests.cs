using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class BookBibliographyServiceTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    public static BookPublication Publication(string id = "000008005812") => new(id, "Clannad : 光見守る坂道で", "Clannad", null,
        ["9784840232500"], [], ["Key 文&原作", "ごとP イラスト"], ["メディアワークス"], [], "2005.11", null, ["jpn"], false, Stamp);
    private static BookSource Source() => new(new("Bangumi", "48", BookEntityLevel.Publication), "CLANNAD", "CLANNAD", "小说", ["Key"], ["9784840232500"], null);
    private sealed class Store : IBookBibliographyCache, IBookPublicationBindings
    {
        public Dictionary<string, BookCacheValue> Cached = new(); public Dictionary<string, BookPublicationBinding[]> Choices = new(); public bool Refuse;
        public Task<BookCacheValue?> ReadAsync(string key, CancellationToken c) { c.ThrowIfCancellationRequested(); return Task.FromResult(Cached.GetValueOrDefault(key)); }
        public Task WriteAsync(BookCacheValue v, CancellationToken c) { c.ThrowIfCancellationRequested(); Cached[v.Key] = v; return Task.CompletedTask; }
        public Task<BookPublicationBinding[]> ReadAsync(BookIdentity key, CancellationToken c) { c.ThrowIfCancellationRequested(); return Task.FromResult(Choices.GetValueOrDefault(key.Key) ?? []); }
        public Task WriteAsync(BookIdentity key, BookPublicationBinding[] values, CancellationToken c) { c.ThrowIfCancellationRequested(); if (Refuse) throw new IOException(); Choices[key.Key] = values; return Task.CompletedTask; }
    }
    private sealed class Provider : IBookBibliographyProvider
    {
        public int Fetches, Searches; public BookFailure? Failure; public Func<CancellationToken, Task<BookPublication>>? Fetch;
        public Task<BookPublication> FetchAsync(string id, CancellationToken c) { Fetches++; if (Failure is { } f) throw new BookProviderException(f, "fixture"); return Fetch?.Invoke(c) ?? Task.FromResult(Publication(id)); }
        public Task<BookSearchPage> SearchAsync(BookSearchQuery q, CancellationToken c) { Searches++; return Task.FromResult(new BookSearchPage([Publication()], 1, null, Stamp)); }
    }
    [Theory] [InlineData("4-8402-3250-4", "9784840232500")] [InlineData("4044292019", "9784044292010")] [InlineData("9784840232501", null)] [InlineData("http://example.org/ISBN", null)]
    public void IsbnUsesCheckDigitsAndDoesNotCreateWorkIdentity(string input, string? expected) => Assert.Equal(expected, BookIsbn.Normalize(input));
    [Fact] public void NdlIdsKeepNineOrTwelveDigitsAndNeverPadThem()
    { Assert.True(BookValidation.NdlId("025691306")); Assert.True(BookValidation.NdlId("000008005812")); Assert.False(BookValidation.NdlId("8152373")); Assert.NotEqual(new BookIdentity("NDL", "025691306", BookEntityLevel.Publication).Key, new BookIdentity("NDL", "000025691306", BookEntityLevel.Publication).Key); }
    [Fact] public async Task SearchingNeverCreatesBindingAndConfirmedChoiceSurvivesServiceRestart()
    {
        var store = new Store(); var provider = new Provider(); var source = Source(); var service = new BookBibliographyService(provider, store, store, () => Stamp);
        await service.SearchAsync(new("CLANNAD"), false, default); Assert.Empty(await service.BindingsAsync(source, default));
        await service.ConfirmAsync(source, Publication(), default); Assert.Equal(1, provider.Fetches);
        Assert.Single(await new BookBibliographyService(provider, store, store).BindingsAsync(source, default));
        Assert.Empty(await service.BindingsAsync(source with { OriginalTitle = "different volume" }, default));
        await service.RemoveAsync(source, Publication().Id, default); Assert.Empty(await service.BindingsAsync(source, default));
    }
    [Fact] public async Task IsbnConflictAndAggregateCannotBeConfirmedAndSameTitleDoesNotOverrideThem()
    {
        var service = new BookBibliographyService(new Provider(), choices: new Store());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(Source() with { Isbns = ["9784044292010"] }, Publication(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(Source(), Publication() with { IsAggregate = true }, default));
    }
    [Theory] [InlineData(BookFailure.Timeout)] [InlineData(BookFailure.Unavailable)]
    public async Task SearchFactsCannotReplaceAnIndependentRecordWhenSaving(BookFailure failure)
    {
        var store = new Store(); var provider = new Provider(); var service = new BookBibliographyService(provider, store, store, () => Stamp);
        var search = await service.SearchAsync(new("CLANNAD"), false, default); provider.Failure = failure;
        await Assert.ThrowsAsync<BookProviderException>(() => service.ConfirmAsync(Source(), search.Page.Publications[0], default));
        Assert.Equal(1, provider.Fetches); Assert.Empty(await service.BindingsAsync(Source(), default));
        Assert.True((await service.SearchAsync(new("CLANNAD"), false, default)).FromCache);
    }
    [Fact] public async Task ChangedIndependentRecordCannotSilentlyReplaceTheCandidate()
    {
        var store = new Store(); var provider = new Provider { Fetch = _ => Task.FromResult(Publication() with { Title = "另一版次" }) };
        var service = new BookBibliographyService(provider, store, store, () => Stamp);
        var search = await service.SearchAsync(new("CLANNAD"), false, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(Source(), search.Page.Publications[0], default));
        Assert.Empty(await service.BindingsAsync(Source(), default)); Assert.Equal(1, provider.Fetches);
    }
    [Fact] public async Task UnknownEditionAndSameIsbnRecordsDoNotMergeByIsbn()
    {
        var provider = new Provider(); var store = new Store(); var source = Source() with { Identity = new("Bangumi", "149014", BookEntityLevel.Series), Isbns = [] };
        var service = new BookBibliographyService(provider, store, store, () => Stamp);
        await service.ConfirmAsync(source, Publication(), default); await service.ConfirmAsync(source, Publication("000008005813"), default);
        Assert.Equal(2, (await service.BindingsAsync(source, default)).Length);
    }
    [Fact] public async Task PersistenceFailureCannotClaimConfirmedChoice()
    {
        var store = new Store { Refuse = true }; var service = new BookBibliographyService(new Provider(), store, store, () => Stamp);
        await Assert.ThrowsAsync<IOException>(() => service.ConfirmAsync(Source(), Publication(), default)); Assert.Empty(await service.BindingsAsync(Source(), default));
    }
    [Fact] public async Task CacheAndStaleOfflineFallbackButInvalidOrUnavailableDoesNotReviveOldData()
    {
        var now = Stamp; var provider = new Provider(); var store = new Store(); var service = new BookBibliographyService(provider, store, store, () => now);
        Assert.False((await service.LoadAsync(Publication().Id, false, default)).FromCache); Assert.True((await service.LoadAsync(Publication().Id, false, default)).FromCache);
        now += TimeSpan.FromDays(8); provider.Failure = BookFailure.Offline; Assert.True((await service.LoadAsync(Publication().Id, false, default)).IsStale);
        provider.Failure = BookFailure.InvalidResponse; await Assert.ThrowsAsync<BookProviderException>(() => service.LoadAsync(Publication().Id, true, default));
        provider.Failure = BookFailure.Unavailable; await Assert.ThrowsAsync<BookProviderException>(() => service.LoadAsync(Publication().Id, true, default));
        now += TimeSpan.FromDays(40); provider.Failure = BookFailure.Offline; await Assert.ThrowsAsync<BookProviderException>(() => service.LoadAsync(Publication().Id, false, default));
    }
    [Fact] public async Task CancellationDoesNotPublishLateCacheOrChoice()
    {
        var done = new TaskCompletionSource<BookPublication>(TaskCreationOptions.RunContinuationsAsynchronously); var provider = new Provider { Fetch = _ => done.Task }; var store = new Store(); var c = new CancellationTokenSource();
        var pending = new BookBibliographyService(provider, store, store).LoadAsync(Publication().Id, false, c.Token); c.Cancel(); done.SetResult(Publication());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Empty(store.Cached); Assert.Empty(store.Choices);
    }
    [Fact] public async Task RejectedFreshRecordDoesNotReturnFromDiskAfterRestart()
    {
        var store = new Store(); var provider = new Provider(); var service = new BookBibliographyService(provider, store, store, () => Stamp);
        await service.LoadAsync(Publication().Id, false, default); provider.Failure = BookFailure.Unavailable;
        await Assert.ThrowsAsync<BookProviderException>(() => service.LoadAsync(Publication().Id, true, default));
        provider.Failure = BookFailure.Offline;
        await Assert.ThrowsAsync<BookProviderException>(() => new BookBibliographyService(provider, store, store, () => Stamp).LoadAsync(Publication().Id, false, default));
    }
    [Fact] public void BookInfoboxKeepsSeriesAndPublicationFactsWithoutInventingAuthorRoles()
    {
        using var json = JsonDocument.Parse("""{"series":false,"volumes":0,"eps":0,"infobox":[{"key":"ISBN","value":"9784840232500"},{"key":"插图","value":"ごとP"},{"key":"作者","value":[{"v":"Key"}]},{"key":"册数","value":[{"k":"Web版","v":"24卷"},{"k":"书籍版","v":"26卷"}]}]}""");
        var f = BangumiBookFactsMapping.Map(json.RootElement); Assert.Equal(BookEntityLevel.Publication, f.Level); Assert.Equal("ごとP", Assert.Single(f.Illustrators));
        Assert.Equal("Web版 · 24卷", f.RawFacts.Single(x => x.Name == "册数").Values[0]); Assert.True(f.IsValid);
        Assert.Equal(BookEntityLevel.Series, (f with { IsSeries = true }).Level);
    }
    [Fact] public void AnimePremiereHeuristicDoesNotBindBooksAndSingleVolumeCannotBindMultiVolume()
    {
        var source = new AniListSource("149015", AniListMediaKind.Manga, "CLANNAD", "CLANNAD", "2005-11-07", "漫画", null, []) { BookLevel = BookEntityLevel.Publication };
        var m = new AniListMedia(32598, null, AniListMediaKind.Manga, "CLANNAD", "CLANNAD", "", "MANGA", "FINISHED", null, "2005-11-07", null, null, 30, 8, []);
        Assert.False(AniListMatching.StrongEvidence(source, m)); Assert.True(AniListMatching.Assess(source, m).Conflict);
    }
    [Fact] public void NullIsbnIsNeverIdentityEvidenceOrValidCachedMetadata()
    { Assert.False(BookValidation.Isbns([null!])); Assert.False(BookValidation.Publication(Publication() with { Isbns = [null!] })); Assert.False((Source() with { Isbns = [null!] }).IsValid); }
    [Theory] [InlineData("null")] [InlineData("42")]
    public void MalformedBookInfoboxIsControlledFailure(string bad)
    { using var json = JsonDocument.Parse("{\"infobox\":[" + bad + "]}"); var e = Assert.Throws<BangumiRelationsException>(() => BangumiBookFactsMapping.Map(json.RootElement)); Assert.Equal(BangumiRelationsFailure.InvalidResponse, e.Failure); }
}
