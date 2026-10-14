using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class CatalogReleasesTests
{
    private sealed class Credential : ICredentialProvider
    { public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>("synthetic-test-key"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => reply(request, cancellation); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Row(string id = "159513", object? parent = null, string? title = null, string? date = "2012-09",
        string kind = "patch", string language = "fr", string platform = "lin", string[]? platforms = null) => new
    {
        @object = "release", id, work_id = parent, title, date, release_kind = kind, lang = language, platform,
        platforms = platforms ?? ["lin", "win"], refs = new[] { new { source = "vndb", external_id = "r157764" } }
    };
    private static object Page(object[] rows, string? cursor = null) => cursor is null
        ? new { @object = "list", items = rows } : (object)new { @object = "list", items = rows, next_cursor = cursor };
    private static CatalogReleaseEdition Edition(string id = "1", string work = "86") => new(id, work, null, null, "patch", "2012-09", ["fr"], ["lin", "win"], [new("vndb", "r1", "https://vndb.org/r1")]);
    private static CatalogReleasesPage DisplayPage(string work = "86", string? next = null, params CatalogReleaseEdition[] rows)
        => new(work, rows.Length == 0 ? [Edition(work: work)] : rows, next, DateTimeOffset.UnixEpoch);
    private sealed class Reader(Func<string, string?, bool, CancellationToken, Task<CatalogReleasesPage>> read) : ICatalogReleaseReader
    {
        public int Calls;
        public Task<CatalogReleasesPage> LoadReleasesAsync(string workId, string? cursor, bool refresh, CancellationToken cancellation)
        { Calls++; return read(workId, cursor, refresh, cancellation); }
    }

    [Fact]
    public async Task DisplayPreservesNullTitlePartialDateAllPlatformsAndReleaseReferences()
    {
        Uri? seen = null;
        using var http = new HttpClient(new Handler((request, _) => { seen = request.RequestUri; return Task.FromResult(Json(Page([Row()]))); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var page = await catalog.LoadReleasesAsync("3407", null, false, default);
        var row = Assert.Single(page.Items);
        Assert.Equal("3407", row.WorkId); Assert.Equal("159513", row.Id); Assert.Null(row.Title); Assert.Null(row.OriginalTitle);
        Assert.Equal("patch", row.Kind); Assert.Equal("2012-09", row.Date); Assert.Equal(["fr"], row.Languages); Assert.Equal(["lin", "win"], row.Platforms);
        Assert.Equal(new WorkReference("vndb", "r157764", "https://vndb.org/r157764"), Assert.Single(row.References));
        Assert.Null(page.NextCursor); Assert.Equal("/v2/catalog/works/3407/releases", seen!.AbsolutePath);
        Assert.Contains("limit=100", seen.Query); Assert.DoesNotContain("q=", seen.Query);
    }
    [Fact]
    public async Task EmptySourceFieldsRemainEmptyAndUnknownTypeIsRetained()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Page([Row(date: null, kind: "new-kind", language: "", platform: "", platforms: [])])))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var row = Assert.Single((await catalog.LoadReleasesAsync("86", null, false, default)).Items);
        Assert.Null(row.Date); Assert.Empty(row.Languages); Assert.Empty(row.Platforms); Assert.Equal("new-kind", row.Kind);
    }
    [Theory]
    [InlineData("99", "2012-09")]
    [InlineData("86", "2012-13")]
    [InlineData("86", "2012-02-30")]
    public async Task ConflictingParentOrInvalidCalendarDateIsRejected(string parent, string date)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Page([Row(parent: parent, date: date)])))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var error = await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("86", null, false, default));
        Assert.Equal(OnlineFailure.InvalidResponse, error.Failure);
    }
    [Fact]
    public async Task DuplicateReleaseIdsAndRepeatedCursorAreRejected()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("cursor=")
            ? Page([Row()], "cur_same") : Page([Row(), Row()])))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("86", null, false, default));
        await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("86", "cur_same", false, default));
    }
    [Fact]
    public async Task CacheOwnsItsArraysRefreshInvalidatesLaterPagesAndExpiryReloads()
    {
        int calls = 0; var now = DateTimeOffset.UnixEpoch;
        using var http = new HttpClient(new Handler((request, _) => { calls++; return Task.FromResult(Json(Page([Row()], request.RequestUri!.Query.Contains("cursor=") ? null : "cur_next"))); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")), clock: () => now);
        var first = await catalog.LoadReleasesAsync("86", null, false, default);
        first.Items[0].Platforms[0] = "mutated";
        Assert.Equal("lin", (await catalog.LoadReleasesAsync("86", null, false, default)).Items[0].Platforms[0]); Assert.Equal(1, calls);
        await catalog.LoadReleasesAsync("86", "cur_next", false, default); Assert.Equal(2, calls);
        await catalog.LoadReleasesAsync("86", null, true, default); Assert.Equal(3, calls);
        await catalog.LoadReleasesAsync("86", "cur_next", false, default); Assert.Equal(4, calls);
        now = now.AddMinutes(6); await catalog.LoadReleasesAsync("86", null, false, default); Assert.Equal(5, calls);
        catalog.ClearCache(); await catalog.LoadReleasesAsync("86", null, false, default); Assert.Equal(6, calls);
    }
    [Fact]
    public async Task CancellationAfterMappingDoesNotPublishCache()
    {
        int calls = 0; using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Page([Row()]))); }));
        var transport = new CatalogTransport(http, new Credential(), new("https://api.nextmoe.dev/v2/"));
        transport.AcceptanceProbe = stage => { if (stage == OnlineAcceptanceStage.BeforeReturn) stop.Cancel(); };
        var catalog = new NextMoeCatalog(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.LoadReleasesAsync("86", null, false, stop.Token));
        transport.AcceptanceProbe = null; await catalog.LoadReleasesAsync("86", null, false, default); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CancelledOrMalformedRequestsSendNothing()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Page([]))); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.LoadReleasesAsync("86", null, false, new(true)));
        await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("../86", null, false, default));
        await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("86", "bad", false, default)); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task ViewModelIsLazyAndRefreshCanReplaceTheSameCursor()
    {
        var reader = new Reader((work, cursor, _, _) => Task.FromResult(DisplayPage(work, cursor is null ? "cur_next" : null,
            Edition(cursor is null ? "1" : "2", work))));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); Assert.Equal(0, reader.Calls); Assert.False(model.Expanded);
        await model.ExpandAsync(); await model.NextAsync(); Assert.Equal(2, model.Items.Length); Assert.Null(model.NextCursor);
        await model.RefreshAsync(); Assert.Single(model.Items); Assert.Equal("cur_next", model.NextCursor); Assert.Equal(3, reader.Calls);
    }
    [Fact]
    public async Task FailedInitialLoadIsNotReportedAsEmptyAndCanBeRetried()
    {
        bool fail = true; var reader = new Reader((work, _, _, _) => fail ? Task.FromException<CatalogReleasesPage>(new OnlineException(OnlineFailure.Offline))
            : Task.FromResult(new CatalogReleasesPage(work, [], null, DateTimeOffset.UnixEpoch)));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        Assert.False(model.Loaded); Assert.Equal(CatalogReleasesState.Failed, model.State);
        fail = false; await model.RefreshAsync(); Assert.True(model.Loaded); Assert.Empty(model.Items); Assert.Contains("未收录", model.Message);
    }
    [Fact]
    public async Task LateAResultCannotOverwriteBOrANewVisitToA()
    {
        var pending = new TaskCompletionSource<CatalogReleasesPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader((work, _, _, _) => Task.FromResult(DisplayPage(work)));
        var calls = 0; var late = new Reader((work, _, _, _) => ++calls == 1 ? pending.Task : reader.LoadReleasesAsync(work, null, false, default));
        using var model = new CatalogReleasesViewModel(late); model.SetSource("86"); var old = model.ExpandAsync();
        model.SetSource("3407"); await model.ExpandAsync(); model.SetSource("86"); await model.ExpandAsync();
        pending.SetResult(DisplayPage("86", null, Edition("old"))); await old;
        Assert.Equal("86", model.WorkId); Assert.Equal("1", Assert.Single(model.Items).Id);
    }
    [Fact]
    public async Task CollapseDeactivateAndAuthChangeDiscardLateResponses()
    {
        long auth = 0; var pending = new TaskCompletionSource<CatalogReleasesPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader((_, _, _, _) => pending.Task); using var model = new CatalogReleasesViewModel(reader, () => auth);
        model.SetSource("86"); var load = model.ExpandAsync(); auth++;
        pending.SetResult(DisplayPage()); await load; Assert.False(model.Loaded); Assert.Empty(model.Items); Assert.Equal(CatalogReleasesState.Cancelled, model.State);
        model.Deactivate(); Assert.Null(model.WorkId); Assert.False(model.Expanded);
        var another = new TaskCompletionSource<CatalogReleasesPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var collapsed = new CatalogReleasesViewModel(new Reader((_, _, _, _) => another.Task)); collapsed.SetSource("86"); var task = collapsed.ExpandAsync();
        collapsed.Collapse(); another.SetResult(DisplayPage()); await task; Assert.Empty(collapsed.Items); Assert.False(collapsed.Loaded);
    }
    [Fact]
    public async Task WrongWorkDuplicateRowsAndCursorLoopsClearUntrustworthyRows()
    {
        int calls = 0; var reader = new Reader((work, _, _, _) => Task.FromResult(++calls == 1
            ? DisplayPage(work, "cur_next") : DisplayPage(work, "cur_next", Edition("2", work))));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync(); await model.NextAsync();
        Assert.Equal(CatalogReleasesState.Failed, model.State); Assert.Empty(model.Items); Assert.False(model.Loaded);
        using var wrong = new CatalogReleasesViewModel(new Reader((_, _, _, _) => Task.FromResult(DisplayPage("99"))));
        wrong.SetSource("86"); await wrong.ExpandAsync(); Assert.False(wrong.Loaded); Assert.Equal(CatalogReleasesState.Failed, wrong.State);
    }
    [Fact]
    public async Task FiveHundredRowsIsAnExplicitBoundAndNotAFalseCompletePage()
    {
        int page = 0; var reader = new Reader((work, _, _, _) => Task.FromResult(new CatalogReleasesPage(work,
            Enumerable.Range(++page * 100, 100).Select(id => Edition(id.ToString(), work)).ToArray(), "cur_" + page, DateTimeOffset.UnixEpoch)));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        for (int i = 0; i < 4; i++) await model.NextAsync();
        Assert.Equal(500, model.Items.Length); Assert.NotNull(model.NextCursor); Assert.False(model.CanNext); Assert.Contains("上限", model.Message);
        await model.NextAsync(); Assert.Equal(5, reader.Calls);
    }
    [Fact]
    public void WorkDateUsesSourcePrecisionAndCompanyRoleDoesNotUseRegistryKind()
    {
        Assert.Equal("2012-09", CatalogReleaseFacts.WorkDate("2012-09-01", "month"));
        Assert.Equal("2012", CatalogReleaseFacts.WorkDate("2012-01-01", "year"));
        Assert.Equal("legacy date", CatalogReleaseFacts.WorkDate("legacy date", "year"));
        using var doc = JsonDocument.Parse("""{"object":"work","id":"86","display_name":"CLANNAD","companies":[{"id":"2","display_name":"Key","company_kind":"publisher","attribution_role":"developer"},{"id":"39","display_name":"VISUAL ARTS","company_kind":"publisher","attribution_role":"publisher"}]}""");
        var work = new RemoteWorkDto(doc.RootElement).Map(false, DateTimeOffset.UnixEpoch);
        Assert.Equal("developer", work.Presentation!.Companies.Single(c => c.Id == "2").Relation);
        Assert.Equal("publisher", work.Presentation.Companies.Single(c => c.Id == "39").Relation);
        Assert.Equal("Key", Assert.Single(work.Developers)); Assert.Equal("相关组织", CatalogReleaseFacts.CompanyCaption(null));
    }
    [Theory]
    [InlineData(OnlineFailure.Forbidden)][InlineData(OnlineFailure.NotFound)][InlineData(OnlineFailure.InvalidResponse)]
    public async Task PermanentFailureClearsPreviouslyVisibleReleaseFacts(OnlineFailure failure)
    {
        bool fail = false;
        var reader = new Reader((_, _, _, _) => fail ? throw new OnlineException(failure) : Task.FromResult(DisplayPage()));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        fail = true; await model.RefreshAsync();
        Assert.Empty(model.Items); Assert.Null(model.FetchedAtUtc); Assert.Null(model.NextCursor); Assert.False(model.Loaded);
        Assert.Equal(CatalogReleasesState.Failed, model.State);
    }
    [Fact] public async Task NetworkFailureRetainsReleaseFactsWithVisiblePreviousDataNotice()
    {
        bool fail = false;
        var reader = new Reader((_, _, _, _) => fail ? throw new OnlineException(OnlineFailure.Offline) : Task.FromResult(DisplayPage()));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        fail = true; await model.RefreshAsync();
        Assert.Single(model.Items); Assert.Contains("之前", model.Message); Assert.Equal(CatalogReleasesState.Failed, model.State);
    }
}
