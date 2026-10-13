using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class RelatedGameIdentityTests
{
    private static CatalogEntityRef Game(string id = "3407") => new(CatalogEntityKind.Work, id, "Title is not evidence") { KunGalGalgameId = id };
    private static object Row(string id, string[] subjects, string medium = "galgame", string claim = "3407") => new
    {
        @object = "work", id, medium, claim = new { site = "kungal", site_work_id = claim, state = "live" },
        refs = subjects.Select(subject => new { source = "bangumi", external_id = subject })
    };
    private sealed class Credential : ICredentialProvider { public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>("isolated-test-key"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => reply(request, cancellation); }
    private static HttpResponseMessage List(params object[] rows) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { @object = "list", items = rows }), Encoding.UTF8, "application/json") };
    [Fact]
    public async Task OneBoundedReferenceBatchUsesExactIdsAndReturnsOnlyExplicitClaimedGames()
    {
        var calls = new List<Uri>();
        using var http = new HttpClient(new Handler((request, _) => { calls.Add(request.RequestUri!); return Task.FromResult(List(Row("3407", ["3660"]), Row("99", ["51"], claim: "99"))); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var result = await catalog.LoadRelatedIdentitiesAsync([Game()], default);
        Assert.Equal(new RelatedGameIdentity("3407", "3407", "3660"), Assert.Single(result));
        var uri = Assert.Single(calls); Assert.Equal("/v2/catalog/works", uri.AbsolutePath);
        Assert.Contains("ids=3407", uri.Query); Assert.Contains("include=refs", uri.Query); Assert.DoesNotContain("q=", uri.Query);
    }
    [Theory]
    [InlineData("anime", "3407")]
    [InlineData("galgame", "99")]
    public async Task ChangedMediaOrClaimCannotProvideMergeEvidence(string medium, string claim)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(List(Row("3407", ["3660"], medium, claim)))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Empty(await catalog.LoadRelatedIdentitiesAsync([Game()], default));
    }
    [Theory]
    [InlineData("3660", "51")]
    [InlineData("3660", "bad")]
    [InlineData("", "")]
    public async Task MultipleOrMalformedBangumiReferencesRemainUnmerged(string one, string two)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(List(Row("3407", [one, two])))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Empty(await catalog.LoadRelatedIdentitiesAsync([Game()], default));
    }
    [Fact]
    public async Task ConflictingDuplicateRowsCannotChooseTheFirstReference()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(List(Row("3407", ["3660"]), Row("3407", ["51"])))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Empty(await catalog.LoadRelatedIdentitiesAsync([Game()], default));
    }
    [Fact]
    public async Task CancelledOrInvalidInputSendsNothing()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(List()); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.LoadRelatedIdentitiesAsync([Game()], new(true)));
        await Assert.ThrowsAsync<OnlineException>(() => catalog.LoadRelatedIdentitiesAsync([Game("bad")], default));
        Assert.Empty(await catalog.LoadRelatedIdentitiesAsync([], default)); Assert.Equal(0, calls);
    }
}
