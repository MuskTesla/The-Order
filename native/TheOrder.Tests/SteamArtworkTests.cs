using System.Net;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;
public sealed class SteamArtworkTests
{
    [Fact] public async Task UsesExactAssociatedAppAndMeasuredDimensionsWithoutCredentialOrTitleSearch()
    {
        int requests = 0; using var http = new HttpClient(new OnlineTransportTests.Handler((request, _) => {
            requests++; Assert.Equal(HttpMethod.Head, request.Method); Assert.StartsWith("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1424660/library_", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); return Task.FromResult(Response(HttpStatusCode.OK));
        }));
        await using var images = new Images(); var source = new SteamArtworkSource(http, images, new Registry());
        var result = await source.ReadAsync(Work() with { References = [new("steam", "1424660", null)] }, default);
        Assert.True(result.Complete); Assert.Equal(2, requests); Assert.Equal(1920, Assert.Single(result.Art).Width); Assert.Equal(620, result.Art[0].Height);
        Assert.Equal(600, Assert.Single(result.Covers).Width); Assert.Equal(900, result.Covers[0].Height); Assert.Equal(2, images.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameAndDemoRequireOfficialExactParentInEitherInputOrder(bool reverse)
    {
        int lookups = 0, heads = 0;
        using var http = new HttpClient(new OnlineTransportTests.Handler((request, _) => {
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            if (request.Method == HttpMethod.Get)
            {
                lookups++; Assert.Equal("store.steampowered.com", request.RequestUri!.Host); Assert.Equal("/api/appdetails", request.RequestUri.AbsolutePath);
                bool game = request.RequestUri.Query.StartsWith("?appids=800820&", StringComparison.Ordinal);
                return Task.FromResult(Store(game ? "800820" : "862510", game ? "game" : "demo", game ? null : "800820"));
            }
            heads++; Assert.Equal(HttpMethod.Head, request.Method);
            Assert.StartsWith("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/800820/library_", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(HttpStatusCode.OK));
        }));
        await using var images = new Images();
        var refs = new[] { new WorkReference("steam", "800820", null), new WorkReference("steam", "862510", null) };
        var result = await new SteamArtworkSource(http, images, new Registry()).ReadAsync(Work() with { References = [.. (reverse ? refs.Reverse() : refs)] }, default);
        Assert.True(result.Complete); Assert.Single(result.Art); Assert.Single(result.Covers); Assert.Equal(2, lookups); Assert.Equal(2, heads);
    }
    [Theory]
    [InlineData("game", null, true)]
    [InlineData("demo", "999", true)]
    [InlineData("dlc", "800820", true)]
    [InlineData("demo", "800820", false)]
    public async Task DifferentGamesWrongParentDlcOrMismatchedResponseNeverChoose(string otherType, string? parent, bool validId)
    {
        using var http = new HttpClient(new OnlineTransportTests.Handler((request, _) => {
            Assert.Equal(HttpMethod.Get, request.Method);
            bool game = request.RequestUri!.Query.StartsWith("?appids=800820&", StringComparison.Ordinal);
            return Task.FromResult(Store(game ? "800820" : "862510", game ? "game" : otherType, game ? null : parent, validId ? null : 999));
        }));
        await using var images = new Images();
        var result = await new SteamArtworkSource(http, images, new Registry()).ReadAsync(Work() with { References = [new("steam", "800820", null), new("steam", "862510", null)] }, default);
        Assert.Empty(result.Art); Assert.True(result.Covers.IsDefaultOrEmpty); Assert.Equal(validId, result.Complete); Assert.Equal(0, images.Calls);
    }
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", false)]
    [InlineData(HttpStatusCode.Redirect, "{}", false)]
    [InlineData(HttpStatusCode.OK, "{}", false)]
    [InlineData(HttpStatusCode.OK, "{\"800820\":{\"success\":false}}", true)]
    [InlineData(HttpStatusCode.OK, "{\"800820\":{\"success\":\"true\"}}", false)]
    public async Task ParentLookupFailureOrAbsenceIsNotASelection(HttpStatusCode status, string json, bool complete)
    {
        using var http = new HttpClient(new OnlineTransportTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) })));
        await using var images = new Images(); var source = new SteamArtworkSource(http, images, new Registry());
        var work = Work() with { References = [new("steam", "800820", null), new("steam", "862510", null)] };
        var result = await source.ReadAsync(work, default); Assert.Empty(result.Art); Assert.Equal(complete, result.Complete); Assert.Equal(0, images.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(work, cancelled.Token));
    }
    [Fact] public async Task ParentLookupIsBoundedAndMoreThanTwoAppIdsAreNotTruncatedToAPair()
    {
        int calls = 0;
        using var http = new HttpClient(new OnlineTransportTests.Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 129 * 1024)) }); }));
        await using var images = new Images(); var source = new SteamArtworkSource(http, images, new Registry());
        var work = Work() with { References = [new("steam", "800820", null), new("steam", "862510", null)] };
        Assert.False((await source.ReadAsync(work, default)).Complete); Assert.Equal(1, calls);
        Assert.True((await source.ReadAsync(work with { References = [.. work.References, new("steam", "3", null)] }, default)).Complete);
        Assert.Equal(1, calls); Assert.Equal(0, images.Calls);
    }
    private static HttpResponseMessage Store(string id, string type, string? parent = null, ulong? actualId = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { success = true, data = new { steam_appid = actualId ?? ulong.Parse(id), type, fullgame = parent is null ? null : new { appid = parent } } } }))
    };
    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Redirect, false)]
    public async Task KnownAbsenceIsEmptyButTransientFailureIsRetryable(HttpStatusCode code, bool complete)
    {
        using var http = new HttpClient(new OnlineTransportTests.Handler((_, _) => Task.FromResult(Response(code))));
        await using var images = new Images(); var result = await new SteamArtworkSource(http, images, new Registry()).ReadAsync(Work() with { References = [new("steam", "1", null)] }, default);
        Assert.Equal(complete, result.Complete); Assert.Empty(result.Art); Assert.Empty(result.Covers); Assert.Equal(0, images.Calls);
    }
    [Fact] public async Task DecodeFailureAndCancellationCannotBecomeVerifiedAbsence()
    {
        using var http = new HttpClient(new OnlineTransportTests.Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK))));
        await using var images = new Images { Missing = true }; var source = new SteamArtworkSource(http, images, new Registry());
        var work = Work() with { References = [new("steam", "1", null)] };
        Assert.False((await source.ReadAsync(work, default)).Complete);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(work, cancelled.Token));
    }
    [Theory]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/library_hero.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/library_600x900.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/library_hero.jpg?redirect=localhost", false)]
    [InlineData("https://shared.fastly.steamstatic.com:8443/store_item_assets/steam/apps/1/library_hero.jpg", false)]
    [InlineData("https://other.example/store_item_assets/steam/apps/1/library_hero.jpg", false)]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/header.jpg", false)]
    public void OriginalAssetRouteIsNarrow(string url, bool expected) => Assert.Equal(expected, SteamArtworkSource.OriginalImageUri(new(url)));
    private static HttpResponseMessage Response(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentType = new("image/jpeg"); return response;
    }
    private sealed class Registry : IRemoteCoverRegistry { public string? Register(string? url, string revision) => url; }
    private sealed class Images : IImagePipeline
    {
        public int Calls; public bool Missing;
        public ImageMetrics Metrics => new(Calls, Calls, 0, 0, 0, 0, 0, 0, 0, 0);
        public Task<ImageResult> LoadAsync(string? key, int edge, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Assert.Equal(ArtworkSizing.MaximumEdge, edge);
            var hero = key!.EndsWith("library_hero.jpg"); int w = hero ? 1920 : 600, h = hero ? 620 : 900;
            return Task.FromResult(new ImageResult(Missing ? null : new(w, h, new byte[w * h * 4]), Missing ? "failure" : "loaded"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
