namespace TheOrder.Infrastructure.Online;

// One pooled client per transport lifetime. Separate image transport never has an Authorization header.
public sealed class CatalogNetwork : IDisposable
{
    public HttpClient Client { get; } = CatalogTransport.CreateClient();
    public void Dispose() => Client.Dispose();
}
public sealed class ImageNetwork : IDisposable
{
    public HttpClient Client { get; } = RemoteCoverSource.CreateClient();
    public void Dispose() => Client.Dispose();
}
// Fixed public catalog APIs and canonical VNDB/Steam/Bangumi assets use the system route, including VPN
// fake-DNS and proxies. This pool has no catalog credentials, cookies or redirects.
// Arbitrary metadata image hosts still use ImageNetwork's checked public socket.
public sealed class PublicArtworkNetwork : IDisposable
{
    public HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
    }) { Timeout = Timeout.InfiniteTimeSpan };
    public void Dispose() => Client.Dispose();
}
