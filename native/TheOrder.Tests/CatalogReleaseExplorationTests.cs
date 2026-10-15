using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class CatalogReleaseExplorationTests
{
    private sealed class Credential : ICredentialProvider
    { public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>("synthetic-offline-test"); }
    private sealed class Handler(object row) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { @object = "list", items = new[] { row } }), Encoding.UTF8, "application/json") });
        }
    }
    private static Dictionary<string, object?> Remote(object? engine = null) => new()
    {
        ["object"] = "release", ["id"] = "163981", ["work_id"] = null, ["title"] = "CLANNAD - Steam Edition",
        ["date"] = "2015-11-23", ["release_kind"] = "default", ["lang"] = "en", ["platform"] = "win",
        ["platforms"] = new[] { "win" }, ["engine"] = engine, ["refs"] = new[] { new { source = "vndb", external_id = "r36069" } }
    };
    private static CatalogReleaseEdition Edition(string id, string kind, string language, params string[] platforms)
        => new(id, "86", "Edition " + id, null, kind, "2004-04-28", [language], platforms, [new WorkReference("vndb", "r" + id, "https://vndb.org/r" + id)]);
    private static CatalogReleaseEdition[] Editions() =>
    [
        Edition("1", "patch", "zh-Hans", "win"), Edition("2", "default", "ja", "win"),
        Edition("3", "default", "en", "lin", "win"), Edition("4", "trial", "ja", "win"),
        Edition("5", "physical", "ja", "psp"), Edition("6", "new-kind", "fr", "lin")
    ];
    private sealed class Reader(Func<int, string, CatalogReleasesPage> read) : ICatalogReleaseReader
    {
        public int Calls;
        public Task<CatalogReleasesPage> LoadReleasesAsync(string workId, string? cursor, bool refresh, CancellationToken cancellation)
        { Calls++; return Task.FromResult(read(Calls, workId)); }
    }
    private static CatalogReleasesPage Page(string work, CatalogReleaseEdition[] rows, string? next = null)
        => new(work, rows, next, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ReleaseSpecificEngineIsReadFromExistingListWithoutExtraRequests()
    {
        using var handler = new Handler(Remote(new { @object = "engine", id = "13", display_name = "RealLive" }));
        using var http = new HttpClient(handler);
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var first = await catalog.LoadReleasesAsync("86", null, false, default);
        Assert.Equal(new CatalogReleaseEngine("13", "RealLive"), Assert.Single(first.Items).Engine);
        var again = await catalog.LoadReleasesAsync("86", null, false, default);
        Assert.Equal(first.Items[0].Engine, again.Items[0].Engine); Assert.Equal(first.Items[0].Id, again.Items[0].Id); Assert.Equal(1, handler.Calls);
        Assert.Equal("r36069", Assert.Single(first.Items[0].References).Id);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrNullReleaseEngineRemainsUnknown(bool absent)
    {
        var row = Remote(); if (absent) row.Remove("engine");
        using var handler = new Handler(row); using var http = new HttpClient(handler);
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Null(Assert.Single((await catalog.LoadReleasesAsync("86", null, false, default)).Items).Engine);
    }
    [Theory]
    [InlineData("work", "13", "RealLive")]
    [InlineData("engine", "../13", "RealLive")]
    [InlineData("engine", "13", "")]
    [InlineData("engine", "13", " RealLive")]
    [InlineData("engine", "13 ", "RealLive")]
    public async Task MalformedEngineFactsRejectThePage(string type, string id, string title)
    {
        using var handler = new Handler(Remote(new { @object = type, id, display_name = title })); using var http = new HttpClient(handler);
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var error = await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadReleasesAsync("86", null, false, default));
        Assert.Equal(OnlineFailure.InvalidResponse, error.Failure);
    }
    [Fact]
    public async Task FiltersIntersectExactSourceCodesAndNeverMutateTheLoadedEditions()
    {
        var rows = Editions(); var reader = new Reader((_, work) => Page(work, rows));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        Assert.Equal(["2", "3", "5", "4", "1", "6"], model.VisibleItems.Select(row => row.Id));
        model.SetFilters("default", "en", "lin");
        Assert.Equal("3", Assert.Single(model.VisibleItems).Id); Assert.Equal(6, model.Items.Length);
        Assert.Equal(["lin", "win"], model.Items.Single(row => row.Id == "3").Platforms);
        model.SetFilters("default", "ja", "lin"); Assert.Empty(model.VisibleItems); Assert.Equal(6, model.Items.Length);
        model.ClearFilters(); Assert.Equal(6, model.VisibleItems.Length); Assert.False(model.HasFilters); Assert.Equal(1, reader.Calls);
    }
    [Fact]
    public async Task UnknownKindsAreAvailableAndInvalidFilterValuesCannotHideAllRecords()
    {
        var reader = new Reader((_, work) => Page(work, Editions()));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        Assert.Contains("new-kind", model.Kinds); model.SetFilters("new-kind", "fr", "lin");
        Assert.Equal("6", Assert.Single(model.VisibleItems).Id);
        model.SetFilters("made-up-kind", "EN", "Windows"); Assert.False(model.HasFilters); Assert.Equal(6, model.VisibleItems.Length);
    }
    [Fact]
    public async Task EmptyFilteredPageCanContinuePaginationWithoutClaimingTheWorkHasNoEditions()
    {
        var reader = new Reader((call, work) => call == 1 ? Page(work, [Editions()[1], Editions()[0]], "cur_next")
            : Page(work, [Edition("7", "default", "zh-Hans", "lin")]));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        model.SetFilters("default", "zh-Hans", null); Assert.Empty(model.VisibleItems); Assert.True(model.CanNext);
        await model.NextAsync(); Assert.Equal("7", Assert.Single(model.VisibleItems).Id); Assert.Equal(3, model.Items.Length); Assert.Equal(2, reader.Calls);
        Assert.Null(model.NextCursor); Assert.Equal("zh-Hans", model.LanguageFilter);
    }
    [Fact]
    public async Task RefreshKeepsExistingFiltersButDropsValuesNoLongerPresentAndSourceChangeResetsThem()
    {
        var reader = new Reader((call, work) => Page(work, call < 3 ? Editions() : [Edition("8", "default", "en", "lin")]));
        using var model = new CatalogReleasesViewModel(reader); model.SetSource("86"); await model.ExpandAsync();
        model.SetFilters("default", "ja", "win"); await model.RefreshAsync(); Assert.Equal("2", Assert.Single(model.VisibleItems).Id);
        await model.RefreshAsync(); Assert.Equal("default", model.KindFilter); Assert.Null(model.LanguageFilter); Assert.Null(model.PlatformFilter);
        Assert.Equal("8", Assert.Single(model.VisibleItems).Id);
        model.SetSource("3407"); Assert.Empty(model.VisibleItems); Assert.Empty(model.Items); Assert.False(model.HasFilters);
    }
    [Fact]
    public async Task AuthChangeDiscardsFactsAndFiltersBeforeTheNextRequest()
    {
        long auth = 0; var reader = new Reader((_, work) => Page(work, Editions()));
        using var model = new CatalogReleasesViewModel(reader, () => auth); model.SetSource("86"); await model.ExpandAsync();
        model.SetFilters("trial", "ja", "win"); auth++; model.SetSource("86");
        Assert.Empty(model.Items); Assert.Empty(model.VisibleItems); Assert.False(model.HasFilters); Assert.False(model.Loaded); Assert.False(model.Expanded);
    }
    [Fact]
    public void OptionalEngineDoesNotAlterLegacyEditionConstructionOrIdentity()
    {
        var row = Edition("3", "default", "en", "win"); Assert.Null(row.Engine); Assert.True(CatalogReleaseFacts.Valid(row, "86"));
        var withEngine = row with { Engine = new("13", "RealLive") }; Assert.True(CatalogReleaseFacts.Valid(withEngine, "86"));
        Assert.Equal(row.Id, withEngine.Id); Assert.Equal(row.WorkId, withEngine.WorkId); Assert.Equal(row.References, withEngine.References);
        Assert.False(CatalogReleaseFacts.Valid(row with { Engine = new("13", null!) }, "86"));
    }
}
