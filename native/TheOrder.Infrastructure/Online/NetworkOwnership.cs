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
