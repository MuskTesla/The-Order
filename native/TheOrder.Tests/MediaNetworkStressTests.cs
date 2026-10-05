using System.Net;
using System.Net.Sockets;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class MediaNetworkStressTests
{
    [Fact]
    public async Task LocalHttpFixtureExercisesMissHitRevisionCorruptionAndConcurrentRequests()
    {
        var png = await ImagePipelineTests.Png(320, 480);
        await using var server = new Server(png); using var http = new HttpClient(new LoopbackTransport(server.Port));
        var source = new RemoteCoverSource(http); using var profile = new TransactionProfile();
        await using var pipe = new ImagePipeline(source, new WindowsRasterCodec(), new IsolatedThumbnailCache(new(profile.Root)), new());
        var key = source.Register("https://images.example/cover", "r1")!;
        var all = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => pipe.LoadAsync(key, 192, default)));
        Assert.All(all, result => Assert.NotNull(result.Pixels)); Assert.Equal(1, server.Requests);
        Assert.Equal("memory", (await pipe.LoadAsync(key, 192, default)).Status);
        var revised = source.Register("https://images.example/cover", "r2")!;
        Assert.NotEqual(key, revised); Assert.NotNull((await pipe.LoadAsync(revised, 192, default)).Pixels); Assert.Equal(2, server.Requests);
        var corrupt = source.Register("https://images.example/corrupt", "r1")!;
        Assert.Equal("invalid", (await pipe.LoadAsync(corrupt, 192, default)).Status);
        Assert.Equal(0, pipe.Metrics.ActiveDecodes); Assert.Equal(0, pipe.Metrics.Outstanding);
    }
    [Fact]
    public async Task ErrorAndCancellationBurstsDrainNetworkAndDecodeWithoutRetryStorm()
    {
        await using var server = new Server(await ImagePipelineTests.Png(32, 48)); using var http = new HttpClient(new LoopbackTransport(server.Port));
        var source = new RemoteCoverSource(http); using var profile = new TransactionProfile();
        await using var pipe = new ImagePipeline(source, new WindowsRasterCodec(), new IsolatedThumbnailCache(new(profile.Root)), new());
        for (int round = 0; round < 100; round++)
        {
            using var cancel = new CancellationTokenSource();
            var key = source.Register("https://images.example/slow", "r" + round)!;
            var load = pipe.LoadAsync(key, 64, cancel.Token); cancel.Cancel(); Assert.Equal("cancelled", (await load).Status);
        }
        var unavailable = source.Register("https://images.example/missing", "r1")!;
        for (int i = 0; i < 30; i++) Assert.Equal("unavailable", (await pipe.LoadAsync(unavailable, 64, default)).Status);
        await pipe.DisposeAsync(); Assert.Equal(0, pipe.Metrics.ActiveDecodes); Assert.Equal(0, pipe.Metrics.PendingWaiters);
        Assert.InRange(server.Requests, 30, 130); // One attempt per request; no internal retry loop.
    }
    private sealed class LoopbackTransport(int port) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker transport = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal("images.example", request.RequestUri!.Host); Assert.Null(request.Headers.Authorization);
            request.RequestUri = new Uri($"http://127.0.0.1:{port}" + request.RequestUri.AbsolutePath);
            return transport.SendAsync(request, cancellation);
        }
        protected override void Dispose(bool disposing) { if (disposing) transport.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task serving;
        private readonly byte[] png;
        public int Requests;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Server(byte[] png) { this.png = png; listener.Start(); serving = Serve(); }
        private async Task Serve()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token); using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    string first = await reader.ReadLineAsync(stopping.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopping.Token))) { }
                    Interlocked.Increment(ref Requests);
                    if (first.Contains("slow")) await Task.Delay(30, stopping.Token);
                    var body = first.Contains("corrupt") ? "invalid raster"u8.ToArray() : png;
                    string status = first.Contains("missing") ? "404 Not Found" : "200 OK";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    try { await stream.WriteAsync(header, stopping.Token); await stream.WriteAsync(body, stopping.Token); }
                    catch (IOException) { }
                }
            }
            catch (Exception e) when (stopping.IsCancellationRequested && e is OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await serving; stopping.Dispose(); }
    }
}
