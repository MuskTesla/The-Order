using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiSubjectDetailsTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private const string Json = """{"id":51,"type":2,"name":"CLANNAD","name_cn":"克兰纳德","summary":"line 1\nline 2","nsfw":false,"date":"2007-10-04","platform":"TV","eps":22,"rating":{"score":8.4,"total":120},"images":{"large":"https://lain.bgm.tv/pic/cover/l/test.jpg"}}""";
    private static BangumiSubjectDetails Sample(string id = "51", BangumiRelationKind kind = BangumiRelationKind.Anime, DateTimeOffset? stamp = null)
        => new(id, kind, "作品", "Original", null, "简介", null, null, null, null, null, stamp ?? Stamp);
    private static BangumiSubjectDetails Map(string json = Json)
    { using var document = JsonDocument.Parse(json); return BangumiSubjectDetailsProvider.Map("51", BangumiRelationKind.Anime, document.RootElement, Stamp); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("TheOrder", request.Headers.UserAgent.ToString()); Urls.Add(request.RequestUri!.AbsoluteUri);
            return send(request, cancellation);
        }
    }
    private static HttpResponseMessage Response(string value = Json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Provider(Func<string, BangumiRelationKind, CancellationToken, Task<BangumiSubjectDetails>> action) : IBangumiSubjectDetailsProvider
    {
        public int Calls;
        public Task<BangumiSubjectDetails> FetchAsync(string id, BangumiRelationKind kind, CancellationToken token)
        { Calls++; return action(id, kind, token); }
    }

    [Fact]
    public void PublicFactsUseActualTypeAndPreservePlainTextSummary()
    {
        var subject = Map(); Assert.Equal("51", subject.Id); Assert.Equal(BangumiRelationKind.Anime, subject.Kind);
        Assert.Equal("克兰纳德", subject.Title); Assert.Equal("CLANNAD", subject.OriginalTitle); Assert.Equal("line 1\nline 2", subject.Summary);
        Assert.Equal("2007-10-04", subject.ReleaseDate); Assert.Equal("TV", subject.Platform); Assert.Equal(22, subject.EpisodeCount);
        Assert.Equal(8.4, subject.Score); Assert.Equal(120, subject.RatingCount); Assert.Equal("https://bgm.tv/subject/51", subject.Url);
    }
    [Theory]
    [InlineData("\"id\":51", "\"id\":52")]
    [InlineData("\"type\":2", "\"type\":4")]
    [InlineData("\"nsfw\":false", "\"nsfw\":null")]
    [InlineData("\"nsfw\":false,", "")]
    [InlineData("\"name\":\"CLANNAD\"", "\"name\":17")]
    [InlineData("line 1\\nline 2", "line 1\\u0000line 2")]
    public void WrongIdentityTypeAndMalformedRequiredFactsReject(string before, string after)
        => Assert.Equal(BangumiRelationsFailure.InvalidResponse, Assert.Throws<BangumiRelationsException>(() => Map(Json.Replace(before, after))).Failure);
    [Fact] public void AdultSubjectIsUnavailableRatherThanDisplayed()
        => Assert.Equal(BangumiRelationsFailure.Unavailable, Assert.Throws<BangumiRelationsException>(() => Map(Json.Replace("\"nsfw\":false", "\"nsfw\":true"))).Failure);
    [Fact] public void MissingOrUnreliableOptionalFactsAreOmitted()
    {
        var value = Map(Json.Replace("2007-10-04", "unknown").Replace("\"eps\":22", "\"eps\":0").Replace("\"total\":120", "\"total\":0")
            .Replace("https://lain.bgm.tv/pic/cover/l/test.jpg", "https://evil.example/image.jpg"));
        Assert.Null(value.ReleaseDate); Assert.Null(value.EpisodeCount); Assert.Null(value.Score); Assert.Null(value.RatingCount); Assert.Null(value.CoverUrl);
    }
    [Fact] public void SummaryIsBounded()
        => Assert.Throws<BangumiRelationsException>(() => Map(Json.Replace("line 1\\nline 2", new string('x', BangumiSubjectDetailsService.MaximumSummaryLength + 1))));
    [Fact] public async Task FetchUsesOnlyCanonicalAnonymousSubjectGet()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var client = new HttpClient(handler);
        var result = await new BangumiSubjectDetailsProvider(client, () => Stamp).FetchAsync("51", BangumiRelationKind.Anime, default);
        Assert.Equal("51", result.Id); Assert.Equal(["https://api.bgm.tv/v0/subjects/51"], handler.Urls);
    }
    [Theory] [InlineData("../51")][InlineData("051")][InlineData("https://bgm.tv/subject/51")]
    public async Task BadIdNeverMakesARequest(string id)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BangumiSubjectDetailsProvider(client).FetchAsync(id, BangumiRelationKind.Anime, default)); Assert.Empty(handler.Urls);
    }
    [Theory] [InlineData(false)][InlineData(true)]
    public async Task SecretsOnSuppliedClientPreventNetworkUse(bool cookie)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var client = new HttpClient(handler);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", "synthetic=test"); else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic");
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiSubjectDetailsProvider(client).FetchAsync("51", BangumiRelationKind.Anime, default)); Assert.Empty(handler.Urls);
    }
    [Theory] [InlineData(401)][InlineData(403)][InlineData(404)]
    public async Task UnavailableIsNotAnEmptyDetail(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("{}", (HttpStatusCode)status))); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiSubjectDetailsProvider(client).FetchAsync("51", BangumiRelationKind.Anime, default));
        Assert.Equal(BangumiRelationsFailure.Unavailable, error.Failure); Assert.Single(handler.Urls);
    }
    [Fact] public async Task ResponseLimitAndWrongContentTypeAreRejected()
    {
        var large = new Handler((_, _) => Task.FromResult(Response(new string('x', BangumiSubjectDetailsProvider.MaximumResponseBytes + 1))));
        using var client = new HttpClient(large);
        Assert.Equal(BangumiRelationsFailure.ResponseTooLarge, (await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiSubjectDetailsProvider(client).FetchAsync("51", BangumiRelationKind.Anime, default))).Failure);
        var html = new Handler((_, _) => { var response = Response(); response.Content.Headers.ContentType = new("text/html"); return Task.FromResult(response); });
        using var other = new HttpClient(html);
        Assert.Equal(BangumiRelationsFailure.InvalidResponse, (await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiSubjectDetailsProvider(other).FetchAsync("51", BangumiRelationKind.Anime, default))).Failure);
    }
    [Fact] public async Task CancellationAndTimeoutAreDistinct()
    {
        var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(); }); using var client = new HttpClient(handler);
        var provider = new BangumiSubjectDetailsProvider(client, operationTimeout: TimeSpan.FromMilliseconds(40));
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("51", BangumiRelationKind.Anime, default)); Assert.Equal(BangumiRelationsFailure.Timeout, error.Failure);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync("51", BangumiRelationKind.Anime, cancelled.Token));
    }
    [Fact] public async Task RetryBudgetIsFiniteAndHonorsRateLimit()
    {
        var now = Stamp; int attempts = 0;
        var handler = new Handler((_, _) => { attempts++; var response = Response("{}", HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); return Task.FromResult(response); });
        using var client = new HttpClient(handler);
        var provider = new BangumiSubjectDetailsProvider(client, () => now, (wait, token) => { token.ThrowIfCancellationRequested(); now += wait; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("51", BangumiRelationKind.Anime, default)); Assert.Equal(BangumiRelationsFailure.RateLimited, error.Failure); Assert.Equal(1, attempts);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("51", BangumiRelationKind.Anime, default)); Assert.Equal(1, attempts);
    }
    [Fact] public async Task FreshCacheAvoidsNetworkAndCacheIsBounded()
    {
        var provider = new Provider((id, kind, _) => Task.FromResult(Sample(id, kind))); var service = new BangumiSubjectDetailsService(provider, () => Stamp);
        await service.LoadAsync("51", BangumiRelationKind.Anime, false, default);
        Assert.True((await service.LoadAsync("51", BangumiRelationKind.Anime, false, default)).FromCache); Assert.Equal(1, provider.Calls);
        for (int id = 100; id < 100 + BangumiSubjectDetailsService.MaximumEntries; id++) await service.LoadAsync(id.ToString(), BangumiRelationKind.Book, false, default);
        Assert.False((await service.LoadAsync("51", BangumiRelationKind.Anime, false, default)).FromCache); Assert.Equal(66, provider.Calls);
    }
    [Theory] [InlineData(BangumiRelationsFailure.Offline)][InlineData(BangumiRelationsFailure.Timeout)][InlineData(BangumiRelationsFailure.Server)][InlineData(BangumiRelationsFailure.RateLimited)]
    public async Task TransientFailureKeepsEarlierPublicDetail(BangumiRelationsFailure failure)
    {
        var now = Stamp; bool fail = false;
        var provider = new Provider((id, kind, _) => fail ? throw new BangumiRelationsException(failure, "网络故障") : Task.FromResult(Sample(id, kind)));
        var service = new BangumiSubjectDetailsService(provider, () => now); await service.LoadAsync("51", BangumiRelationKind.Anime, false, default);
        now += TimeSpan.FromHours(2); fail = true;
        var cached = await service.LoadAsync("51", BangumiRelationKind.Anime, false, default); Assert.True(cached.FromCache); Assert.True(cached.IsStale); Assert.NotNull(cached.Warning);
    }
    [Theory] [InlineData(BangumiRelationsFailure.Unavailable)][InlineData(BangumiRelationsFailure.InvalidResponse)][InlineData(BangumiRelationsFailure.ResponseTooLarge)]
    public async Task AccessAndIdentityRejectionEvictEarlierDetail(BangumiRelationsFailure failure)
    {
        bool fail = false; var provider = new Provider((id, kind, _) => fail ? throw new BangumiRelationsException(failure, "不可读取") : Task.FromResult(Sample(id, kind)));
        var service = new BangumiSubjectDetailsService(provider, () => Stamp); await service.LoadAsync("51", BangumiRelationKind.Anime, false, default); fail = true;
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", BangumiRelationKind.Anime, true, default));
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", BangumiRelationKind.Anime, false, default)); Assert.Equal(3, provider.Calls);
    }
    [Fact] public async Task NewerRefreshWinsCacheEvenWhenOldRequestIgnoresCancellation()
    {
        var gate = new TaskCompletionSource<BangumiSubjectDetails>(TaskCreationOptions.RunContinuationsAsynchronously); int count = 0;
        var provider = new Provider((id, kind, _) => ++count == 1 ? gate.Task : Task.FromResult(Sample(id, kind) with { Title = "new" }));
        var service = new BangumiSubjectDetailsService(provider, () => Stamp); var old = service.LoadAsync("51", BangumiRelationKind.Anime, true, default);
        await service.LoadAsync("51", BangumiRelationKind.Anime, true, default); gate.SetResult(Sample() with { Title = "old" }); await old;
        Assert.Equal("new", (await service.LoadAsync("51", BangumiRelationKind.Anime, false, default)).Details.Title);
    }
    [Fact] public async Task CancelledReadDoesNotWriteCacheOrReturnFallback()
    {
        var gate = new TaskCompletionSource<BangumiSubjectDetails>(TaskCreationOptions.RunContinuationsAsynchronously); var provider = new Provider((_, _, _) => gate.Task);
        var service = new BangumiSubjectDetailsService(provider, () => Stamp); using var cancel = new CancellationTokenSource();
        var read = service.LoadAsync("51", BangumiRelationKind.Anime, false, cancel.Token); cancel.Cancel(); gate.SetResult(Sample());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False((await service.LoadAsync("51", BangumiRelationKind.Anime, false, default)).FromCache); Assert.Equal(2, provider.Calls);
    }
    [Fact] public async Task VeryOldMemoryEntryDoesNotPretendToBeAnOfflineResult()
    {
        var now = Stamp; bool fail = false;
        var provider = new Provider((id, kind, _) => fail ? throw new BangumiRelationsException(BangumiRelationsFailure.Offline, "离线") : Task.FromResult(Sample(id, kind)));
        var service = new BangumiSubjectDetailsService(provider, () => now); await service.LoadAsync("51", BangumiRelationKind.Anime, false, default);
        now += TimeSpan.FromDays(8); fail = true;
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", BangumiRelationKind.Anime, false, default));
    }
    [Fact] public async Task MediaIdentityCacheEntriesDoNotCollide()
    {
        var provider = new Provider((id, kind, _) => kind == BangumiRelationKind.Anime ? Task.FromResult(Sample(id, kind))
            : throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "类型不匹配"));
        var service = new BangumiSubjectDetailsService(provider, () => Stamp); await service.LoadAsync("51", BangumiRelationKind.Anime, false, default);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", BangumiRelationKind.Book, false, default));
        Assert.True((await service.LoadAsync("51", BangumiRelationKind.Anime, false, default)).FromCache);
    }
    [Fact] public async Task ServerRetriesStopAtThreeAttemptsWithinSharedDeadline()
    {
        var now = Stamp;
        var handler = new Handler((_, _) => Task.FromResult(Response("{}", HttpStatusCode.ServiceUnavailable))); using var client = new HttpClient(handler);
        var provider = new BangumiSubjectDetailsProvider(client, () => now, (wait, token) => { token.ThrowIfCancellationRequested(); now += wait; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("51", BangumiRelationKind.Anime, default));
        Assert.Equal(BangumiRelationsFailure.Server, error.Failure); Assert.Equal(3, handler.Urls.Count); Assert.True(now - Stamp < TimeSpan.FromSeconds(25));
    }
}
