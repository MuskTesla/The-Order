using System.Net;
using System.Security.Cryptography;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;
namespace TheOrder.Tests;

public sealed class ArtworkCacheTests
{
    private const string Url="https://images.example/portrait.webp";
    private static string Key=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Url)));
    [Fact] public async Task ReopeningOfflineAtAnotherRevisionAndDpiUsesPersistentOriginal()
    {
        using var fixture=new TransactionProfile();var disk=new IsolatedArtworkCache(new(fixture.Root));
        var bytes=await ImagePipelineTests.Png(80,120);int downloads=0;
        using(var http=new HttpClient(new OnlineTransportTests.Handler((_,_)=>{
            downloads++;var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};r.Content.Headers.ContentType=new("image/png");return Task.FromResult(r);
        })))
        {
            var source=new RemoteCoverSource(http,disk);var key=source.Register(Url,"revision-one")!;
            await using var pipeline=new ImagePipeline(source,new WindowsRasterCodec(),new IsolatedThumbnailCache(new(fixture.Root)),new());
            Assert.NotNull((await pipeline.LoadAsync(key,128,default)).Pixels);Assert.True(await source.EnsureCachedAsync(key,default));
        }
        using var offline=new HttpClient(new OnlineTransportTests.Handler((_,_)=>throw new InvalidOperationException("No network request is permitted")));
        var reopened=new RemoteCoverSource(offline,new IsolatedArtworkCache(new(fixture.Root)));var newKey=reopened.Register(Url,"revision-two")!;
        await using var next=new ImagePipeline(reopened,new WindowsRasterCodec(),new IsolatedThumbnailCache(new(fixture.Root)),new());
        var result=await next.LoadAsync(newKey,384,default);Assert.NotNull(result.Pixels);Assert.Equal(120,result.Pixels.Height);
        Assert.True(next.TryGet(newKey,384,out var ready));Assert.Same(result.Pixels,ready);Assert.Equal(1,downloads);
    }
    [Theory][InlineData("hash")][InlineData("short")][InlineData("magic")]
    public async Task CorruptionIsAMissAndAtomicRepairRestoresValidBytes(string fault)
    {
        using var fixture=new TransactionProfile();var disk=new IsolatedArtworkCache(new(fixture.Root));byte[] bytes=[1,2,3,4,5];
        Assert.True(await disk.WriteAsync(Key,bytes,default));var file=Path.Combine(fixture.Root,"native-artwork-v1",Key+".art");
        var corrupt=File.ReadAllBytes(file);if(fault=="short")corrupt=[1];else corrupt[fault=="magic"?0:8]^=1;File.WriteAllBytes(file,corrupt);
        Assert.Null(await disk.ReadAsync(Key,default));Assert.Equal(corrupt,File.ReadAllBytes(file));
        Assert.True(await disk.WriteAsync(Key,bytes,default));Assert.Equal(bytes,await disk.ReadAsync(Key,default));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!,"*.tmp"));
    }
    [Fact] public async Task CancelAndTraversalCannotCreateOrModifyCache()
    {
        using var fixture=new TransactionProfile();var disk=new IsolatedArtworkCache(new(fixture.Root));using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>disk.WriteAsync(Key,[1,2,3,4],stop.Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>disk.ReadAsync("../escape",default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root,"native-artwork-v1")));
    }
    [Fact] public async Task PersistenceFailureIsNotReportedAsPrepared()
    {
        var bytes=await ImagePipelineTests.Png(2,2);
        using var http=new HttpClient(new OnlineTransportTests.Handler((_,_)=>{var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};r.Content.Headers.ContentType=new("image/png");return Task.FromResult(r);}));
        var source=new RemoteCoverSource(http,new RefusingCache());var key=source.Register(Url,"r")!;
        Assert.NotNull(await source.ReadAsync(key,default));Assert.False(await source.EnsureCachedAsync(key,default));
    }
    private sealed class RefusingCache:IRemoteArtworkCache
    {
        public Task<byte[]?> ReadAsync(string key,CancellationToken token)=>Task.FromResult<byte[]?>(null);
        public Task<bool> WriteAsync(string key,byte[] bytes,CancellationToken token)=>Task.FromResult(false);
    }
}
