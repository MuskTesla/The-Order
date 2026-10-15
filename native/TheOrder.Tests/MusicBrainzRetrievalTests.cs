using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicBrainzRetrievalTests
{
    private const string Target = "78b1fb7c-5ddf-4acb-9ba1-d1a905f4046e";
    private const string FullTitle = "千恋*万花 キャラクターソング Vol.4 「Blue sky」";
    private const string BangumiJson = """
    {"id":172618,"type":3,"name":"Blue sky","name_cn":"","summary":"","nsfw":false,"date":"2016-05-27",
    "infobox":[{"key":"别名","value":[{"v":"千恋＊万花 キャラクターソング Vol.4 「Blue sky」"}]},
    {"key":"艺术家","value":"レナ・リヒテナウアー（CV：種﨑敦美）"},{"key":"碟片数量","value":"1"}]}
    """;
    private static MusicAlbumSource Source()
    {
        using var json = JsonDocument.Parse(BangumiJson);
        return MusicAlbumSource.From(BangumiSubjectDetailsProvider.Map("172618", BangumiRelationKind.Music, json.RootElement, MusicTestData.Stamp));
    }
    private static string Result(bool target) => JsonSerializer.Serialize(new
    {
        count = target ? 1 : 200,
        releases = Enumerable.Range(1, target ? 1 : 25).Select(i => new
        {
            id = target ? Target : i.ToString("x8") + "-1111-1111-1111-111111111111",
            title = target ? FullTitle : "Blue Sky", date = target ? "2016-05-27" : "2004-01-01", status = "Official", country = "JP",
            media = new[] { new Dictionary<string, object> { ["format"] = "CD", ["track-count"] = 3 } }
        })
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HomonymousTitleResultsCannotPreventMoreSpecificDateOrDeclaredAliasRetrieval(bool requireAlias)
    {
        using var handler = new MusicTestData.Handler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Task.FromResult(MusicTestData.Response(Result(requireAlias ? query.Contains("千恋", StringComparison.Ordinal) : query.Contains("date:", StringComparison.Ordinal))));
        });
        using var http = new HttpClient(handler);
        var source = Source();
        var found = await new MusicBrainzProvider(http, new MusicTestData.Clock().Rate()).SearchAsync(source, default);
        Assert.Contains(found.Candidates, c => c.Id == Target);
        Assert.DoesNotContain(found.Candidates, c => c.Title == "Blue Sky");
        Assert.Null(MusicBrainzMatching.Automatic(source, found));
    }

    [Fact]
    public void OnlyDeclaredAliasesSupplyRetrievalHintsAndLegacyIdentityFingerprintsStayStable()
    {
        using var json = JsonDocument.Parse(BangumiJson.Replace("\"key\":\"别名\"", "\"key\":\"相关作品\""));
        var unrelated = MusicAlbumSource.From(BangumiSubjectDetailsProvider.Map("172618", BangumiRelationKind.Music, json.RootElement, MusicTestData.Stamp));
        var source = Source();
        Assert.Equal("千恋＊万花 キャラクターソング Vol.4 「Blue sky」", Assert.Single(source.Facts.TitleAliases));
        Assert.Empty(unrelated.Facts.TitleAliases);
        Assert.Equal(unrelated.Fingerprint, source.Fingerprint);
        var withoutField = JsonSerializer.Deserialize<BangumiMusicFacts>("""{"CatalogNumbers":[],"Artists":[],"Labels":[],"Barcode":null,"DiscCount":null,"ExternalIds":[]}""")!;
        Assert.Empty(withoutField.TitleAliases); Assert.True(withoutField.IsValid);
    }

    [Fact]
    public void AliasAndCharacterQueriesAreNotIdentityEvidenceWithoutEditionFacts()
    {
        var source = Source();
        var candidate = MusicTestData.Candidate() with { Title = FullTitle, Date = source.ReleaseDate, DiscCount = 1, Artist = "レナ · リヒテナウアー(CV:沢澤砂羽)" };
        Assert.Contains("来源提供的完整标题别名一致", MusicBrainzMatching.Assess(source, candidate).Evidence);
        Assert.Null(MusicBrainzMatching.Automatic(source, new([candidate], 1, false, null, null)));
        Assert.Contains("レナ・リヒテナウアー", MusicBrainzSearchQueries.ArtistTerms(source.Facts.Artists));
        Assert.Contains(MusicBrainzSearchQueries.Retrieval(source), query => query.Contains("artist:\"レナ・リヒテナウアー\"", StringComparison.Ordinal));
        var conflict = source with { Facts = source.Facts with { ExternalIds = [new(MusicBrainzEntityKind.Release, MusicTestData.Other)] } };
        Assert.True(MusicBrainzMatching.Assess(conflict, candidate).HasIdentityConflict);
    }

    [Fact]
    public async Task ATruncatedContextPageCannotHideTheFullAliasQuery()
    {
        int calls = 0;
        using var handler = new MusicTestData.Handler((_, _) => Task.FromResult(MusicTestData.Response(Result(++calls > 1))));
        using var http = new HttpClient(handler);
        var result = await new MusicBrainzProvider(http, new MusicTestData.Clock().Rate()).SearchAsync(Source(), default);
        Assert.Equal(Target, Assert.Single(result.Candidates).Id); Assert.Equal(2, calls); Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ACompleteButContradictoryHitCannotStopTheRemainingContextQueries()
    {
        int calls = 0;
        var wrong = "{\"count\":1,\"releases\":[" + MusicTestData.AlbumJson + "]}";
        using var handler = new MusicTestData.Handler((_, _) => Task.FromResult(MusicTestData.Response(++calls == 1 ? wrong : Result(true))));
        using var http = new HttpClient(handler);
        var result = await new MusicBrainzProvider(http, new MusicTestData.Clock().Rate()).SearchAsync(Source(), default);
        Assert.Equal(Target, Assert.Single(result.Candidates).Id); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RevisedRetrievalSkipsOldNoisySearchCacheAndAliasChangesGetTheirOwnCacheKey()
    {
        var source = Source(); var cache = new MusicMemoryCache();
        var old = "search:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.Fingerprint + ":retrieval-3")));
        cache.Values[old] = new(old, DateTimeOffset.UtcNow, Search: MusicTestData.Search());
        var provider = new MusicTestProvider(); var service = new MusicBrainzService(provider, cache);
        await service.MatchAsync(source, false, default); Assert.Equal(1, provider.Searches);
        await service.MatchAsync(source, false, default); Assert.Equal(1, provider.Searches);
        var newAlias = source with { Facts = source.Facts with { TitleAliases = ["Another declared title"] } };
        Assert.Equal(source.Fingerprint, newAlias.Fingerprint);
        await service.MatchAsync(newAlias, false, default); Assert.Equal(2, provider.Searches);
        // A human choice uses the stable source identity, not either search key.
        await service.ConfirmAsync(source, MusicTestData.Album(), default);
        Assert.Equal(MusicTestData.Release, (await service.MatchAsync(newAlias, false, default)).ConfirmedReleaseId);
        Assert.Equal(2, provider.Searches);
    }

    [Fact]
    public void SearchPlanEscapesAliasesAndArtistsAndHasAFixedRequestBudget()
    {
        var source = Source() with { Facts = Source().Facts with { TitleAliases = ["Album\" OR artist:*"], Artists = ["Person\" OR artist:*"] } };
        var queries = MusicBrainzSearchQueries.Retrieval(source);
        Assert.InRange(queries.Length, 1, MusicBrainzSearchQueries.MaximumTitleQueries);
        Assert.Contains(queries, q => q.Contains("Album\\\" OR artist:*", StringComparison.Ordinal));
        Assert.All(queries.Where(q => q.Contains("Person", StringComparison.Ordinal)), q => Assert.Contains("Person\\\" OR artist:*", q));
        Assert.Equal(queries.Length, queries.Distinct().Count());
    }
}
