using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class BangumiRelationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private const string Source = """{"id":13,"type":4,"nsfw":false}""";
    private const string Rows = """[{"id":51,"type":2,"name":"CLANNAD -クラナド-","name_cn":"CLANNAD","relation":"动画","images":{"medium":"https://lain.bgm.tv/r/800/pic/cover/l/ab/cd/51.jpg"}},{"id":616,"type":3,"name":"CLANNAD Original SoundTrack","name_cn":"","relation":"原声集"},{"id":149014,"type":1,"name":"Comic","name_cn":"漫画版","relation":"书籍"},{"id":7970,"type":4,"name":"Side Story","name_cn":"外传","relation":"外传"}]""";
    private static LibraryEntry Game(params WorkReference[] refs) => Entry() with { Metadata = Work("7") with { References = [.. refs] } };
    private static BangumiRelationIdentity Identity(string id = "13", string lifecycle = "life")
    {
        Assert.True(BangumiRelationIdentityResolver.TryCreate(Game(new WorkReference("bangumi", id, "https://bgm.tv/subject/" + id)), lifecycle, out var result, out _));
        return result!;
    }
    private static BangumiRelationsSnapshot Snapshot(string title = "Animation", DateTimeOffset? time = null) => new("13", time ?? Now,
        [new("51", BangumiRelationKind.Anime, title, "Original", "动画", null)]);
    private static HttpResponseMessage Response(string text, int status = 200, string? retry = null)
    {
        var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        if (retry is not null) result.Headers.TryAddWithoutValidation("Retry-After", retry);
        return result;
    }
    private sealed class Handler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> Agents { get; } = [];
        public bool SentSecrets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri); Agents.Add(request.Headers.UserAgent.ToString());
            SentSecrets |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            Assert.Equal(HttpMethod.Get, request.Method);
            return action(Urls.Count, request, cancellation);
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }
    private sealed class Time
    {
        public DateTimeOffset Value = Now;
        public List<TimeSpan> Delays { get; } = [];
        public Task Delay(TimeSpan duration, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Delays.Add(duration); Value += duration; return Task.CompletedTask; }
    }
    private sealed class MemoryCache : IBangumiRelationsCache
    {
        public BangumiRelationsSnapshot? Saved;
        public int Reads;
        public List<BangumiRelationsSnapshot> Written { get; } = [];
        public bool FailRead, FailWrite;
        public Task<BangumiRelationsSnapshot?> ReadAsync(BangumiRelationIdentity identity, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Reads++; if (FailRead) throw new IOException(); return Task.FromResult(Saved); }
        public Task WriteAsync(BangumiRelationIdentity identity, BangumiRelationsSnapshot snapshot, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (FailWrite) throw new IOException(); Written.Add(snapshot); Saved = snapshot; return Task.CompletedTask; }
    }
    private sealed class Provider(Func<string, CancellationToken, Task<BangumiRelationsSnapshot>> action) : IBangumiRelationsProvider
    {
        public int Calls;
        public Task<BangumiRelationsSnapshot> FetchAsync(string subjectId, CancellationToken cancellation)
        { Calls++; return action(subjectId, cancellation); }
    }

    [Theory]
    [InlineData("13")][InlineData("https://bgm.tv/subject/13")][InlineData("https://bangumi.tv/subject/13/")][InlineData("https://chii.in/subject/13")]
    public void ExactOfficialReferencesNormalize(string input)
    { Assert.True(BangumiRelationIdentityResolver.TryReferenceId(input, out var id)); Assert.Equal("13", id); }
    [Theory]
    [InlineData("0")][InlineData("013")][InlineData("-13")][InlineData("13 ")][InlineData("１3")][InlineData("10000000000")]
    [InlineData("http://bgm.tv/subject/13")][InlineData("https://evil.example/subject/13")][InlineData("https://bgm.tv/character/13")]
    [InlineData("https://bgm.tv/subject/13?x=1")][InlineData("https://bgm.tv/subject/13#x")][InlineData("https://u@bgm.tv/subject/13")]
    [InlineData("https://bgm.tv:444/subject/13")][InlineData("https://bgm.tv/subject/42/../13")][InlineData("https://bgm.tv/subject/%31%33")]
    public void UnreliableIdentifiersAreRejected(string input) => Assert.False(BangumiRelationIdentityResolver.TryReferenceId(input, out _));
    [Fact] public void DuplicateSameSubjectIsSafeButConflictingIdsOrLinksAbstain()
    {
        Assert.True(BangumiRelationIdentityResolver.TryCreate(Game(new WorkReference("bangumi", "13", null), new WorkReference("bangumi", "https://chii.in/subject/13", "https://bgm.tv/subject/13")), "life", out var same, out _));
        Assert.Equal("13", same!.SubjectId);
        foreach (var game in new[] { Game(new WorkReference("bangumi", "13", null), new WorkReference("bangumi", "14", null)), Game(new WorkReference("bangumi", "13", "https://bgm.tv/subject/14")), Game(new WorkReference("bangumi", "13", "https://evil.example/subject/13")), Game() })
        { Assert.False(BangumiRelationIdentityResolver.TryCreate(game, "life", out var missing, out var reason)); Assert.Null(missing); Assert.NotEmpty(reason); }
    }
    [Fact] public void ManualLayerDoesNotBecomeNextMoeAuthority()
    {
        var basis = Work("7") with { References = [] };
        var direct = Work("bangumi:13") with { References = [new("bangumi", "13", "https://bgm.tv/subject/13")] };
        var mix = new MetadataMix(basis, [new("bangumi", "13", direct, ["description"], true)]);
        var game = Entry() with { MetadataMix = mix, Metadata = MetadataMixing.Compose(mix) };
        Assert.False(BangumiRelationIdentityResolver.TryCreate(game, "life", out _, out _));
        mix = mix with { Base = null };
        Assert.False(BangumiRelationIdentityResolver.TryCreate(game with { MetadataMix = mix, Metadata = MetadataMixing.Compose(mix) }, "life", out _, out _));
        Assert.False(BangumiRelationIdentityResolver.TryCreate(Entry() with { Metadata = direct }, "life", out _, out _));
    }
    [Fact] public void IdentityChangesWithSourceAndLifecycleButCacheSurvivesRestart()
    {
        var a = Identity(); var restart = Identity(lifecycle: "next-life"); var b = Identity("14");
        Assert.NotEqual(a, restart); Assert.Equal(a.CacheKey, restart.CacheKey); Assert.NotEqual(a.CacheKey, b.CacheKey);
        var refs = new[] { new WorkReference("bangumi", "13", null), new WorkReference("bangumi", "13", "https://bgm.tv/subject/13") };
        Assert.True(BangumiRelationIdentityResolver.TryCreate(Game(refs), "life", out var ordered, out _));
        Assert.True(BangumiRelationIdentityResolver.TryCreate(Game(refs.Reverse().ToArray()), "life", out var reversed, out _));
        Assert.Equal(ordered, reversed);
    }
    [Fact] public void CatalogAnchorWorksWithoutShelfEntryAndRetainsExactReferenceChecks()
    {
        var work = Work("3407") with { References = [new("bangumi", "3660", "https://bgm.tv/subject/3660")] };
        Assert.True(BangumiRelationIdentityResolver.TryCreateCatalog(work, "browse:1", out var first, out _));
        Assert.True(BangumiRelationsService.ValidIdentity(first!));
        Assert.Equal("catalog:3407", first!.LocalGameId);
        Assert.Equal("3660", first.SubjectId);
        Assert.True(BangumiRelationIdentityResolver.TryCreateCatalog(work, "browse:3", out var returning, out _));
        Assert.NotEqual(first, returning); Assert.Equal(first.CacheKey, returning!.CacheKey);
        Assert.True(BangumiRelationIdentityResolver.TryCreate(Entry() with { Metadata = work }, "browse:1", out var local, out _));
        Assert.NotEqual(first.CacheKey, local!.CacheKey);
        foreach (var invalid in new WorkMetadata?[] { null, work with { Id = "bangumi:3660" }, work with { References = [] },
            work with { References = [new("bangumi", "3660", "https://bgm.tv/subject/13")] },
            work with { References = [new("bangumi", "3660", null), new("bangumi", "13", null)] } })
        {
            Assert.False(BangumiRelationIdentityResolver.TryCreateCatalog(invalid, "browse:1", out var missing, out var reason));
            Assert.Null(missing); Assert.NotEmpty(reason);
        }
        Assert.False(BangumiRelationIdentityResolver.TryCreateCatalog(work, "", out _, out _));
    }
    [Fact] public void MapsFourKindsAndPreservesRemoteRelationshipDirection()
    {
        using var doc = JsonDocument.Parse(Rows); var mapped = BangumiRelationsProvider.MapRelations("13", doc.RootElement, Now);
        Assert.Equal(new[] { BangumiRelationKind.Anime, BangumiRelationKind.Music, BangumiRelationKind.Book, BangumiRelationKind.Game }, mapped.Items.Select(i => i.Kind));
        Assert.Equal("动画", mapped.Items[0].Relation); Assert.Equal("CLANNAD", mapped.Items[0].Title); Assert.Equal("CLANNAD -クラナド-", mapped.Items[0].OriginalTitle);
        Assert.Equal("CLANNAD Original SoundTrack", mapped.Items[1].Title); Assert.Equal("https://bgm.tv/subject/51", mapped.Items[0].Url);
        Assert.True(BangumiRelationsService.Valid(Identity(), mapped));
    }
    [Fact] public void StableDedupIgnoresSelfAndReportsUnsupportedTypes()
    {
        using var doc = JsonDocument.Parse("""[{"id":51,"type":2,"name":"First","name_cn":"","relation":"动画"},{"id":51,"type":2,"name":"Later","name_cn":"","relation":"其他"},{"id":13,"type":4,"name":"Self","name_cn":"","relation":"其他"},{"id":99,"type":6,"name":"Film","name_cn":"","relation":"不同演绎"}]""");
        var result = BangumiRelationsProvider.MapRelations("13", doc.RootElement, Now);
        Assert.Equal("First", Assert.Single(result.Items).Title); Assert.Equal(1, result.UnsupportedCount);
    }
    [Theory]
    [InlineData("{}")][InlineData("[null]")][InlineData("[{\"id\":51,\"type\":2}]")]
    [InlineData("[{\"id\":\"51\",\"type\":2,\"name\":\"x\",\"name_cn\":\"\",\"relation\":\"x\"}]")]
    [InlineData("[{\"id\":51,\"type\":2,\"name\":\"\",\"name_cn\":\"\",\"relation\":\"x\"}]")]
    [InlineData("[{\"id\":51,\"type\":2,\"name\":\"x\",\"name_cn\":\"\",\"relation\":\"x\"},{\"id\":51,\"type\":4,\"name\":\"x\",\"name_cn\":\"\",\"relation\":\"x\"}]")]
    public void MalformedRelationsNeverBecomeAnEmptySuccess(string body)
    { using var doc = JsonDocument.Parse(body); Assert.Throws<BangumiRelationsException>(() => BangumiRelationsProvider.MapRelations("13", doc.RootElement, Now)); }
    [Fact] public void LargeValidListIsBoundedAndExplicitlyTruncated()
    {
        var rows = Enumerable.Range(100, 300).Select(id => new { id, type = 2, name = "Anime " + id, name_cn = "", relation = "动画" });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rows)); var result = BangumiRelationsProvider.MapRelations("13", doc.RootElement, Now);
        Assert.Equal(256, result.Items.Length); Assert.True(result.Truncated); Assert.Equal("100", result.Items[0].Id);
    }
    [Fact] public void UnsafeImageFailsOnlyTheCover()
    {
        using var doc = JsonDocument.Parse(Rows.Replace("https://lain.bgm.tv/r/800/pic/cover/l/ab/cd/51.jpg", "https://evil.example/pic/cover/a.jpg"));
        var result = BangumiRelationsProvider.MapRelations("13", doc.RootElement, Now); Assert.Equal(4, result.Items.Length); Assert.Null(result.Items[0].CoverUrl);
    }
    [Theory]
    [InlineData("https://lain.bgm.tv/other/image.jpg")][InlineData("http://lain.bgm.tv/pic/cover/a.jpg")]
    [InlineData("https://lain.bgm.tv/pic/cover/a.jpg?secret=1")][InlineData("https://lain.bgm.tv/r/not-a-size/pic/cover/a.jpg")]
    [InlineData("https://lain.bgm.tv/pic/cover/../a.jpg")][InlineData("https://lain.bgm.tv.evil.example/pic/cover/a.jpg")]
    public void OnlyCanonicalBangumiCoverLocationsPass(string url) => Assert.False(BangumiRelationsService.ValidCoverUrl(url));
    [Fact] public async Task TransportChecksSourceBeforeSingleHopAndSendsNoSecrets()
    {
        var time = new Time(); using var handler = new Handler((n, _, _) => Task.FromResult(Response(n == 1 ? Source : Rows))); using var http = new HttpClient(handler);
        var provider = new BangumiRelationsProvider(http, () => time.Value, time.Delay); var result = await provider.FetchAsync("13", default);
        Assert.Equal(4, result.Items.Length); Assert.False(handler.SentSecrets); Assert.All(handler.Agents, a => { Assert.Contains("TheOrder/", a); Assert.Contains("Ph-Rank", a); });
        Assert.Equal(new[] { "https://api.bgm.tv/v0/subjects/13", "https://api.bgm.tv/v0/subjects/13/subjects" }, handler.Urls);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(time.Delays));
    }
    [Theory][InlineData("{\"id\":14,\"type\":4}")][InlineData("{\"id\":13,\"type\":2}")][InlineData("{\"id\":13,\"type\":4,\"nsfw\":true}")]
    public async Task WrongSourceAndUnavailableAdultSourceNeverFetchRelations(string source)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(source))); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default)); Assert.Single(handler.Urls);
    }
    [Theory][InlineData(401)][InlineData(403)][InlineData(404)]
    public async Task AnonymousUnavailableIsNotAnEmptyRelationList(int status)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response("{}", status))); using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default));
        Assert.Equal(BangumiRelationsFailure.Unavailable, failure.Failure); Assert.Single(handler.Urls);
    }
    [Theory][InlineData(500)][InlineData(502)][InlineData(503)][InlineData(504)][InlineData(429)]
    public async Task TemporaryFailuresHaveTwoRetriesAcrossTheOperation(int status)
    {
        var time = new Time(); using var handler = new Handler((_, _, _) => Task.FromResult(Response("{}", status))); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http, () => time.Value, time.Delay).FetchAsync("13", default));
        Assert.Equal(3, handler.Urls.Count);
    }
    [Fact] public async Task SourceAndRelationRequestsShareTheSameRetryAllowance()
    {
        var time = new Time(); using var handler = new Handler((n, _, _) => Task.FromResult(n == 2 ? Response(Source) : Response("{}", 503))); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http, () => time.Value, time.Delay).FetchAsync("13", default));
        Assert.Equal(4, handler.Urls.Count); Assert.Equal(2, handler.Urls.Count(url => url.EndsWith("/subjects", StringComparison.Ordinal)));
    }
    [Fact] public async Task RetryAfterDeltaIsRespectedBeforeSuccess()
    {
        var time = new Time(); using var handler = new Handler((n, _, _) => Task.FromResult(n == 1 ? Response("{}", 429, "4") : Response(n == 2 ? Source : "[]"))); using var http = new HttpClient(handler);
        var result = await new BangumiRelationsProvider(http, () => time.Value, time.Delay).FetchAsync("13", default);
        Assert.Empty(result.Items); Assert.Equal(TimeSpan.FromSeconds(4), time.Delays[0]); Assert.Equal(3, handler.Urls.Count);
    }
    [Fact] public async Task LongRetryAfterDateDoesNotSleepPastBudgetOrHammerAgain()
    {
        var time = new Time(); using var handler = new Handler((_, _, _) => Task.FromResult(Response("{}", 429, Now.AddMinutes(2).ToString("r")))); using var http = new HttpClient(handler);
        var provider = new BangumiRelationsProvider(http, () => time.Value, time.Delay);
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("13", default));
        Assert.Equal(BangumiRelationsFailure.RateLimited, error.Failure); Assert.Equal(TimeSpan.FromMinutes(2), error.RetryAfter);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("13", default)); Assert.Single(handler.Urls); Assert.Empty(time.Delays);
    }
    [Fact] public async Task QueueWaitIsInsideTotalTimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, _, _) => { entered.TrySetResult(); await release.Task; return Response(Source); }); using var http = new HttpClient(handler);
        var provider = new BangumiRelationsProvider(http, operationTimeout: TimeSpan.FromMilliseconds(40));
        var first = provider.FetchAsync("13", default); await entered.Task;
        var secondFailure = await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.FetchAsync("14", default));
        Assert.Equal(BangumiRelationsFailure.Timeout, secondFailure.Failure); Assert.Single(handler.Urls);
        release.SetResult(); var firstFailure = await Assert.ThrowsAsync<BangumiRelationsException>(() => first); Assert.Equal(BangumiRelationsFailure.Timeout, firstFailure.Failure);
    }
    [Fact] public async Task CallerCancellationDuringRetryRemainsCancellation()
    {
        using var stop = new CancellationTokenSource(); using var handler = new Handler((_, _, _) => Task.FromResult(Response("{}", 503))); using var http = new HttpClient(handler);
        Task Delay(TimeSpan _, CancellationToken token) { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BangumiRelationsProvider(http, delay: Delay).FetchAsync("13", stop.Token)); Assert.Single(handler.Urls);
    }
    [Fact] public async Task CancellationBeforeStartAndContaminatedClientSendNothing()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(Source))); using var http = new HttpClient(handler); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BangumiRelationsProvider(http).FetchAsync("13", stop.Token));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-only");
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default)); Assert.Empty(handler.Urls);
    }
    [Theory][InlineData("not json")][InlineData("{\"id\":13,")]
    public async Task InvalidJsonNeverReachesTheRelationsRequest(string body)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(body))); using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default)); Assert.Equal(BangumiRelationsFailure.InvalidResponse, error.Failure); Assert.Single(handler.Urls);
    }
    [Fact] public async Task OversizeResponseIsRejectedBeforeMapping()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Response(new string(' ', BangumiRelationsProvider.MaximumResponseBytes + 1)))); using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default)); Assert.Equal(BangumiRelationsFailure.ResponseTooLarge, error.Failure);
    }
    [Fact] public async Task UnknownContentLengthStillHasAStreamingByteLimit()
    {
        using var handler = new Handler((_, _, _) => {
            var content = new UnknownLengthContent(new byte[BangumiRelationsProvider.MaximumResponseBytes + 1]);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default));
        Assert.Equal(BangumiRelationsFailure.ResponseTooLarge, error.Failure); Assert.Single(handler.Urls);
    }
    [Fact] public async Task RedirectAndUnexpectedContentTypeAreRejected()
    {
        foreach (var redirect in new[] { true, false })
        {
            using var handler = new Handler((_, _, _) => {
                var response = Response(Source, redirect ? 302 : 200);
                if (!redirect) response.Content.Headers.ContentType = new("text/html");
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var error = await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsProvider(http).FetchAsync("13", default));
            Assert.Equal(BangumiRelationsFailure.InvalidResponse, error.Failure); Assert.Single(handler.Urls);
        }
    }
    [Fact] public async Task FreshCacheNeedsNoNetworkAndForceRefreshWritesCurrentSuccess()
    {
        var cache = new MemoryCache { Saved = Snapshot("Old") }; var provider = new Provider((_, _) => Task.FromResult(Snapshot("New"))); var service = new BangumiRelationsService(provider, cache, () => Now);
        var hit = await service.LoadAsync(Identity(), false, default); Assert.True(hit.FromCache); Assert.False(hit.IsStale); Assert.Equal(0, provider.Calls);
        var updated = await service.LoadAsync(Identity(), true, default); Assert.False(updated.FromCache); Assert.Equal("New", updated.Snapshot.Items[0].Title); Assert.Single(cache.Written);
    }
    [Fact] public async Task ExpiredCacheRetainsOldItemsAndTimestampOnTransientRefreshFailure()
    {
        var saved = Snapshot(time: Now.AddHours(-2)); var cache = new MemoryCache { Saved = saved };
        var provider = new Provider((_, _) => throw new BangumiRelationsException(BangumiRelationsFailure.Offline, "暂时离线"));
        var result = await new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), false, default);
        Assert.Same(saved, result.Snapshot); Assert.True(result.FromCache); Assert.True(result.IsStale); Assert.Contains("暂时离线", result.Warning); Assert.Empty(cache.Written);
    }
    [Fact] public async Task FreshCacheRefreshFailureIsClearlyReportedWithoutPretendingItExpired()
    {
        var cache = new MemoryCache { Saved = Snapshot() }; var provider = new Provider((_, _) => throw new BangumiRelationsException(BangumiRelationsFailure.Server, "服务不可用"));
        var result = await new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), true, default);
        Assert.True(result.FromCache); Assert.False(result.IsStale); Assert.NotEmpty(result.Warning!); Assert.Empty(cache.Written);
    }
    [Fact] public async Task WrongIdentityAndFutureCacheAreMissesAndNeverDisplayed()
    {
        foreach (var bad in new[] { Snapshot() with { SubjectId = "14" }, Snapshot(time: Now.AddHours(1)) })
        {
            var cache = new MemoryCache { Saved = bad }; var provider = new Provider((_, _) => Task.FromResult(Snapshot("Live")));
            var result = await new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), false, default);
            Assert.False(result.FromCache); Assert.Equal("Live", result.Snapshot.Items[0].Title); Assert.Equal(1, provider.Calls);
        }
    }
    [Fact] public async Task CacheIoFailureDoesNotDiscardSuccessfulPublicResult()
    {
        var cache = new MemoryCache { FailRead = true, FailWrite = true }; var provider = new Provider((_, _) => Task.FromResult(Snapshot()));
        var result = await new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), false, default);
        Assert.False(result.FromCache); Assert.Single(result.Snapshot.Items); Assert.Contains("缓存", result.Warning);
    }
    [Fact] public async Task LateOldRequestCannotOverwriteNewerCache()
    {
        var old = new TaskCompletionSource<BangumiRelationsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0; var provider = new Provider((_, _) => Interlocked.Increment(ref calls) == 1 ? old.Task : Task.FromResult(Snapshot("New")));
        var cache = new MemoryCache(); var service = new BangumiRelationsService(provider, cache, () => Now);
        var pending = service.LoadAsync(Identity(), true, default); var newest = await service.LoadAsync(Identity(), true, default);
        old.SetResult(Snapshot("Old")); await pending;
        Assert.Equal("New", Assert.Single(cache.Written).Items[0].Title); Assert.Equal("New", newest.Snapshot.Items[0].Title);
    }
    [Fact] public async Task CancellationAfterIgnoredProviderTokenPublishesNoCache()
    {
        using var stop = new CancellationTokenSource(); var cache = new MemoryCache();
        var provider = new Provider((_, _) => { stop.Cancel(); return Task.FromResult(Snapshot()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), true, stop.Token));
        Assert.Empty(cache.Written);
    }
    [Fact] public async Task InvalidProviderSnapshotIsNotCachedOrReportedAsEmpty()
    {
        var cache = new MemoryCache(); var provider = new Provider((_, _) => Task.FromResult(Snapshot() with { SubjectId = "14" }));
        await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiRelationsService(provider, cache, () => Now).LoadAsync(Identity(), true, default)); Assert.Empty(cache.Written);
    }
}
