using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BookMetadataRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameIdentityBangumiRefreshUpdatesFactsWithoutLosingPublicationWork()
    {
        var books = new Books(); using var vm = Model(books);
        var original = Subject("43194"); vm.SetSource(original, null, true);
        await vm.ExpandAsync(); await vm.SearchAsync(); await vm.PreviewAsync(Books.Publication.Id); await vm.ConfirmAsync();
        var search = vm.Search; var preview = vm.Preview; var bindings = vm.Bindings; var version = vm.Version;
        var updated = original with { ReleaseDate = "2026-10-15", BookFacts = original.BookFacts! with
            { Volumes = 3, Chapters = 20, Publishers = ["新版出版社"], Magazines = ["新版杂志"], Imprints = ["新版文库"], RawFacts = [new("页数", ["210"])] } };
        Assert.Equal(BookSource.From(original).Fingerprint, BookSource.From(updated).Fingerprint);
        vm.SetSource(updated, null, true);
        Assert.Same(updated, vm.Bangumi); Assert.Equal(3, vm.Bangumi!.BookFacts!.Volumes);
        Assert.Same(search, vm.Search); Assert.Same(preview, vm.Preview); Assert.Same(bindings, vm.Bindings);
        Assert.True(vm.Expanded); Assert.True(vm.PreviewIsBound); Assert.Equal(version, vm.Version);
    }

    [Fact]
    public async Task SameIdentityAniListRefreshUpdatesCountsAndDatesWithoutResettingSearch()
    {
        using var vm = Model(new Books()); var original = Ani(); vm.SetSource(null, original, false);
        await vm.ExpandAsync(); await vm.SearchAsync(); var search = vm.Search;
        var updated = original with { Media = original.Media with { Volumes = 9, Chapters = 46, StartDate = "2005-05-07" } };
        Assert.Equal(BookSource.From(original).Fingerprint, BookSource.From(updated).Fingerprint);
        vm.SetSource(null, updated, false);
        Assert.Same(updated, vm.AniList); Assert.Equal(9, vm.AniList!.Media.Volumes);
        Assert.Same(search, vm.Search); Assert.True(vm.Expanded);
    }

    [Fact]
    public async Task RootRefreshWhileReadingRelatedVolumeKeepsNavigationAndRefreshesRestoredRoot()
    {
        using var vm = Model(new Books()); var original = Subject("43194"); vm.SetSource(original, null, true);
        await vm.ExpandAsync(); await vm.SearchAsync(); var search = vm.Search;
        await vm.LoadRelationsAsync(); await vm.OpenRelatedAsync("43195");
        var current = vm.Bangumi; var updated = original with { BookFacts = original.BookFacts! with { Volumes = 3 } };
        vm.SetSource(updated, null, true);
        Assert.Same(current, vm.Bangumi); Assert.Equal("43195", vm.Source!.Identity.Id); Assert.Equal(1, vm.HistoryDepth);
        Assert.True(vm.GoBack()); Assert.Same(updated, vm.Bangumi); Assert.Same(search, vm.Search); Assert.True(vm.Expanded);
    }

    [Fact]
    public async Task DifferentIdentityStillDiscardsPriorPublicationPreview()
    {
        using var vm = Model(new Books()); vm.SetSource(Subject("43194"), null, true); await vm.ExpandAsync();
        await vm.SearchAsync(); await vm.PreviewAsync(Books.Publication.Id);
        vm.SetSource(Subject("34549"), null, true);
        Assert.Null(vm.Preview); Assert.Null(vm.Search); Assert.Empty(vm.Bindings); Assert.False(vm.Expanded);
        Assert.Equal("34549", vm.Source!.Identity.Id);
    }

    private static BookDetailsViewModel Model(Books books) => new(new Subjects(), books, new Relations());
    private static BangumiSubjectDetails Subject(string id) => new(id, BangumiRelationKind.Book, "书籍", "書籍", null,
        "简介", "2010-05-26", "漫画", null, null, null, Now)
    { BookFacts = new(true, 2, 14, [], ["作者"], [], ["出版社"], [], [], null) };
    private static AniListDetails Ani() => new(new(32598, null, AniListMediaKind.Manga, "CLANNAD", "CLANNAD", "", "MANGA", "FINISHED", null,
        "2005-11-07", null, null, 45, 8, []), "简介", [], [], [], [], [], [], 1, false, false, Now);
    private sealed class Subjects : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string id, BangumiRelationKind kind, bool refresh, CancellationToken token)
            => Task.FromResult(new BangumiSubjectDetailsResult(Subject(id), false, false, null));
    }
    private sealed class Relations : IBangumiBookRelationsReader
    {
        public Task<BangumiRelationsLoadResult> LoadAsync(string id, bool refresh, CancellationToken token)
            => Task.FromResult(new BangumiRelationsLoadResult(new(id, Now,
                ImmutableArray.Create(new BangumiRelationItem("43195", BangumiRelationKind.Book, "书籍一", "書籍一", "单行本", null))), false, false, null));
    }
    private sealed class Books : IBookBibliographyReader
    {
        public static readonly BookPublication Publication = new("000010877344", "書籍", "書籍", "1", ["9784840232500"], [], ["作者"], ["出版社"], [], "2010.5", null, ["jpn"], false, Now);
        private BookPublicationBinding[] saved = [];
        public Task<BookSearchResult> SearchAsync(BookSearchQuery query, bool refresh, CancellationToken token)
            => Task.FromResult(new BookSearchResult(new([Publication], 1, null, Now), false, false, null));
        public Task<BookPublicationResult> LoadAsync(string id, bool refresh, CancellationToken token) => Task.FromResult(new BookPublicationResult(Publication, false, false, null));
        public Task<BookPublicationBinding[]> BindingsAsync(BookSource source, CancellationToken token) => Task.FromResult(saved.Where(x => x.SourceIdentity == source.Identity).ToArray());
        public Task ConfirmAsync(BookSource source, BookPublication publication, CancellationToken token)
        { saved = [new(source.Identity, source.Fingerprint, publication.Id, Now, "合成确认")]; return Task.CompletedTask; }
        public Task RemoveAsync(BookSource source, string id, CancellationToken token) { saved = []; return Task.CompletedTask; }
    }
}
