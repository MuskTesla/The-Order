using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiWatchDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static object Site(string key, string id, string? season = null, string? url = null) => new { site = key, id, season_id = season, url };
    private static object Row(string bg = "51", string ani = "2167", string type = "tv", string date = "2007-10-04T17:25:00Z", params object[] onair) => new { title = "Same title is deliberately irrelevant", type, begin = date, sites = new[] { Site("bangumi", bg), Site("aniList", ani) }.Concat(onair).ToArray() };
    private static BangumiWatchDataset Blob(string repo, params object[] rows)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { siteMeta = new Dictionary<string, object>
        {
            ["bilibili"] = new { title = "Bilibili", type = "onair", urlTemplate = "https://www.bilibili.com/bangumi/media/md{{id}}/", regions = new[] { "CN" } },
            ["bangumi"] = new { title = "Bangumi", type = "info", urlTemplate = "https://bgm.tv/subject/{{id}}" },
            ["aniList"] = new { title = "AniList", type = "info", urlTemplate = "https://anilist.co/anime/{{id}}" },
            ["dmhy"] = new { title = "Resources", type = "resource", urlTemplate = "https://share.dmhy.org/topics/list?keyword={{id}}" },
            ["unknown"] = new { title = "Unknown", type = "onair", urlTemplate = "https://untrusted.example.com/{{id}}" }
        }, items = rows });
        return BangumiWatchDataset.Create(repo, new('a', 40), Now.AddDays(-1), "test", bytes);
    }
    private static BangumiWatchSnapshot Snapshot(object[] primary, object[]? extra = null) => new(Blob("bangumi-data/bangumi-data", primary), extra is null ? null : Blob("wan0ge/bangumi-data", extra), DateTimeOffset.MinValue);
    private static WatchDataQuery Query(int? ani = 2167, string? bg = "51", string format = "TV", string date = "2007-10-05") => new(new(ani, bg), format, date);
    [Fact] public void MediaIdAloneStaysOnTheExactInformationPage()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777")])])); var result = index.Find(Query()); var link = Assert.Single(result.Links);
        Assert.Equal("https://www.bilibili.com/bangumi/media/md7777/", link.Url); Assert.Equal(WatchLinkKind.Information, link.Kind); Assert.Equal(WatchLinkVerification.ProviderListed, link.Verification);
        Assert.Equal(new[] { "CN" }, link.Regions); Assert.NotNull(link.Sources[0].DataVersion);
    }
    [Fact] public void ExplicitSeasonIsSupplementedWithoutConvertingTheMediaId()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777")])], [Row(onair: [Site("bilibili", "7777", "8888")])]));
        var link = Assert.Single(index.Find(Query()).Links); Assert.Equal("https://www.bilibili.com/bangumi/play/ss8888", link.Url);
        Assert.Contains(link.Sources, x => x.Source == "wan0ge/bangumi-data"); Assert.Contains(link.Sources, x => x.Url.EndsWith("md7777/", StringComparison.Ordinal));
    }
    [Theory][InlineData("52", "2167", "tv", "2007-10-04T17:25:00Z", "7777")]
    [InlineData("51", "4181", "tv", "2007-10-04T17:25:00Z", "7777")]
    [InlineData("51", "2167", "movie", "2007-10-04T17:25:00Z", "7777")]
    [InlineData("51", "2167", "tv", "2008-01-04T17:25:00Z", "7777")]
    [InlineData("51", "2167", "tv", "2007-10-04T17:25:00Z", "9999")]
    public void ConflictingForkIdentityFormatDateOrMediaIdNeverSuppliesSeason(string bg, string ani, string type, string date, string media)
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777")])], [Row(bg, ani, type, date, Site("bilibili", media, "8888"))]));
        Assert.Equal(WatchLinkKind.Information, Assert.Single(index.Find(Query()).Links).Kind);
    }
    [Fact] public void DuplicateIdAndConflictingCrossSiteIdsAbstain()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777")]), Row("51", "4181", onair: [Site("bilibili", "8888")])]));
        Assert.Equal(WatchDataMatch.AmbiguousIdentity, index.Find(Query()).Match); Assert.Empty(index.Find(Query()).Links);
        index = new(Snapshot([Row(), Row("876", "4181")])); Assert.Equal(WatchDataMatch.IdentityConflict, index.Find(Query(4181, "51")).Match);
    }
    [Theory][InlineData("MOVIE", "2007-10-05")][InlineData("TV", "2008-10-05")][InlineData("TV", "2007-07-05")]
    public void DifferentFormatYearOrQuarterAbstains(string format, string date)
    {
        var result = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777")])])).Find(Query(format: format, date: date));
        Assert.Equal(WatchDataMatch.GranularityConflict, result.Match); Assert.Empty(result.Links);
    }
    [Fact] public void TimezoneBoundaryIsNotAFalseQuarterConflict()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(date: "2007-09-30T17:25:00Z", onair: [Site("bilibili", "7777")])])); Assert.Equal(WatchDataMatch.Matched, index.Find(Query(date: "2007-10-01")).Match);
    }
    [Fact] public void SharedSeriesPageDoesNotPretendToBeTwoSeasonSpecificPlayers()
    {
        var first = Row(onair: [Site("bilibili", "7777")]); var second = Row("876", "4181", date: "2008-10-02T16:59:00Z", onair: [Site("bilibili", "7777")]);
        var index = new BangumiWatchDataIndex(Snapshot([first, second], [Row(onair: [Site("bilibili", "7777", "8888")])]));
        var link = Assert.Single(index.Find(Query()).Links); Assert.Equal(WatchLinkKind.Information, link.Kind); Assert.Contains("合集", link.Notes);
    }
    [Fact] public void NewsResourcesUnknownHostsAndHostSpoofingAreExcluded()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("dmhy", "CLANNAD"), Site("unknown", "x"), Site("bilibili", "7777", url: "https://www.bilibili.com.evil.example/bangumi/play/ss7777")])]));
        Assert.Equal(WatchDataMatch.MatchedFilteredBroadcast, index.Find(Query()).Match); Assert.Empty(index.Find(Query()).Links);
        Assert.Equal(2, index.RejectedSites);
    }
    [Theory][InlineData("https://www.bilibili.com/redirect?target=https://bad.example")][InlineData("https://www.bilibili.com/bangumi/play/ss0001")][InlineData("http://www.bilibili.com/bangumi/play/ss1")]
    public void UnsafeOrNoncanonicalTargetIsNotExposed(string url) => Assert.Empty(new BangumiWatchDataIndex(Snapshot([Row(onair: [Site("bilibili", "7777", url: url)])])).Find(Query()).Links);
    [Fact] public void UnrecordedAndRecordedWithoutBroadcastAreDistinct()
    {
        var index = new BangumiWatchDataIndex(Snapshot([Row()])); Assert.Equal(WatchDataMatch.MatchedNoBroadcast, index.Find(Query()).Match); Assert.Equal(WatchDataMatch.NotRecorded, index.Find(Query(99, "999")).Match);
    }
    [Fact] public async Task FullBuiltInSnapshotsAreVerifiedAndContainDifferentClannadSeasons()
    {
        using var http = new HttpClient(new ThrowHandler()); var snapshot = await new BangumiWatchDataProvider(http).BuiltInAsync(default); var index = new BangumiWatchDataIndex(snapshot);
        Assert.Equal(8922, index.InputItems); Assert.True(index.IndexedItems > 8000); Assert.True(index.AmbiguousIdentityKeys > 0);
        Assert.Contains(index.Find(Query()).Links, x => x.Url.EndsWith("ss1177", StringComparison.Ordinal));
        Assert.Contains(index.Find(Query(4181, "876", date: "2008-10-03")).Links, x => x.Url.EndsWith("ss1178", StringComparison.Ordinal));
    }
    [Fact] public async Task FailedUpdateKeepsOldDataAndPersistsWeeklyBackoff()
    {
        var cache = new MemoryStore(); var provider = new FakeProvider(Snapshot([Row(onair: [Site("bilibili", "7777")])])) { Fail = true };
        await using var service = new BangumiWatchDataService(provider, cache, () => Now);
        Assert.Single((await service.ReadAsync(Query(), default)).Links); Assert.True(await service.UpdateIfDueAsync(default));
        Assert.Single((await service.ReadAsync(Query(), default)).Links); Assert.Contains("继续使用", (await service.ReadAsync(Query(), default)).Message);
        Assert.False(await service.UpdateIfDueAsync(default)); Assert.Equal(1, provider.Updates);
        await using var restarted = new BangumiWatchDataService(provider, cache, () => Now.AddDays(1)); await restarted.ReadAsync(Query(), default); Assert.False(await restarted.UpdateIfDueAsync(default)); Assert.Equal(1, provider.Updates);
    }
    [Fact] public async Task ConcurrentUpdatesCoalesceAndCancellationCannotReplaceOldSnapshot()
    {
        var cache = new MemoryStore(); var provider = new FakeProvider(Snapshot([Row(onair: [Site("bilibili", "7777")])]));
        await using var service = new BangumiWatchDataService(provider, cache, () => Now);
        await service.ReadAsync(Query(), default); var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.UpdateIfDueAsync(default))); Assert.Single(outcomes, x => x); Assert.Equal(1, provider.Updates);
        var canceled = new FakeProvider(provider.Snapshot) { Hold = true }; await using var another = new BangumiWatchDataService(canceled, new MemoryStore(), () => Now);
        await another.ReadAsync(Query(), default); using var stop = new CancellationTokenSource(); var pending = another.UpdateIfDueAsync(stop.Token); await canceled.Started.Task; stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Single((await another.ReadAsync(Query(), default)).Links);
    }
    [Fact] public async Task CacheRoundTripPreservesRequiredProfileComponentsAndRejectsUnknownVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "the-order-watch-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, directory), new NativeLibraryCodec()); var protectedFiles = new[] { "library.json", "native-ui.json", "native-profile.json" }.Where(x => File.Exists(Path.Combine(directory, x))).ToDictionary(x => x, x => File.ReadAllBytes(Path.Combine(directory, x)));
            await using var store = new IsolatedWatchDataSnapshotStore(profile.Configuration()); var value = Snapshot([Row(onair: [Site("bilibili", "7777")])]); await store.WriteAsync(value, default); Assert.Single(new BangumiWatchDataIndex((await store.ReadAsync(default))!).Find(Query()).Links);
            foreach (var file in protectedFiles) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(directory, file.Key)));
            var path = Path.Combine(directory, IsolatedWatchDataSnapshotStore.FileName); var bytes = File.ReadAllText(path).Replace("\"Version\":1", "\"Version\":999", StringComparison.Ordinal); File.WriteAllText(path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(default)); await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(value, default)); Assert.Equal(bytes, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact] public void CorruptGzipAndHashCannotEnterIndex()
    {
        var value = Snapshot([Row()]); Assert.Throws<InvalidDataException>(() => new BangumiWatchDataIndex(value with { Primary = value.Primary with { Sha256 = new('0', 64) } }));
    }
    [Fact] public async Task PublicUpdateUsesPinnedJsonAndRejectsMalformedMetadataWithoutLosingTheSnapshot()
    {
        var old = Snapshot([Row(onair: [Site("bilibili", "7777")])]); var handler = new MetadataHandler("[{\"unexpected\":1}]");
        using var http = new HttpClient(handler); var provider = new BangumiWatchDataProvider(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.UpdateAsync(old, default)); Assert.Single(handler.Uris); Assert.Equal("api.github.com", handler.Uris[0].Host);
        Assert.All(handler.Auth, x => Assert.Null(x)); Assert.All(handler.Agents, x => Assert.Contains("TheOrder", x));
    }
    [Fact] public async Task UpdatingChecksBothFixedPublicRepositoriesAndNeverFetchesPlatforms()
    {
        var old = Snapshot([Row(onair: [Site("bilibili", "7777")])], [Row(onair: [Site("bilibili", "7777", "8888")])]);
        var handler = new MetadataHandler("[{\"sha\":\"" + old.Primary.Revision + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-13T00:00:00Z\"}}}]");
        using var http = new HttpClient(handler); var value = await new BangumiWatchDataProvider(http).UpdateAsync(old, default);
        Assert.Equal(2, handler.Uris.Count); Assert.All(handler.Uris, x => Assert.Equal("api.github.com", x.Host)); Assert.Equal(old.Primary.Sha256, value.Primary.Sha256);
    }
    [Fact] public async Task ANewRevisionDownloadsOnlyItsImmutablePublicJson()
    {
        var old = Snapshot([Row(onair: [Site("bilibili", "7777")])]); var updated = Blob("bangumi-data/bangumi-data", Row(onair: [Site("bilibili", "9999")])); var calls = new List<Uri>();
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            calls.Add(request.RequestUri!); Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent("[{\"sha\":\"" + new string('b', 40) + "\",\"commit\":{\"committer\":{\"date\":\"" + DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") + "\"}}}]") };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(updated.Decode()) };
        }));
        var result = await new BangumiWatchDataProvider(http).UpdateAsync(old, default); Assert.Equal(2, calls.Count);
        Assert.Equal("raw.githubusercontent.com", calls[1].Host); Assert.Contains("/" + new string('b', 40) + "/dist/data.json", calls[1].AbsolutePath);
        Assert.Equal(updated.Sha256, result.Primary.Sha256); Assert.Contains("md9999", Assert.Single(new BangumiWatchDataIndex(result).Find(Query()).Links).Url);
    }
    [Theory][InlineData(HttpStatusCode.TooManyRequests)][InlineData(HttpStatusCode.Redirect)][InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task QuotaRedirectAndOutageDoNotRetryOrFollowArbitraryHosts(HttpStatusCode status)
    {
        int calls = 0; using var http = new HttpClient(new ReplyHandler(_ => { calls++; var response = new HttpResponseMessage(status); response.Headers.Location = new("https://bad.example/credentials"); return response; }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new BangumiWatchDataProvider(http).UpdateAsync(Snapshot([Row()]), default)); Assert.Equal(1, calls);
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request)); }
    private sealed class MetadataHandler(string metadata) : HttpMessageHandler
    {
        public List<Uri> Uris = []; public List<string?> Auth = []; public List<string> Agents = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Uris.Add(request.RequestUri!); Auth.Add(request.Headers.Authorization?.ToString()); Agents.Add(request.Headers.UserAgent.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata, Encoding.UTF8, "application/json") }); }
    }
    private sealed class ThrowHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Built-in snapshot must not use the network."); }
    private sealed class MemoryStore : IBangumiWatchSnapshotStore
    {
        public BangumiWatchSnapshot? Value;
        public Task<BangumiWatchSnapshot?> ReadAsync(CancellationToken cancellation) => Task.FromResult(Value);
        public Task WriteAsync(BangumiWatchSnapshot value, CancellationToken cancellation) { Value = value; return Task.CompletedTask; }
    }
    private sealed class FakeProvider(BangumiWatchSnapshot snapshot) : IBangumiWatchDataProvider
    {
        public BangumiWatchSnapshot Snapshot => snapshot; public int Updates; public bool Fail, Hold; public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BangumiWatchSnapshot> BuiltInAsync(CancellationToken cancellation) => Task.FromResult(snapshot);
        public async Task<BangumiWatchSnapshot> UpdateAsync(BangumiWatchSnapshot previous, CancellationToken cancellation) { Updates++; Started.TrySetResult(); if (Hold) await Task.Delay(Timeout.Infinite, cancellation); if (Fail) throw new HttpRequestException(); return previous with { CheckedAtUtc = Now }; }
    }
}
