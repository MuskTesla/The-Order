using System.Diagnostics;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Performance;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class PerformanceHardeningTests
{
    [Fact]
    public async Task DuplicateDecodeIsSharedEvenWhenResultCannotFitMemoryCache()
    {
        var codec = new GateCodec(); await using var pipe = new ImagePipeline(new Source(), codec, new NoDisk(), new(MemoryBytes: 0));
        var work = Enumerable.Range(0, 40).Select(_ => pipe.LoadAsync("same", 64, default)).ToArray();
        await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); codec.Release.TrySetResult();
        Assert.All(await Task.WhenAll(work), r => Assert.NotNull(r.Pixels)); Assert.Equal(1, codec.Calls); Assert.Equal(39, pipe.Metrics.Coalesced);
    }
    [Fact]
    public async Task OneCancelledWaiterCannotCancelAnotherConsumer()
    {
        var codec = new GateCodec(); await using var pipe = new ImagePipeline(new Source(), codec, new NoDisk(), new());
        using var cancelled = new CancellationTokenSource(); var first = pipe.LoadAsync("same", 64, cancelled.Token); var second = pipe.LoadAsync("same", 64, default);
        await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel(); Assert.Equal("cancelled", (await first).Status);
        codec.Release.TrySetResult(); Assert.NotNull((await second).Pixels); Assert.Equal(1, codec.Calls);
    }
    [Fact]
    public async Task CancelledLastWaiterDrainsDecoderAndDisposalIsAwaitableByEveryCaller()
    {
        var codec = new GateCodec(); var pipe = new ImagePipeline(new Source(), codec, new NoDisk(), new()); using var cancel = new CancellationTokenSource();
        var first = pipe.LoadAsync("cancel", 64, cancel.Token); await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        Assert.Equal("cancelled", (await first).Status); await Task.WhenAll(pipe.DisposeAsync().AsTask(), pipe.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pipe.Metrics.ActiveDecodes); Assert.Equal(0, pipe.Metrics.Outstanding); Assert.Equal(0, pipe.Metrics.CachedBytes);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FloodHasBoundedPendingOperationsAndNoUnobservedDecoderBacklog(bool duplicate)
    {
        var codec = new GateCodec(); await using var pipe = new ImagePipeline(new Source(), codec, new NoDisk(), new(PendingRequests: 8));
        var work = Enumerable.Range(0, 600).Select(i => pipe.LoadAsync("flood-" + (duplicate ? 0 : i), 64, default)).ToArray();
        Assert.InRange(pipe.Metrics.PendingOperations, 1, 8); Assert.True(pipe.Metrics.Rejected >= 592);
        Assert.InRange(pipe.Metrics.PendingWaiters, 1, 8);
        codec.Release.TrySetResult(); await Task.WhenAll(work); Assert.Equal(0, pipe.Metrics.Outstanding); Assert.InRange(codec.Calls, 1, 8); Assert.Equal(0, pipe.Metrics.PendingWaiters);
    }
    [Fact]
    public async Task TinyImagesRespectItemBudgetAndHotHitsDoNotDecodeAgain()
    {
        var codec = new GateCodec(); codec.Release.TrySetResult(); await using var pipe = new ImagePipeline(new Source(), codec, new NoDisk(), new(MemoryItems: 3));
        for (int i = 0; i < 20; i++) await pipe.LoadAsync("tiny-" + i, 16, default);
        Assert.Equal(3, pipe.Metrics.CachedItems); int calls = codec.Calls;
        for (int i = 0; i < 100; i++) Assert.Equal("memory", (await pipe.LoadAsync("tiny-19", 16, default)).Status);
        Assert.Equal(calls, codec.Calls);
    }
    [Theory][InlineData(1000)][InlineData(5000)][InlineData(10000)]
    public void DatasetsAreDiverseDeterministicAndRetainInstallationsAndAccounting(int count)
    {
        var entries = Program.Dataset(count); Assert.Equal(count, entries.Select(e => e.Id).Distinct().Count()); Assert.Equal(count, entries.Select(ImageSourceKeys.For).Distinct().Count());
        Assert.True(entries.Select(e => e.Sessions.Length).Distinct().Count() > 1); Assert.Contains(entries, e => e.Installations.Length == 0);
        var codec = new NativeLibraryCodec(); var bytes = codec.Encode(entries, Program.Date); var actual = codec.Decode(bytes);
        Assert.Equal(bytes, codec.Encode(Program.Dataset(count), Program.Date)); Assert.All(actual, e => { Assert.Equal(e.Sessions.Sum(s => s.Seconds), e.TotalPlaySeconds); foreach (var install in e.Installations) Assert.Equal(install.Folder, install.WorkingDirectory); });
    }
    [Fact]
    public void PercentileRetainsOutliersAndUsesNearestRank() { Assert.Equal(100, Program.Percentile([1, 2, 3, 4, 100], .95)); Assert.Equal(3, Program.Percentile([1, 2, 3, 4, 100], .5)); }
    [Theory][InlineData(1d,256)][InlineData(1.5d,384)][InlineData(2d,512)]
    public async Task DpiThumbnailDensityUsesBoundedActualDecoder(double scale, int expected)
    {
        int edge = ThumbnailSizing.Edge(160, 240, scale); Assert.Equal(expected, edge);
        var raster = await new WindowsRasterCodec().DecodeAsync(await ImagePipelineTests.Png(600, 900), edge, default);
        Assert.Equal(expected, Math.Max(raster.Width, raster.Height)); Assert.Equal(raster.Width * raster.Height * 4, raster.Bgra.Length);
    }
    [Fact]
    public void SizeBucketsRemainStableAndBoundPathologicalLayoutDimensions()
    { Assert.Equal(ThumbnailSizing.Edge(160, 240, 1), ThumbnailSizing.Edge(161, 241, 1)); Assert.Equal(1600, ThumbnailSizing.Edge(10000, 10000, 2)); Assert.Equal(64, ThumbnailSizing.Edge(double.NaN, 10, 1)); }
    private sealed class Source : IImageSourceProvider { public Task<byte[]?> ReadAsync(string key, CancellationToken cancellation) => Task.FromResult<byte[]?>([1]); }
    private sealed class NoDisk : IThumbnailDiskCache
    { public Task<RasterPixels?> ReadAsync(string key, CancellationToken cancellation) => Task.FromResult<RasterPixels?>(null); public Task WriteAsync(string key, RasterPixels pixels, long maximumBytes, CancellationToken cancellation) => Task.CompletedTask; }
    private sealed class GateCodec : IRasterCodec
    {
        public int Calls; public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RasterPixels> DecodeAsync(ReadOnlyMemory<byte> encoded, int edge, CancellationToken cancellation) { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellation); return new(16, 16, new byte[1024]); }
        public Task<LocalCover> NormalizePhotoAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
