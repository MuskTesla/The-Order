using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

internal static class AniListTestData
{
    public static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
    public const string MediaJson = """{"id":2167,"idMal":2167,"type":"ANIME","title":{"romaji":"CLANNAD","english":"Clannad","native":"ＣＬＡＮＮＡＤ -クラナド-"},"synonyms":["CLANNAD -クラナド-"],"format":"TV","status":"FINISHED","coverImage":{"large":"https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx2167.jpg"},"startDate":{"year":2007,"month":10,"day":5},"endDate":{"year":2008,"month":3,"day":28},"episodes":23,"chapters":null,"volumes":null}""";
    public static string DetailJson => MediaJson[..^1] + """, "description":"First <br>second &amp; third", "genres":["Drama"],"tags":[{"name":"School","isGeneralSpoiler":false,"isMediaSpoiler":false},{"name":"Secret","isGeneralSpoiler":true}],"studios":{"nodes":[{"name":"Kyoto Animation"}]},"characters":{"pageInfo":{"hasNextPage":true},"edges":[{"role":"MAIN","node":{"id":11,"name":{"full":"Tomoya Okazaki","native":"岡崎朋也"},"image":{"large":null}},"voiceActors":[{"id":12,"name":{"full":"Yuichi Nakamura","native":"中村悠一"},"image":{"large":null}}]}]},"staff":{"pageInfo":{"hasNextPage":false},"edges":[{"role":"Director","node":{"id":13,"name":{"full":"Tatsuya Ishihara","native":"石原立也"},"image":{"large":null}}}]},"relations":{"edges":[]}}""";
    public static AniListMedia Media() { using var doc = JsonDocument.Parse(MediaJson); return AniListProvider.MapMedia(doc.RootElement); }
    public static AniListDetails Details(int id = 2167, int page = 1) { using var doc = JsonDocument.Parse(DetailJson); var result = AniListProvider.MapDetails(doc.RootElement, page, Stamp); return result with { Media = result.Media with { Id = id } }; }
    public static AniListSource Source() => new("51", AniListMediaKind.Anime, "CLANNAD", "CLANNAD -クラナド-", "2007-10-04", "TV", 22, []);
    public static HttpResponseMessage Response(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { Calls++; return action(request, cancellation); }
    }
    public sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Stamp; public List<TimeSpan> Waits = [];
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
        public Task Delay(TimeSpan value, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Waits.Add(value); Now += value; return Task.CompletedTask; }
        public AniListRateCoordinator Rate() => new(this, Delay);
    }
}
public sealed class AniListTests
{
    [Fact] public void RichMappingKeepsIdentityOriginalTitlesRolesAndSanitizesSummary()
    {
        var details = AniListTestData.Details(); Assert.True(AniListValidation.Details(details)); Assert.Equal(2167, details.Media.Id);
        Assert.Equal("ＣＬＡＮＮＡＤ -クラナド-", details.Media.NativeTitle); Assert.Equal("2007-10-05", details.Media.StartDate); Assert.Equal(23, details.Media.Episodes);
        Assert.Equal("First \nsecond & third", details.Description); Assert.Equal(["School"], details.Tags);
        Assert.Equal("中村悠一", Assert.Single(Assert.Single(details.Characters).VoiceActors).NativeName); Assert.Single(details.Staff); Assert.True(details.MoreCharacters);
    }
    [Theory] [InlineData("ANIME","TV",AniListMediaKind.Anime)] [InlineData("MANGA","MANGA",AniListMediaKind.Manga)] [InlineData("MANGA","NOVEL",AniListMediaKind.Manga)]
    public void MangaAndNovelShareProviderButKeepFormat(string type, string format, AniListMediaKind expected)
    {
        using var json = JsonDocument.Parse(AniListTestData.MediaJson.Replace("\"ANIME\"", "\"" + type + "\"").Replace("\"TV\"", "\"" + format + "\""));
        var item = AniListProvider.MapMedia(json.RootElement); Assert.Equal(expected, item.Kind); Assert.Equal(format, item.Format);
    }
    [Theory] [InlineData("https://anilist.co/anime/2167",false)] [InlineData("https://myanimelist.net/anime/2167/Clannad",true)]
    public void CanonicalExternalIdsAreTyped(string url, bool mal) { Assert.True(AniListReference.TryParse(url, out var reference)); Assert.Equal(2167, reference!.Id); Assert.Equal(mal, reference.MyAnimeList); }
    [Theory] [InlineData("https://anilist.co.evil/anime/2167")][InlineData("https://user@anilist.co/anime/2167")][InlineData("http://anilist.co/anime/2167")][InlineData("https://anilist.co/anime/02167")][InlineData("https://anilist.co/anime/2167?token=x")][InlineData("https://anilist.co/anime/2167/a/b")]
    public void SpoofedOrAmbiguousIdentityLinksReject(string url) => Assert.False(AniListReference.TryParse(url, out _));
    [Theory] [InlineData("https://s4.anilist.co/file/anilistcdn/media/x.jpg",true)][InlineData("https://s4.anilist.co.evil/file/anilistcdn/x.jpg",false)][InlineData("https://s4.anilist.co/file/anilistcdn/x.jpg?secret=1",false)][InlineData("https://s4.anilist.co/other/x.jpg",false)]
    public void ImagesOnlyUseCanonicalPublicAniListCdn(string url, bool allowed) => Assert.Equal(allowed, AniListValidation.Image(url));
    [Fact] public void InconsistentFormatYearAndExternalIdAreConflictsRatherThanHighScores()
    {
        var source = AniListTestData.Source(); var item = AniListTestData.Media(); Assert.False(AniListMatching.Assess(source,item).Conflict);
        Assert.True(AniListMatching.Assess(source,item with { Format = "MOVIE" }).Conflict);
        Assert.True(AniListMatching.Assess(source,item with { StartDate = "2008-10-03" }).Conflict);
        Assert.True(AniListMatching.Assess(source with { ExternalLinks = ["https://anilist.co/anime/4181"] },item).Conflict);
        Assert.True(AniListMatching.Assess(source,item with { Kind = AniListMediaKind.Manga }).Conflict);
    }
    [Fact] public void InfoboxUsesCompleteExplicitUrlsNotTruncatedOrTitleDerivedIds()
    {
        using var json = JsonDocument.Parse("""{"infobox":[{"key":"title","value":"Clannad 2167"},{"key":"url","value":"https://anilist.co/anime/2167?spoof=1"},{"key":"url","value":"https://myanimelist.net/anime/2167/Clannad"},{"key":"url","value":"https://anilist.co/manga/2167"}]}""");
        Assert.Equal(["https://myanimelist.net/anime/2167"], AniListBridgeLinks.Map(json.RootElement, BangumiRelationKind.Anime));
    }
    [Fact] public async Task ReadOnlyGraphqlUsesVariablesAndNoOtherProviderCredentials()
    {
        using var handler = new AniListTestData.Handler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(AniListProvider.Endpoint, request.RequestUri!.ToString().TrimEnd('/'));
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.Contains("TheOrder", request.Headers.UserAgent.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); Assert.Equal("name\" } mutation", body.RootElement.GetProperty("variables").GetProperty("search").GetString());
            Assert.StartsWith("query", body.RootElement.GetProperty("query").GetString()); return AniListTestData.Response("{\"data\":{\"Page\":{\"media\":[" + AniListTestData.MediaJson + "]}}}");
        });
        using var http = new HttpClient(handler); var result = await new AniListProvider(http,new AniListTestData.Clock().Rate()).SearchAsync(AniListTestData.Source(),"name\" } mutation",default); Assert.Single(result);
    }
    [Fact] public async Task MissingExternalIdUsesBoundedNativeThenDisplayTitleCandidates()
    {
        using var handler = new AniListTestData.Handler((_, _) => Task.FromResult(AniListTestData.Response("{\"data\":{\"Page\":{\"media\":[]}}}")));
        using var http = new HttpClient(handler); Assert.Empty(await new AniListProvider(http,new AniListTestData.Clock().Rate()).SearchAsync(AniListTestData.Source(),null,default)); Assert.Equal(2,handler.Calls);
    }
    [Theory] [InlineData(404,AniListFailure.Unavailable)][InlineData(403,AniListFailure.Server)][InlineData(503,AniListFailure.Server)]
    public async Task HttpFailuresRemainDistinguishable(int status, AniListFailure failure)
    {
        using var handler = new AniListTestData.Handler((_, _) => Task.FromResult(AniListTestData.Response("{}",(HttpStatusCode)status))); using var http = new HttpClient(handler);
        Assert.Equal(failure,(await Assert.ThrowsAsync<AniListException>(()=>new AniListProvider(http,new AniListTestData.Clock().Rate()).FetchAsync(2167,AniListMediaKind.Anime,1,default))).Failure);
    }
    [Fact] public async Task GraphqlNotFoundIsNotAnEmptySuccessfulDetail()
    {
        using var handler = new AniListTestData.Handler((_, _) => Task.FromResult(AniListTestData.Response("{\"data\":{\"Media\":null},\"errors\":[{\"status\":404,\"message\":\"Not found\"}]}"))); using var http = new HttpClient(handler);
        Assert.Equal(AniListFailure.Unavailable,(await Assert.ThrowsAsync<AniListException>(()=>new AniListProvider(http).FetchAsync(2167,AniListMediaKind.Anime,1,default))).Failure);
    }
    [Fact] public async Task EveryProviderAnd429RetryShareServerCooldownAndThirtyPerMinuteSpacing()
    {
        var time = new AniListTestData.Clock(); var rate = time.Rate(); var starts = new List<long>(); int calls=0;
        using var handler = new AniListTestData.Handler((_, _) =>
        {
            starts.Add(time.GetTimestamp()); var response = AniListTestData.Response("{\"data\":{\"Media\":" + AniListTestData.DetailJson + "}}",++calls==1?HttpStatusCode.TooManyRequests:HttpStatusCode.OK);
            if(calls==1)response.Headers.RetryAfter=new(TimeSpan.FromSeconds(4)); return Task.FromResult(response);
        }); using var http=new HttpClient(handler); var a=new AniListProvider(http,rate,clock:time.GetUtcNow); var b=new AniListProvider(http,rate,clock:time.GetUtcNow);
        await Task.WhenAll(a.FetchAsync(2167,AniListMediaKind.Anime,1,default),b.FetchAsync(2167,AniListMediaKind.Anime,1,default));
        Assert.Equal(3,starts.Count); Assert.True(starts[1]-starts[0]>=TimeSpan.FromSeconds(4).Ticks); Assert.All(starts.Zip(starts.Skip(1)),pair=>Assert.True(pair.Second-pair.First>=TimeSpan.FromSeconds(2.1).Ticks));
    }
    [Fact] public async Task ExhaustedRemainingHeaderHoldsNextRequestUntilReset()
    {
        var time=new AniListTestData.Clock();var rate=time.Rate(); using(var lease=await rate.EnterAsync(default)) rate.Observe(10,0,time.Now.AddSeconds(20),null);
        using(var lease=await rate.EnterAsync(default))Assert.True(time.Now>=AniListTestData.Stamp.AddSeconds(20));
    }
    [Fact] public async Task CancellationAndOperationTimeoutNeverBecomeEmptyResults()
    {
        using var handler=new AniListTestData.Handler(async(_,token)=>{await Task.Delay(10000,token);return AniListTestData.Response("{}");});using var http=new HttpClient(handler);
        var provider=new AniListProvider(http,new AniListTestData.Clock().Rate(),TimeSpan.FromMilliseconds(30));
        Assert.Equal(AniListFailure.Timeout,(await Assert.ThrowsAsync<AniListException>(()=>provider.FetchAsync(2167,AniListMediaKind.Anime,1,default))).Failure);
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>provider.FetchAsync(2167,AniListMediaKind.Anime,1,cancel.Token));
    }
    [Fact] public async Task WrongMediaIdentityAndTypeAreRejected()
    {
        using var handler=new AniListTestData.Handler((_,_)=>Task.FromResult(AniListTestData.Response("{\"data\":{\"Media\":"+AniListTestData.DetailJson.Replace("\"id\":2167","\"id\":4181")+"}}")));using var http=new HttpClient(handler);
        Assert.Equal(AniListFailure.InvalidResponse,(await Assert.ThrowsAsync<AniListException>(()=>new AniListProvider(http,new AniListTestData.Clock().Rate()).FetchAsync(2167,AniListMediaKind.Anime,1,default))).Failure);
    }
    [Fact] public void CachePayloadMustAgreeWithItsTypedKeyAndNullBindingEvidenceRejects()
    {
        var details=AniListTestData.Details();Assert.False(AniListValidation.Cache(new("media:0:4181:1",AniListTestData.Stamp,Details:details)));
        Assert.False(AniListValidation.Cache(new("binding:x",AniListTestData.Stamp,Binding:new(null!,2167,AniListMediaKind.Anime,"确认"))));
    }
    [Fact] public async Task AniListArtworkUsesCredentialFreePublicRouteOnlyForVerifiedCdn()
    {
        using var anonymous=new AniListTestData.Handler((request,_)=>{Assert.Null(request.Headers.Authorization);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([1]){Headers={ContentType=new("image/jpeg")}}});});
        using var checkedRoute=new AniListTestData.Handler((_,_)=>throw new InvalidOperationException("Wrong image transport"));using var publicClient=new HttpClient(anonymous);using var normal=new HttpClient(checkedRoute);
        var source=new RemoteCoverSource(normal,originalArtwork:publicClient);Assert.NotNull(await source.ReadAsync(source.Register("https://s4.anilist.co/file/anilistcdn/character/x.jpg","test")!,default));Assert.Equal(1,anonymous.Calls);
    }
}
