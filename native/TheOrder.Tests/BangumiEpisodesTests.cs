using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiEpisodesTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private const string Json = """{"total":1,"limit":50,"offset":0,"data":[{"id":9,"subject_id":51,"type":1,"sort":1.5,"name":"Original","name_cn":"特别篇","airdate":"2008-01-01","duration":"00:03:09","desc":"原始说明\n第二行"}]}""";
    private static BangumiEpisode Row(int id = 9, string subject = "51") => new(id, subject, 1, 1.5m, "特别篇", "Original", "2008-01-01", "00:03:09", "原始说明");
    private static BangumiEpisodePage Page(string subject = "51", int offset = 0, int total = 1, BangumiEpisode[]? rows = null, DateTimeOffset? stamp = null)
        => new(subject, offset, total, rows ?? [Row(subject: subject)], stamp ?? Stamp);
    private static BangumiEpisodePage Map(string json = Json)
    { using var document = JsonDocument.Parse(json); return BangumiSubjectDetailsProvider.MapEpisodes("51", 0, document.RootElement, Stamp); }
    private sealed class Provider(Func<string, int, CancellationToken, Task<BangumiEpisodePage>> fetch) : IBangumiEpisodesProvider
    {
        public int Calls;
        public Task<BangumiEpisodePage> FetchEpisodesAsync(string id, int offset, CancellationToken token) { Calls++; return fetch(id, offset, token); }
    }
    private sealed class Reader(Func<string, int, bool, CancellationToken, Task<BangumiEpisodesResult>> read) : IBangumiEpisodesReader
    {
        public int Calls;
        public Task<BangumiEpisodesResult> LoadAsync(string id, int offset, bool refresh, CancellationToken token) { Calls++; return read(id, offset, refresh, token); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Assert.Equal(HttpMethod.Get, request.Method); Assert.False(request.Headers.Contains("Cookie")); Urls.Add(request.RequestUri!.AbsoluteUri); return send(request, token); }
    }
    private static HttpResponseMessage Response(string json = Json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Credentials : IBangumiCredentialStore
    {
        private string? value;
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(value);
        public Task SaveAsync(string input, CancellationToken token) { value = input; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken token) { value = null; return Task.CompletedTask; }
    }
    private sealed class Account : IBangumiAccountProvider
    { public Task<BangumiAccount> ValidateAsync(string token, CancellationToken cancellation) => Task.FromResult(new BangumiAccount("1", "synthetic", "合成")); }

    [Fact] public void OriginalEpisodeTypeFractionalSortAndTextSurviveMapping()
    {
        var episode = Assert.Single(Map().Episodes);
        Assert.Equal(1, episode.Type); Assert.Equal(1.5m, episode.Sort); Assert.Equal("51", episode.SubjectId);
        Assert.Equal("特别篇", episode.Title); Assert.Equal("Original", episode.OriginalTitle);
        Assert.Equal("2008-01-01", episode.AirDate); Assert.Equal("00:03:09", episode.Duration); Assert.Equal("原始说明\n第二行", episode.Summary);
    }
    [Fact] public void EmptyWikiFactsAreNotInventedAndEmptyDirectoryIsValid()
    {
        var value = Map(Json.Replace("特别篇", "").Replace("Original", "").Replace("2008-01-01", "").Replace("00:03:09", "").Replace("原始说明\\n第二行", ""));
        Assert.Null(value.Episodes[0].AirDate); Assert.Empty(value.Episodes[0].Title); Assert.Empty(value.Episodes[0].Duration); Assert.Empty(value.Episodes[0].Summary);
        Assert.Empty(Map("""{"total":0,"limit":50,"offset":0,"data":[]}""").Episodes);
    }
    [Theory]
    [InlineData("\"subject_id\":51", "\"subject_id\":52")]
    [InlineData("\"id\":9", "\"id\":0")]
    [InlineData("\"type\":1", "\"type\":7")]
    [InlineData("\"sort\":1.5", "\"sort\":-1")]
    [InlineData("\"limit\":50", "\"limit\":100")]
    [InlineData("\"offset\":0", "\"offset\":1")]
    [InlineData("\"total\":1", "\"total\":0")]
    [InlineData("\"total\":1", "\"total\":1000001")]
    [InlineData("\"name\":\"Original\"", "\"name\":null")]
    [InlineData("\"desc\":\"原始说明\\n第二行\"", "\"desc\":42")]
    public void WrongIdentityAndMalformedPagesReject(string before, string after) =>
        Assert.Equal(BangumiRelationsFailure.InvalidResponse, Assert.Throws<BangumiRelationsException>(() => Map(Json.Replace(before, after))).Failure);
    [Fact] public void DuplicateIdsAndImpossibleNonEmptyTotalsReject()
    {
        var row = Json[(Json.IndexOf('[') + 1)..Json.LastIndexOf(']')];
        Assert.Throws<BangumiRelationsException>(() => Map("{\"total\":2,\"limit\":50,\"offset\":0,\"data\":[" + row + "," + row + "]}"));
        Assert.Throws<BangumiRelationsException>(() => Map("""{"total":1,"limit":50,"offset":0,"data":[]}"""));
        Assert.Throws<BangumiRelationsException>(() => Map(Json.Replace("原始说明\\n第二行", new string('x', 32769))));
    }
    [Fact] public async Task SubjectAndEpisodesShareOneQueueAndTwoSecondInterval()
    {
        var now = Stamp; var starts = new List<DateTimeOffset>();
        var handler = new Handler((request, _) => { starts.Add(now); return Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("episodes") ? Json : """{"id":51,"type":2,"name":"CLANNAD","name_cn":"","summary":"","nsfw":false}""")); });
        using var client = new HttpClient(handler);
        var provider = new BangumiSubjectDetailsProvider(client, () => now, (wait, token) => { token.ThrowIfCancellationRequested(); now += wait; return Task.CompletedTask; });
        await provider.FetchAsync("51", BangumiRelationKind.Anime, default); await provider.FetchEpisodesAsync("51", 0, default);
        Assert.Equal("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=0", handler.Urls[1]); Assert.True(starts[1] - starts[0] >= TimeSpan.FromSeconds(2));
    }
    [Theory] [InlineData("051", 0)][InlineData("../51", 0)][InlineData("51", -1)][InlineData("51", 10001)]
    public async Task InvalidEpisodeRequestNeverUsesNetwork(string id, int offset)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response())); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BangumiSubjectDetailsProvider(client).FetchEpisodesAsync(id, offset, default)); Assert.Empty(handler.Urls);
    }
    [Fact] public async Task EpisodeTransportDenialIsNotAnEmptyDirectory()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("{}", HttpStatusCode.NotFound))); using var client = new HttpClient(handler);
        Assert.Equal(BangumiRelationsFailure.Unavailable, (await Assert.ThrowsAsync<BangumiRelationsException>(() => new BangumiSubjectDetailsProvider(client).FetchEpisodesAsync("51", 0, default))).Failure);
    }
    [Fact] public async Task FreshPagesAvoidNetworkAndMemoryIsBounded()
    {
        var provider = new Provider((id, offset, _) => Task.FromResult(Page(id, offset, offset + 1)));
        var service = new BangumiEpisodesService(provider, () => Stamp);
        await service.LoadAsync("51", 0, false, default); Assert.True((await service.LoadAsync("51", 0, false, default)).FromCache);
        for (int i = 1; i <= BangumiEpisodesService.MaximumPages; i++) await service.LoadAsync((100 + i).ToString(), 0, false, default);
        Assert.False((await service.LoadAsync("51", 0, false, default)).FromCache); Assert.Equal(34, provider.Calls);
    }
    [Theory] [InlineData(BangumiRelationsFailure.Offline)][InlineData(BangumiRelationsFailure.Timeout)][InlineData(BangumiRelationsFailure.RateLimited)][InlineData(BangumiRelationsFailure.Server)]
    public async Task OnlyTransientFailuresCanUseSevenDayStalePage(BangumiRelationsFailure failure)
    {
        var now = Stamp; bool fail = false;
        var provider = new Provider((_, _, _) => fail ? throw new BangumiRelationsException(failure, "故障") : Task.FromResult(Page()));
        var service = new BangumiEpisodesService(provider, () => now); await service.LoadAsync("51", 0, false, default);
        now += TimeSpan.FromHours(2); fail = true; var stale = await service.LoadAsync("51", 0, false, default); Assert.True(stale.IsStale); Assert.True(stale.FromCache);
        now += TimeSpan.FromDays(8); await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", 0, false, default));
    }
    [Theory] [InlineData(BangumiRelationsFailure.Unavailable)][InlineData(BangumiRelationsFailure.Unauthorized)][InlineData(BangumiRelationsFailure.InvalidResponse)][InlineData(BangumiRelationsFailure.ResponseTooLarge)]
    public async Task DenialAndInvalidContentEvictWithoutStaleFallback(BangumiRelationsFailure failure)
    {
        bool fail = false; var provider = new Provider((_, _, _) => fail ? throw new BangumiRelationsException(failure, "拒绝") : Task.FromResult(Page()));
        var service = new BangumiEpisodesService(provider, () => Stamp); await service.LoadAsync("51", 0, false, default); fail = true;
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", 0, true, default));
        await Assert.ThrowsAsync<BangumiRelationsException>(() => service.LoadAsync("51", 0, false, default)); Assert.Equal(3, provider.Calls);
    }
    [Fact] public async Task LateOldRefreshCannotOverwriteNewerCache()
    {
        int calls = 0; var gate = new TaskCompletionSource<BangumiEpisodePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider((_, _, _) => ++calls == 1 ? gate.Task : Task.FromResult(Page(rows: [Row() with { Title = "new" }])));
        var service = new BangumiEpisodesService(provider, () => Stamp); var old = service.LoadAsync("51", 0, true, default);
        await service.LoadAsync("51", 0, true, default); gate.SetResult(Page(rows: [Row() with { Title = "old" }]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old); Assert.Equal("new", (await service.LoadAsync("51", 0, false, default)).Page.Episodes[0].Title);
    }
    [Fact] public async Task CancelledProviderIgnoringCancellationDoesNotEnterCache()
    {
        var gate = new TaskCompletionSource<BangumiEpisodePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider((_, _, _) => gate.Task); var service = new BangumiEpisodesService(provider, () => Stamp); using var cancel = new CancellationTokenSource();
        var task = service.LoadAsync("51", 0, false, cancel.Token); cancel.Cancel(); gate.SetResult(Page());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.False((await service.LoadAsync("51", 0, false, default)).FromCache);
    }
    [Fact] public async Task AnonymousAndAuthorizedEpisodeCacheScopesRemainSeparate()
    {
        var access = new BangumiAccessContext(); var auth = new BangumiAuthorization(new Credentials(), new Account(), access);
        var provider = new Provider((_, _, _) => Task.FromResult(Page(rows: [Row() with { Title = access.Capture().Authorized ? "authorized" : "anonymous" }])));
        var service = new BangumiEpisodesService(provider, () => Stamp, access);
        await service.LoadAsync("51", 0, false, default); await auth.SaveAsync("Synthetic_Token_12345", default);
        Assert.False((await service.LoadAsync("51", 0, false, default)).FromCache);
        await auth.ClearAsync(default); Assert.Equal("anonymous", (await service.LoadAsync("51", 0, false, default)).Page.Episodes[0].Title);
        Assert.Equal(2, provider.Calls);
    }
    [Fact] public async Task AccessABAChangeDiscardsOldRequestEvenWhenProviderIgnoresCancellation()
    {
        var access = new BangumiAccessContext(); var auth = new BangumiAuthorization(new Credentials(), new Account(), access);
        var gate = new TaskCompletionSource<BangumiEpisodePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BangumiEpisodesService(new Provider((_, _, _) => gate.Task), () => Stamp, access);
        var old = service.LoadAsync("51", 0, false, default); await auth.SaveAsync("Synthetic_Token_12345", default); await auth.ClearAsync(default);
        gate.SetResult(Page()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.False((await service.LoadAsync("51", 0, false, default)).FromCache);
    }
    [Fact] public async Task PageIsolationAndFutureTimestampValidation()
    {
        var provider = new Provider((id, offset, _) => Task.FromResult(Page(id, offset, 100, [Row(offset + 1, id)])));
        var service = new BangumiEpisodesService(provider, () => Stamp); await service.LoadAsync("51", 0, false, default); await service.LoadAsync("51", 50, false, default);
        Assert.Equal(51, (await service.LoadAsync("51", 50, false, default)).Page.Episodes[0].Id);
        Assert.Equal(1, (await service.LoadAsync("51", 0, false, default)).Page.Episodes[0].Id);
        var future = new BangumiEpisodesService(new Provider((_, _, _) => Task.FromResult(Page(stamp: Stamp.AddMinutes(6)))), () => Stamp);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => future.LoadAsync("51", 0, false, default));
    }
    [Fact] public async Task ViewModelDoesNotFetchUntilExpandedAndEmptyIsDistinct()
    {
        var reader = new Reader((id, _, _, _) => Task.FromResult(new BangumiEpisodesResult(Page(id, 0, 0, []), false, false, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); Assert.Equal(0, reader.Calls);
        await model.ExpandAsync(); Assert.Equal(BangumiEpisodesState.Empty, model.State); Assert.False(model.CanMore);
        await model.ExpandAsync(); Assert.Equal(1, reader.Calls); model.SetSource(null); await model.ExpandAsync(); Assert.Equal(1, reader.Calls);
    }
    [Fact] public async Task ViewModelABARejectsTheFirstAResponse()
    {
        int calls = 0; var gate = new TaskCompletionSource<BangumiEpisodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader((id, _, _, _) => ++calls == 1 ? gate.Task : Task.FromResult(new BangumiEpisodesResult(Page(id, rows: [Row(subject: id) with { Title = "new" }]), false, false, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); var old = model.ExpandAsync(); model.SetSource("52"); model.SetSource("51"); await model.ExpandAsync();
        gate.SetResult(new(Page(rows: [Row() with { Title = "old" }]), false, false, null)); await old;
        Assert.Equal("new", Assert.Single(model.Episodes).Title); Assert.Equal(BangumiEpisodesState.Ready, model.State);
    }
    [Fact] public async Task ViewModelDeduplicatesIdsWhileAdvancingOriginalPageOffset()
    {
        var reader = new Reader((id, offset, _, _) => Task.FromResult(new BangumiEpisodesResult(Page(id, offset, 100,
            Enumerable.Range(offset == 0 ? 1 : 50, 50).Select(x => Row(x, id)).ToArray()), false, false, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync(); await model.MoreAsync();
        Assert.Equal(99, model.Episodes.Length); Assert.Equal(100, model.NextOffset); Assert.False(model.CanMore);
    }
    [Fact] public async Task ViewModelBoundsRowsAndReportsTruncation()
    {
        var reader = new Reader((id, offset, _, _) => Task.FromResult(new BangumiEpisodesResult(Page(id, offset, 600,
            Enumerable.Range(offset + 1, 50).Select(x => Row(x, id)).ToArray()), false, false, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync();
        for (int i = 0; i < 10; i++) await model.MoreAsync();
        Assert.Equal(500, model.Episodes.Length); Assert.Equal(10, reader.Calls); Assert.False(model.CanMore); Assert.True(model.AtDisplayLimit); Assert.Contains("500", model.Message);
    }
    [Fact] public async Task ViewModelDenialClearsOldPageRatherThanShowingItAsSuccess()
    {
        bool fail = false; var reader = new Reader((id, _, _, _) => fail ? throw new BangumiRelationsException(BangumiRelationsFailure.Unavailable, "不可访问") : Task.FromResult(new BangumiEpisodesResult(Page(id), false, false, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync(); fail = true; await model.RefreshAsync();
        Assert.Equal(BangumiEpisodesState.Failed, model.State); Assert.Empty(model.Episodes); Assert.Null(model.Result); Assert.Contains("不可访问", model.Message);
    }
    [Fact] public async Task DisplayLimitCannotHideStaleWarning()
    {
        var reader = new Reader((id, offset, _, _) => Task.FromResult(new BangumiEpisodesResult(Page(id, offset, 600, Enumerable.Range(offset + 1, 50).Select(x => Row(x, id)).ToArray()), offset == 0, offset == 0, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync();
        for (int i = 0; i < 9; i++) await model.MoreAsync();
        Assert.True(model.Result!.IsStale); Assert.Contains("之前", model.Message); Assert.Contains("500", model.Message);
    }
    [Fact] public async Task FreshAppendCannotHideAnEarlierStalePage()
    {
        var reader = new Reader((id, offset, refresh, _) => Task.FromResult(new BangumiEpisodesResult(Page(id, offset, 100,
            Enumerable.Range(offset + 1, 50).Select(x => Row(x, id)).ToArray()), !refresh && offset == 0, !refresh && offset == 0, null)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync(); await model.MoreAsync();
        Assert.True(model.Result!.IsStale); Assert.Contains("之前", model.Message); Assert.Equal(100, model.Episodes.Length);
        await model.RefreshAsync(); Assert.False(model.Result!.IsStale); Assert.Equal(50, model.Episodes.Length);
    }
}
