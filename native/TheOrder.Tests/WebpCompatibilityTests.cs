using System.Net;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class WebpCompatibilityTests
{
    // Original synthetic 4x6 solid-color images encoded with Pillow; no remote artwork or credentials.
    private const string Lossy = "UklGRjwAAABXRUJQVlA4IDAAAADwAQCdASoEAAYAAUAmJaACdLoB+AAETAAA/uxw3/0mzxNnibPkkf/OwbEu14AAAAA=";
    private const string Lossless = "UklGRiIAAABXRUJQVlA4TBYAAAAvA0ABEAdQvKIUuYABgCD8b5uI6H8I";
    private const string Animated = "UklGRsQAAABXRUJQVlA4WAoAAAACAAAAAwAABQAAQU5JTQYAAAAAAAAAAABBTk1GSgAAAAAAAAAAAAMAAAUAAGQAAAJWUDggMgAAADABAJ0BKgQABgABQCYloAADcAD+8ut///mwP/bz/wR6Af//0uD//pcH//S4P/SkAAAAQU5NRkYAAAAAAAAAAAADAAAFAABkAAAAVlA4IC4AAAA0AQCdASoEAAYAAAAmJaAAA3AA/vtV4///S4P/+lwf/9Lg/9Lg//rV5Vesq6AA";

    [Theory][InlineData(Lossy)][InlineData(Lossless)]
    public async Task ActualWindowsDecoderReadsStaticWebpAndScalesBeforePixelAllocation(string fixture)
    {
        var pixels = await new WindowsRasterCodec().DecodeAsync(Convert.FromBase64String(fixture), 3, default);
        Assert.Equal(2, pixels.Width); Assert.Equal(3, pixels.Height); Assert.Equal(24, pixels.Bgra.Length);
        Assert.True(pixels.Bgra[0] > pixels.Bgra[2]);
    }

    [Fact] public async Task RealCdnMimeContractFlowsThroughProductionSourceDecoderAndThumbnailCache()
    {
        using var profile = new TransactionProfile(); int calls = 0;
        using var http = new HttpClient(new OnlineTransportTests.Handler((request, _) =>
        {
            calls++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(Lossy)) };
            response.Content.Headers.ContentType = new("image/webp"); return Task.FromResult(response);
        }));
        var source = new RemoteCoverSource(http); var key = source.Register("https://images.example/cover.webp", "revision-1")!;
        var cache = new IsolatedThumbnailCache(new(profile.Root));
        await using (var pipeline = new ImagePipeline(source, new WindowsRasterCodec(), cache, new()))
        {
            Assert.NotNull((await pipeline.LoadAsync(key, 128, default)).Pixels);
            Assert.Equal("memory", (await pipeline.LoadAsync(key, 128, default)).Status);
        }
        await using (var pipeline = new ImagePipeline(source, new WindowsRasterCodec(), cache, new()))
        { Assert.NotNull((await pipeline.LoadAsync(key, 128, default)).Pixels); Assert.Equal(0, pipeline.Metrics.Loads); }
        Assert.Equal(1, calls);
    }

    [Theory][InlineData("animation")][InlineData("truncated")][InlineData("riff-size")][InlineData("huge-canvas")]
    public async Task WebpCannotBypassStaticImageAndBoundedDecodePolicy(string kind)
    {
        var bytes = Convert.FromBase64String(kind is "animation" or "huge-canvas" ? Animated : Lossy);
        if (kind == "truncated") bytes = bytes[..20];
        if (kind == "riff-size") bytes[4]++;
        if (kind == "huge-canvas") { bytes[20] = 0; bytes[24] = 0xff; bytes[25] = 0xff; bytes[26] = 0xff; }
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRasterCodec().DecodeAsync(bytes, 128, default));
    }

    [Theory][InlineData("198.18.2.64")][InlineData("198.19.255.254")][InlineData("fdfe:dcba:9876::22d")]
    public void RealSmokeFakeIpRangesRemainDenied(string address) => Assert.False(RemoteCoverSource.IsPublic(IPAddress.Parse(address)));
}
