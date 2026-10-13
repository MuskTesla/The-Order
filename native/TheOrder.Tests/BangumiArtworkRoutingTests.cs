using System.Net;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiArtworkRoutingTests
{
    [Theory]
    [InlineData("https://lain.bgm.tv/r/800/pic/cover/l/test.jpg", true)]
    [InlineData("https://lain.bgm.tv/pic/cover/m/test.jpg", true)]
    [InlineData("https://lain.bgm.tv/other/test.jpg", false)]
    [InlineData("https://lain.bgm.tv.attacker.example/pic/cover/m/test.jpg", false)]
    public async Task OnlyCanonicalBangumiArtworkUsesAnonymousPublicPool(string url, bool publicRoute)
    {
        var normal = new Handler(); var anonymous = new Handler();
        using var first = new HttpClient(normal); using var second = new HttpClient(anonymous);
        var source = new RemoteCoverSource(first, originalArtwork: second);
        var key = source.Register(url, "bangumi-test")!;
        Assert.NotNull(await source.ReadAsync(key, default));
        Assert.Equal(publicRoute ? 1 : 0, anonymous.Requests);
        Assert.Equal(publicRoute ? 0 : 1, normal.Requests);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("image/jpeg"); return Task.FromResult(response);
        }
    }
}
