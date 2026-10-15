using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class AcgnRedTeamEvidenceTests
{
    [Theory]
    [InlineData("续作")]
    [InlineData("原作")]
    [InlineData("改编")]
    [InlineData("官方网站")]
    public void CanonicalTargetUrlDoesNotProveSameSubject(string key)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { infobox = new[] { new { key, value = "https://anilist.co/anime/4181" } } }));
        Assert.Empty(AniListBridgeLinks.Map(data.RootElement, BangumiRelationKind.Anime));
    }

    [Fact]
    public void LinkFieldKeepsExplicitIdentityButRejectsRelationLabelsAndEmbeddedProse()
    {
        using var data = JsonDocument.Parse("""{"infobox":[{"key":"外部链接","value":[{"k":"AniList","v":"https://anilist.co/anime/2167"},{"k":"续作","v":"https://anilist.co/anime/4181"}]},{"key":"url","value":"续作见 https://anilist.co/anime/4181"}]}""");
        Assert.Equal(["https://anilist.co/anime/2167"], AniListBridgeLinks.Map(data.RootElement, BangumiRelationKind.Anime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrTruncatedPaginationNeverProvesUniqueCandidate(bool truncated)
    {
        var response = "{\"data\":{\"Page\":{" + (truncated ? "\"pageInfo\":{\"hasNextPage\":true}," : "")
            + "\"media\":[" + AniListTestData.MediaJson + "]}}}";
        using var handler = new AniListTestData.Handler((_, _) => Task.FromResult(AniListTestData.Response(response)));
        using var client = new HttpClient(handler);
        var service = new AniListService(new AniListProvider(client, new AniListTestData.Clock().Rate()));
        var match = await service.MatchAsync(StrongSource(), null, false, default);
        Assert.Null(match.Binding); Assert.Single(match.Candidates); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SecondLanguageQueryCanDisproveFirstQueryUniqueness()
    {
        int requests = 0;
        using var handler = new AniListTestData.Handler((_, _) =>
        {
            var json = ++requests == 1 ? AniListTestData.MediaJson : AniListTestData.MediaJson.Replace("\"id\":2167", "\"id\":4181");
            return Task.FromResult(AniListTestData.Response("{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false},\"media\":[" + json + "]}}}"));
        });
        using var client = new HttpClient(handler);
        var service = new AniListService(new AniListProvider(client, new AniListTestData.Clock().Rate()));
        var result = await service.MatchAsync(StrongSource(), null, false, default);
        Assert.Equal(2, result.Candidates.Length); Assert.Null(result.Binding); Assert.Equal(2, requests);
    }

    [Fact]
    public async Task CompleteQueriesAndDetailRevalidationMayStillAutoAssociate()
    {
        using var handler = new AniListTestData.Handler(async (request, _) =>
        {
            var query = await request.Content!.ReadAsStringAsync();
            return AniListTestData.Response(query.Contains("Page(page:1")
                ? "{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false},\"media\":[" + AniListTestData.MediaJson + "]}}}"
                : "{\"data\":{\"Media\":" + AniListTestData.DetailJson + "}}");
        });
        using var client = new HttpClient(handler);
        var disk = new AniListMemoryCache();
        var service = new AniListService(new AniListProvider(client, new AniListTestData.Clock().Rate()), disk);
        var result = await service.MatchAsync(StrongSource(), null, false, default);
        Assert.Equal(2167, result.Binding?.MediaId); Assert.Equal(1, result.Binding?.AutomaticPolicyVersion);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2167, (await new AniListService(new AniListFakeProvider(), disk).MatchAsync(StrongSource(), null, false, default)).Binding?.MediaId);
    }

    [Fact]
    public async Task LegacyAutomaticChoiceNeedsFreshCompleteEvidenceButManualChoiceSurvives()
    {
        var source = StrongSource(); var key = "binding:" + source.Fingerprint;
        var disk = new AniListMemoryCache();
        disk.Values[key] = new(key, AniListTestData.Stamp, Binding: new(source.Fingerprint, 9999, source.Kind, "自动关联 · 旧策略"));
        var provider = new AniListFakeProvider();
        var result = await new AniListService(provider, disk).MatchAsync(source, null, false, default);
        Assert.Equal(2167, result.Binding?.MediaId); Assert.Equal(1, provider.Searches);
        Assert.Equal(1, disk.Values[key].Binding?.AutomaticPolicyVersion);
        disk.Values[key] = new(key, AniListTestData.Stamp, Binding: new(source.Fingerprint, 8888, source.Kind, "用户核对确认 · Bangumi → AniList"));
        Assert.Equal(8888, (await new AniListService(provider, disk).MatchAsync(source, null, false, default)).Binding?.MediaId);
        Assert.Equal(1, provider.Searches);
    }

    private static AniListSource StrongSource() => AniListTestData.Source() with { Date = "2007-10-05", Episodes = 23 };

    [Fact]
    public async Task ExcessIdentityEvidenceCannotDegradeIntoNoEvidence()
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { infobox = Enumerable.Range(1, 17)
            .Select(id => new { key = "url", value = "https://anilist.co/anime/" + id }).ToArray() }));
        var evidence = AniListBridgeLinks.MapEvidence(data.RootElement, BangumiRelationKind.Anime);
        Assert.True(evidence.Ambiguous);
        var source = StrongSource() with { ExternalLinks = evidence.Links, HasAmbiguousExternalIdentity = evidence.Ambiguous };
        var service = new AniListService(new AniListFakeProvider());
        Assert.Null((await service.MatchAsync(source, null, false, default)).Binding);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(source, AniListTestData.Details(), default));
    }

    [Fact]
    public async Task SecondQueryOutageKeepsPartialManualCandidatesWithoutAutoBinding()
    {
        int calls = 0;
        using var handler = new AniListTestData.Handler((_, _) => Task.FromResult(++calls == 1
            ? AniListTestData.Response("{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false},\"media\":[" + AniListTestData.MediaJson + "]}}}")
            : AniListTestData.Response("{}", System.Net.HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        var service = new AniListService(new AniListProvider(client, new AniListTestData.Clock().Rate()));
        var result = await service.MatchAsync(StrongSource(), null, false, default);
        Assert.Single(result.Candidates); Assert.Null(result.Binding); Assert.Contains("不完整", result.Notice);
    }

    [Fact]
    public void DefaultEvidenceAdditionsDoNotChangeLegacyManualCachePayload()
    {
        var source = StrongSource();
        var value = new AniListCacheValue("binding:" + source.Fingerprint, AniListTestData.Stamp,
            Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认"));
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("AutomaticPolicyVersion", json); Assert.DoesNotContain("SearchComplete", json);
        Assert.Equal(value, JsonSerializer.Deserialize<AniListCacheValue>(json));
    }
}
