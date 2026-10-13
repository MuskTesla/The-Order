using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;
public sealed class P1DirectMetadataTests
{
    private const string Bangumi = """{"id":42,"type":4,"name":"Original","name_cn":"中文名称","summary":"简介","date":"2000-09-08","images":{"large":"https://example.com/cover.png"},"infobox":[{"key":"开发","value":"开发商"}],"tags":[{"name":"剧情"}],"rating":{"score":8,"total":10}}""";
    private const string Vndb = """{"results":[{"id":"v42","title":"Original","alttitle":"原名","released":"2000-09","description":"简介","rating":80,"votecount":10,"image":{"url":"https://example.com/adult.png","sexual":2},"developers":[{"name":"Studio"}],"tags":[{"name":"safe","spoiler":0},{"name":"spoiler","spoiler":2}],"va":[{"character":{"id":"c1","name":"Alice","original":"原名","image":{"url":"https://example.com/c.png","sexual":1}},"staff":{"name":"Voice"}}]}]}""";
    [Theory][InlineData("bangumi", "https://bgm.tv/subject/42", "42")][InlineData("bangumi", "https://bangumi.tv/subject/42/", "42")][InlineData("vndb", "42", "v42")][InlineData("vndb", "https://vndb.org/v42", "v42")]
    public void ExactIdCanBeEnteredAsOfficialLink(string source, string input, string expected) => Assert.Equal(expected, DirectMetadataIdentity.Normalize(source, input));
    [Theory][InlineData("bangumi", "https://evil.example/subject/42")][InlineData("bangumi", "http://bgm.tv/subject/42")][InlineData("vndb", "https://vndb.org/v42?token=secret")][InlineData("vndb", "r42")][InlineData("vndb", "0")][InlineData("unknown", "42")]
    public void UnsafeOrWrongKindIdsNeverCreateRequests(string source, string input) => Assert.Throws<InvalidDataException>(() => DirectMetadataIdentity.Normalize(source, input));
    [Fact] public void SourcesMapRatingsDatesAndHiddenImagesCorrectly()
    {
        using var b = JsonDocument.Parse(Bangumi); var bm = DirectMetadataSources.Map("bangumi", "42", b.RootElement, false); Assert.Equal("中文名称", bm.Title); Assert.Equal(10, bm.Ratings[0].Scale); Assert.Equal("开发商", bm.Developers[0]);
        using var v = JsonDocument.Parse(Vndb); var vn = DirectMetadataSources.Map("vndb", "v42", v.RootElement, false); Assert.Equal("month", vn.ReleaseDatePrecision); Assert.Equal(100, vn.Ratings[0].Scale); Assert.Null(vn.CoverUrl); Assert.Null(vn.Characters[0].ImageUrl); Assert.Equal("Voice", vn.Characters[0].VoiceActors[0]); Assert.Equal("safe", Assert.Single(vn.Tags));
        Assert.NotNull(DirectMetadataSources.Map("vndb", "v42", v.RootElement, true).CoverUrl);
    }
    [Fact] public void DifferentNumberNonGameAndAdultPolicyAreNotEmptySuccess()
    { using var json = JsonDocument.Parse(Bangumi); Assert.Throws<InvalidDataException>(() => DirectMetadataSources.Map("bangumi", "43", json.RootElement, true)); using var nongame = JsonDocument.Parse(Bangumi.Replace("\"type\":4", "\"type\":2")); Assert.Throws<InvalidDataException>(() => DirectMetadataSources.Map("bangumi", "42", nongame.RootElement, true)); using var adult = JsonDocument.Parse(Bangumi.Replace("\"type\":4", "\"nsfw\":true,\"type\":4")); Assert.Throws<InvalidDataException>(() => DirectMetadataSources.Map("bangumi", "42", adult.RootElement, false)); }
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string? Url, Method, Payload; public bool Authorized;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; Url = request.RequestUri!.ToString(); Method = request.Method.Method; Authorized = request.Headers.Authorization is not null || request.Headers.Contains("Cookie"); Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellation); return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; }
    }
    [Fact] public async Task TransportUsesFixedPublicUrlsAndNoSecrets()
    { using var h = new Handler(Vndb); using var http = new HttpClient(h); var source = new DirectMetadataSources(http); var result = await source.FetchAsync("vndb", "v42", false, default); Assert.Equal("vndb:v42", result.Id); Assert.Equal("https://api.vndb.org/kana/vn", h.Url); Assert.Equal("POST", h.Method); Assert.False(h.Authorized); using var body = JsonDocument.Parse(h.Payload!); Assert.Equal("v42", body.RootElement.GetProperty("filters")[2].GetString()); }
    [Theory][InlineData(404)][InlineData(401)][InlineData(429)][InlineData(500)]
    public async Task NetworkFailuresAreActionableAndNeverBlankWorks(int status)
    { using var h = new Handler("{}", (HttpStatusCode)status); using var http = new HttpClient(h); var source = new DirectMetadataSources(http); var e = await Assert.ThrowsAsync<InvalidDataException>(() => source.FetchAsync("bangumi", "42", false, default)); Assert.NotEmpty(e.Message); Assert.Equal(1, h.Calls); }
    [Fact] public async Task OversizeAndCancellationDoNotPublishMetadata()
    { using var h = new Handler(new string(' ', 2097153)); using var http = new HttpClient(h); var source = new DirectMetadataSources(http); await Assert.ThrowsAsync<InvalidDataException>(() => source.FetchAsync("bangumi", "42", false, default)); using var stop = new CancellationTokenSource(); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync("bangumi", "42", false, stop.Token)); Assert.Equal(1, h.Calls); }
}
