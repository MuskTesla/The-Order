using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BookDetailsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private const string Isbn = "9784048677608", OtherIsbn = "9780306406157";
    [Fact]
    public async Task TrustedBangumiWorksWithoutAniListAndIsLazy()
    {
        var f = new Fixture(); using var vm = f.Model(); var bg = Subject("10", true);
        vm.SetSource(bg, null, true);
        Assert.Equal("Bangumi", vm.Source!.Identity.Provider); Assert.Equal(BookEntityLevel.Series, vm.Source.Identity.Level);
        Assert.False(vm.Expanded); Assert.Equal(0, f.Books.BindingReads); Assert.Equal(0, f.Relations.Reads); Assert.Equal(0, f.Subjects.Reads);
        await vm.ExpandAsync(); Assert.Equal(1, f.Books.BindingReads); Assert.Equal(0, f.Relations.Reads); Assert.Empty(vm.Bindings);
        await vm.LoadRelationsAsync(); Assert.Equal(1, f.Relations.Reads); Assert.NotNull(vm.Relations);
    }
    [Fact]
    public async Task InternalAniListPageNeverBorrowsStartingBangumiEvenWithSameTitle()
    {
        var f = new Fixture(); using var vm = f.Model(); var bg = Subject("10", true);
        vm.SetSource(bg, Ani(50), true); await vm.ExpandAsync(); await vm.LoadRelationsAsync();
        vm.SetSource(bg, Ani(51), false);
        Assert.Equal(new BookIdentity("AniList", "51", BookEntityLevel.Unknown), vm.Source!.Identity);
        Assert.Null(vm.Bangumi); Assert.Null(vm.Relations); Assert.Empty(vm.Bindings); Assert.False(vm.Expanded);
        await vm.ExpandAsync(); await vm.LoadRelationsAsync(); Assert.Equal(1, f.Relations.Reads);
        vm.SetSource(bg, Ani(52), false); Assert.Equal("52", vm.Source!.Identity.Id); Assert.Empty(vm.Candidates);
    }
    [Fact]
    public async Task ExplicitTankobonRelationEstablishesVolumeAndBackRestoresOriginalSeries()
    {
        var f = new Fixture(); f.Relations.Items = [Relation("11", "单行本"), Relation("12", "不同版本")];
        using var vm = f.Model(); vm.SetSource(Subject("10", true), null, true); await vm.ExpandAsync(); await vm.LoadRelationsAsync();
        await vm.SearchAsync(); var originalCandidates = vm.Candidates;
        await vm.OpenRelatedAsync("11"); Assert.Equal(BookEntityLevel.Volume, vm.Source!.Identity.Level); Assert.Equal("11", vm.Source.Identity.Id);
        Assert.Equal(1, vm.HistoryDepth); Assert.Contains("单行本", vm.Incoming); Assert.Null(vm.Relations); Assert.Equal(1, f.Relations.Reads);
        Assert.True(vm.GoBack()); Assert.Equal("10", vm.Source.Identity.Id); Assert.Same(originalCandidates, vm.Candidates);
        Assert.Equal(BookEntityLevel.Series, vm.Source.Identity.Level); Assert.False(vm.GoBack());
        await vm.OpenRelatedAsync("12"); Assert.Equal(BookEntityLevel.Unknown, vm.Source.Identity.Level);
    }
    [Fact]
    public async Task LateSearchCannotOverwriteRapidSourceSwitchOrReturnToSameSource()
    {
        var f = new Fixture(); var pending = new TaskCompletionSource<BookSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Books.SearchHandler = (_, _) => pending.Task; using var vm = f.Model(); var a = Subject("10", true);
        vm.SetSource(a, null, true); await vm.ExpandAsync(); var first = vm.SearchAsync(); var oldToken = f.Books.LastToken;
        vm.SetSource(Subject("20", true), null, true); vm.SetSource(a, null, true); await vm.ExpandAsync();
        pending.SetResult(SearchResult(Publication("123456789"))); await first;
        Assert.True(oldToken.IsCancellationRequested); Assert.Equal("10", vm.Source!.Identity.Id); Assert.Empty(vm.Candidates); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task BackCancelsPendingChildAndRejectsItsLateSuccess()
    {
        var f = new Fixture(); f.Relations.Items = [Relation("11", "单行本")];
        var pending = new TaskCompletionSource<BangumiSubjectDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Subjects.Handler = (_, _) => pending.Task; using var vm = f.Model();
        vm.SetSource(Subject("10", true), null, true); await vm.ExpandAsync(); await vm.LoadRelationsAsync(); var opening = vm.OpenRelatedAsync("11");
        Assert.True(vm.CanBack); Assert.True(vm.GoBack()); pending.SetResult(new(Subject("11"), false, false, null)); await opening;
        Assert.Equal("10", vm.Source!.Identity.Id); Assert.Equal(0, vm.HistoryDepth); Assert.Equal(BookDetailsState.Cancelled, vm.State);
    }
    [Fact]
    public async Task LateFailureAfterDeactivateDoesNotReplaceCurrentStatus()
    {
        var f = new Fixture(); var pending = new TaskCompletionSource<BookSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Books.SearchHandler = (_, _) => pending.Task; using var vm = f.Model();
        vm.SetSource(Subject("10"), null, true); await vm.ExpandAsync(); var reading = vm.SearchAsync(); vm.Deactivate();
        vm.SetSource(Subject("20"), null, true); var message = vm.Message;
        pending.SetException(new BookProviderException(BookFailure.Offline, "旧请求离线")); await reading;
        Assert.Equal("20", vm.Source!.Identity.Id); Assert.Equal(message, vm.Message); Assert.Equal(BookDetailsState.Ready, vm.State);
    }
    [Fact]
    public async Task SameTitleAndSameIsbnRemainSeparateNdlIdentitiesAndAggregateCannotBind()
    {
        var f = new Fixture(); var first = Publication("123456789"); var second = Publication("123456780"); var aggregate = Publication("123456781") with { IsAggregate = true };
        f.Books.Publications = [first, second, aggregate]; using var vm = f.Model();
        vm.SetSource(Subject("10", false, [Isbn]), null, true); await vm.ExpandAsync(); await vm.SearchAsync();
        Assert.Equal(Isbn, f.Books.LastQuery!.Isbn); Assert.Equal(3, vm.Candidates.Length); Assert.Empty(vm.Bindings); Assert.Equal(0, f.Books.Confirms);
        await vm.PreviewAsync(first.Id); await vm.ConfirmAsync(); await vm.PreviewAsync(second.Id); await vm.ConfirmAsync();
        Assert.Equal(2, vm.Bindings.Length); Assert.Equal(2, f.Books.Confirms);
        await vm.PreviewAsync(aggregate.Id); Assert.False(vm.CanConfirm); await vm.ConfirmAsync(); Assert.Equal(2, f.Books.Confirms);
    }
    [Fact]
    public async Task DifferentVolumeIsbnBlocksConfirmationEvenAfterManualTitleSearch()
    {
        var f = new Fixture(); f.Books.Publications = [Publication("123456789") with { Isbns = [OtherIsbn] }]; using var vm = f.Model();
        vm.SetSource(Subject("10", false, [Isbn]), null, true); await vm.ExpandAsync(); await vm.SearchAsync(isbn: ""); await vm.PreviewAsync("123456789");
        Assert.Null(f.Books.LastQuery!.Isbn); Assert.False(vm.CanConfirm); Assert.Contains("ISBN", vm.ConfirmationBlock);
        await vm.ConfirmAsync(); Assert.Equal(0, f.Books.Confirms); Assert.Empty(vm.Bindings);
    }
    [Fact]
    public async Task SaveFailureNeverLooksConfirmedAndRemoveFailurePreservesSavedRecord()
    {
        var f = new Fixture(); using var vm = f.Model(); vm.SetSource(Subject("10"), null, true); await vm.ExpandAsync(); await vm.SearchAsync(); await vm.PreviewAsync("123456789");
        f.Books.FailSave = true; await vm.ConfirmAsync(); Assert.False(vm.PreviewIsBound); Assert.Empty(vm.Bindings); Assert.Equal(BookDetailsState.Failed, vm.State);
        f.Books.FailSave = false; await vm.ConfirmAsync(); Assert.True(vm.PreviewIsBound);
        f.Books.FailRemove = true; await vm.ForgetAsync("123456789"); Assert.True(vm.PreviewIsBound); Assert.Single(vm.Bindings);
        f.Books.FailRemove = false; await vm.ForgetAsync("123456789"); Assert.False(vm.PreviewIsBound); Assert.Empty(vm.Bindings);
    }
    [Theory]
    [InlineData(BookFailure.Unavailable, false)]
    [InlineData(BookFailure.InvalidResponse, false)]
    [InlineData(BookFailure.ResponseTooLarge, false)]
    [InlineData(BookFailure.Unavailable, true)]
    [InlineData(BookFailure.InvalidResponse, true)]
    [InlineData(BookFailure.ResponseTooLarge, true)]
    public async Task HardFailureClearsPublicationPreviewAndCandidatesWithoutDiscardingBangumi(BookFailure failure, bool duringSearch)
    {
        var f = new Fixture(); using var vm = f.Model(); var bg = Subject("10", false, [Isbn]);
        vm.SetSource(bg, null, true); await vm.ExpandAsync(); await vm.SearchAsync(); await vm.PreviewAsync("123456789");
        Assert.True(vm.CanConfirm); Assert.NotNull(vm.Search); Assert.Single(vm.Candidates);
        if (duringSearch)
        {
            f.Books.SearchHandler = (_, _) => Task.FromException<BookSearchResult>(new BookProviderException(failure, "合成硬失败"));
            await vm.SearchAsync(refresh: true);
        }
        else
        {
            f.Books.LoadHandler = (_, _, _) => Task.FromException<BookPublicationResult>(new BookProviderException(failure, "合成硬失败"));
            await vm.PreviewAsync("123456789", refresh: true);
        }
        Assert.Equal(BookDetailsState.Failed, vm.State); Assert.Null(vm.Preview); Assert.Null(vm.Search); Assert.Empty(vm.Candidates);
        Assert.False(vm.CanConfirm); Assert.Same(bg, vm.Bangumi); Assert.Equal("10", vm.Source!.Identity.Id);
        await vm.ConfirmAsync(); Assert.Equal(0, f.Books.Confirms); Assert.Empty(vm.Bindings);
    }
    [Fact]
    public async Task StalePreviewRemainsReadableButCannotBeConfirmedUntilFreshReload()
    {
        var f = new Fixture(); using var vm = f.Model(); var bg = Subject("10", false, [Isbn]);
        vm.SetSource(bg, null, true); await vm.ExpandAsync(); await vm.SearchAsync();
        f.Books.LoadHandler = (id, _, _) => Task.FromResult(new BookPublicationResult(f.Books.Publications.Single(x => x.Id == id), true, true, "离线，显示旧缓存"));
        await vm.PreviewAsync("123456789", refresh: true);
        Assert.NotNull(vm.Preview); Assert.True(vm.Preview.IsStale); Assert.Equal("123456789", vm.Preview.Publication.Id);
        Assert.False(vm.CanConfirm); Assert.Contains("刷新", vm.ConfirmationBlock); Assert.Same(bg, vm.Bangumi); Assert.Single(vm.Candidates);
        await vm.ConfirmAsync(); Assert.Equal(0, f.Books.Confirms); Assert.Empty(vm.Bindings);
        f.Books.LoadHandler = (id, refresh, _) =>
        {
            Assert.True(refresh);
            return Task.FromResult(new BookPublicationResult(f.Books.Publications.Single(x => x.Id == id), false, false, null));
        };
        await vm.PreviewAsync("123456789", refresh: true);
        Assert.True(vm.CanConfirm); Assert.Empty(vm.ConfirmationBlock);
        await vm.ConfirmAsync(); Assert.Equal(1, f.Books.Confirms); Assert.True(vm.PreviewIsBound);
    }
    [Fact]
    public async Task CandidatePreviewUsesSearchFactsWithoutAnotherRequestButExplicitRefreshLoadsRecord()
    {
        var f = new Fixture(); using var vm = f.Model();
        vm.SetSource(Subject("10", false, [Isbn]), null, true); await vm.ExpandAsync(); await vm.SearchAsync();
        await vm.PreviewAsync("123456789");
        Assert.Equal(0, f.Books.Loads); Assert.True(vm.Preview!.FromSearch); Assert.Same(vm.Candidates[0], vm.Preview.Publication);
        Assert.True(vm.CanConfirm); Assert.Contains("独立核验", vm.Message);
        await vm.PreviewAsync("123456789", refresh: true);
        Assert.Equal(1, f.Books.Loads); Assert.False(vm.Preview!.FromSearch); Assert.True(vm.CanConfirm);
    }
    [Fact]
    public async Task StaleSearchPreviewIsImmediateButCannotBeConfirmed()
    {
        var f = new Fixture(); f.Books.SearchHandler = (_, _) => Task.FromResult(SearchResult(f.Books.Publications) with { FromCache = true, IsStale = true, Warning = "离线缓存" });
        using var vm = f.Model(); vm.SetSource(Subject("10"), null, true); await vm.ExpandAsync(); await vm.SearchAsync();
        await vm.PreviewAsync("123456789"); await vm.ConfirmAsync();
        Assert.Equal(0, f.Books.Loads); Assert.Equal(0, f.Books.Confirms); Assert.True(vm.Preview!.FromSearch); Assert.True(vm.Preview.IsStale);
        Assert.False(vm.CanConfirm); Assert.Contains("刷新", vm.ConfirmationBlock);
    }
    [Fact]
    public async Task OnlySeenRelationsCandidatesAndSavedIdsCanBeOpenedOrChanged()
    {
        var f = new Fixture(); using var vm = f.Model(); vm.SetSource(Subject("10"), null, true); await vm.ExpandAsync();
        await vm.OpenRelatedAsync("999"); await vm.PreviewAsync("123456789"); await vm.ForgetAsync("123456789");
        Assert.Equal(0, f.Subjects.Reads); Assert.Equal(0, f.Books.Loads); Assert.Equal(0, f.Books.Removes);
    }
    [Fact]
    public async Task PaginationUsesBoundedProviderCursorAndDoesNotMergeByTitle()
    {
        var f = new Fixture(); f.Books.SearchHandler = (q, _) => Task.FromResult(new BookSearchResult(new([Publication(q.Start == 1 ? "123456789" : "123456780")], 2, q.Start == 1 ? 81 : null, Now), true, false, null));
        using var vm = f.Model(); vm.SetSource(Subject("10"), null, true); await vm.ExpandAsync(); await vm.SearchAsync(); await vm.NextAsync();
        Assert.Equal(81, vm.Query!.Start); Assert.Equal(2, vm.Candidates.Length); await vm.NextAsync(); Assert.Equal(2, f.Books.Searches);
    }
    private static BangumiSubjectDetails Subject(string id, bool? series = null, string[]? isbn = null) => new(id, BangumiRelationKind.Book, "同名作品", "同名作品", null, "书籍简介", "2020-01-01", "漫画", null, null, null, Now)
    { BookFacts = new(series, null, null, isbn ?? [], ["作者"], [], ["出版社"], [], [], null) };
    private static AniListDetails Ani(int id) => new(new(id, null, AniListMediaKind.Manga, "同名作品", "同名作品", "", "MANGA", "FINISHED", null, "2020-01-01", null, null, null, null, []), "简介", [], [], [], [], [], [], 1, false, false, Now);
    private static BangumiRelationItem Relation(string id, string label) => new(id, BangumiRelationKind.Book, "同名作品", "同名作品", label, null);
    private static BookPublication Publication(string id) => new(id, "同名作品", "同名作品", "1", [Isbn], [], ["作者 著"], ["出版社"], ["文库"], "2020.1", "初版", ["jpn"], false, Now);
    private static BookSearchResult SearchResult(params BookPublication[] publications) => new(new(publications, publications.Length, null, Now), false, false, null);
    private sealed class Fixture
    {
        public readonly SubjectReader Subjects = new(); public readonly BooksReader Books = new(); public readonly RelationsReader Relations = new();
        public BookDetailsViewModel Model() => new(Subjects, Books, Relations);
    }
    private sealed class SubjectReader : IBangumiSubjectDetailsReader
    {
        public int Reads; public Func<string, CancellationToken, Task<BangumiSubjectDetailsResult>>? Handler;
        public Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind, bool forceRefresh, CancellationToken cancellation)
        { Reads++; return Handler?.Invoke(subjectId, cancellation) ?? Task.FromResult(new BangumiSubjectDetailsResult(Subject(subjectId), false, false, null)); }
    }
    private sealed class RelationsReader : IBangumiBookRelationsReader
    {
        public int Reads; public BangumiRelationItem[] Items = [];
        public Task<BangumiRelationsLoadResult> LoadAsync(string id, bool refresh, CancellationToken cancellation)
        { Reads++; return Task.FromResult(new BangumiRelationsLoadResult(new(id, Now, Items.ToImmutableArray()), false, false, null)); }
    }
    private sealed class BooksReader : IBookBibliographyReader
    {
        public int BindingReads, Searches, Loads, Confirms, Removes;
        public bool FailSave, FailRemove;
        public CancellationToken LastToken; public BookSearchQuery? LastQuery;
        public BookPublication[] Publications = [Publication("123456789")];
        public Func<BookSearchQuery, CancellationToken, Task<BookSearchResult>>? SearchHandler;
        public Func<string, bool, CancellationToken, Task<BookPublicationResult>>? LoadHandler;
        private readonly List<BookPublicationBinding> saved = [];
        public Task<BookSearchResult> SearchAsync(BookSearchQuery query, bool refresh, CancellationToken cancellation)
        { Searches++; LastQuery = query; LastToken = cancellation; return SearchHandler?.Invoke(query, cancellation) ?? Task.FromResult(SearchResult(Publications)); }
        public Task<BookPublicationResult> LoadAsync(string id, bool refresh, CancellationToken cancellation)
        { Loads++; return LoadHandler?.Invoke(id, refresh, cancellation) ?? Task.FromResult(new BookPublicationResult(Publications.Single(x => x.Id == id), false, false, null)); }
        public Task<BookPublicationBinding[]> BindingsAsync(BookSource source, CancellationToken cancellation)
        { BindingReads++; return Task.FromResult(saved.Where(x => x.SourceIdentity == source.Identity && x.SourceFingerprint == source.Fingerprint).ToArray()); }
        public Task ConfirmAsync(BookSource source, BookPublication publication, CancellationToken cancellation)
        {
            Confirms++; if (FailSave) throw new IOException("合成保存失败");
            saved.RemoveAll(x => x.SourceIdentity == source.Identity && x.PublicationId == publication.Id);
            saved.Add(new(source.Identity, source.Fingerprint, publication.Id, Now, "合成用户确认")); return Task.CompletedTask;
        }
        public Task RemoveAsync(BookSource source, string publicationId, CancellationToken cancellation)
        { Removes++; if (FailRemove) throw new IOException("合成解除失败"); saved.RemoveAll(x => x.SourceIdentity == source.Identity && x.PublicationId == publicationId); return Task.CompletedTask; }
    }
}
