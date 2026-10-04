using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class ImagePipelineTests
{
    [Fact] public async Task RealPngAndJpegDecodeAtRequestedSizeAndPersonalCoverSurvivesCacheDeletion()
    {
        using var profile = new TransactionProfile(); var codec = new WindowsRasterCodec();
        var source = await Png(600, 900); var path = Path.Combine(profile.Root, "photo.png"); await File.WriteAllBytesAsync(path, source);
        var small = await codec.DecodeAsync(source, 192, default); Assert.Equal(192, Math.Max(small.Width, small.Height)); Assert.Equal(small.Width * small.Height * 4, small.Bgra.Length);
        var cover = await codec.NormalizePhotoAsync(path, default); Assert.InRange(Convert.FromBase64String(cover.DataUrl.Split(',')[1]).Length, 1, 128 * 1024);
        if (Environment.GetEnvironmentVariable("THE_ORDER_IMAGE_FIXTURE_OUTPUT") is { } fixtureOutput)
        {
            Directory.CreateDirectory(fixtureOutput);
            File.WriteAllBytes(Path.Combine(fixtureOutput, "photo.png"), source);
            File.WriteAllBytes(Path.Combine(fixtureOutput, "small.png"), await Png(60, 90));
            var compact = await codec.NormalizePhotoAsync(Path.Combine(fixtureOutput, "small.png"), default);
            File.WriteAllText(Path.Combine(fixtureOutput, "cover.json"), System.Text.Json.JsonSerializer.Serialize(compact, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            File.WriteAllBytes(Path.Combine(fixtureOutput, "invalid.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            var pool = new List<LocalCover>();
            for (var i = 0; i < 4; i++)
            {
                var poolPath = Path.Combine(fixtureOutput, $"pool-{i}.png"); File.WriteAllBytes(poolPath, await Png(40, 60, i));
                pool.Add(await codec.NormalizePhotoAsync(poolPath, default));
            }
            File.WriteAllText(Path.Combine(fixtureOutput, "cover-pool.json"), System.Text.Json.JsonSerializer.Serialize(pool, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        }
        var entry = new LibraryEntry("image", "image", false, null, [], null, "planned", null, false, [], [], "", 0, 0, [], "2026-10-04T00:00:00.000Z", null, cover, null, new(false, null));
        var legacy = new NativeLibraryCodec(); var document = legacy.Encode([entry], "2026-10-04T00:00:00.000Z"); Assert.Equal(cover, legacy.Decode(document)[0].LocalCover); File.WriteAllBytes(profile.Main, document);
        var cache = new IsolatedThumbnailCache(new(profile.Root)); var key = LocalCoverSources.Key(cover) + "-192";
        await cache.WriteAsync(key, small, 1024 * 1024, default); var cached = await cache.ReadAsync(key, default); Assert.Equal(small.Bgra, cached!.Bgra);
        var cacheRoot = Path.GetFullPath(Path.Combine(profile.Root, "native-thumbnails-v1")); Assert.Equal(profile.Root, Path.GetDirectoryName(cacheRoot)); Directory.Delete(cacheRoot, true);
        Assert.Equal(document, File.ReadAllBytes(profile.Main)); Assert.Equal(cover, legacy.Decode(File.ReadAllBytes(profile.Main))[0].LocalCover);
    }
    [Theory][InlineData("invalid")][InlineData("truncated")][InlineData("huge")][InlineData("unsupported")]
    public async Task InvalidRastersBecomePlaceholderWithoutCrashingLibrary(string kind)
    {
        var bytes = kind switch { "invalid" => new byte[] { 1, 2, 3, 4 }, "truncated" => (await Png(80, 120))[..50], "huge" => await HugeHeader(), _ => "GIF89a"u8.ToArray() };
        if (kind == "huge")
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRasterCodec().DecodeAsync(bytes, 192, default));
            Assert.Contains("4000 万像素", error.Message);
        }
        await using var pipeline = new ImagePipeline(new Source(bytes), new WindowsRasterCodec(), new NoDisk(), new());
        var result = await pipeline.LoadAsync("image", 192, default); Assert.Null(result.Pixels); Assert.Equal("invalid", result.Status); Assert.Equal(1, pipeline.Metrics.Errors);
    }
    [Fact] public async Task ConcurrencyMemoryBudgetEvictionAndCancellationAreBounded()
    {
        var decoder = new CountingCodec(); await using var pipeline = new ImagePipeline(new Source([1]), decoder, new NoDisk(), new(2, 16 * 16 * 4 * 2));
        var work = Enumerable.Range(0, 12).Select(i => pipeline.LoadAsync("image" + i, 16, default)).ToArray(); await Task.WhenAll(work);
        Assert.InRange(pipeline.Metrics.MaximumActiveDecodes, 1, 2); Assert.InRange(pipeline.Metrics.CachedItems, 1, 2); Assert.InRange(pipeline.Metrics.CachedBytes, 1, 2048);
        long before = pipeline.Metrics.Loads; await pipeline.LoadAsync("image0", 16, default); Assert.Equal(before + 1, pipeline.Metrics.Loads);
        await pipeline.LoadAsync("image0", 16, default); Assert.True(pipeline.Metrics.Hits > 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.Equal("cancelled", (await pipeline.LoadAsync("cancelled", 16, cancelled.Token)).Status);
        Assert.Equal(0, pipeline.Metrics.Outstanding); Assert.Equal(0, pipeline.Metrics.ActiveDecodes);
    }
    [Fact] public void RecycledContainerRejectsLateCompletionEvenForSameKey()
    {
        using var slot = new ImageRequestSlot(); var old = slot.Begin("a"); var next = slot.Begin("b");
        Assert.True(old.Cancellation.IsCancellationRequested); Assert.False(slot.IsCurrent("a", old.Generation)); Assert.True(slot.IsCurrent("b", next.Generation));
        var same = slot.Begin("b"); Assert.False(slot.IsCurrent("b", next.Generation)); Assert.True(slot.IsCurrent("b", same.Generation)); slot.Clear(); Assert.False(slot.IsCurrent("b", same.Generation));
    }
    [Fact] public async Task DiskCorruptionIsMissAndCacheBoundDoesNotTouchPersonalFiles()
    {
        using var profile = new TransactionProfile(); var cache = new IsolatedThumbnailCache(new(profile.Root)); var key = new string('A', 64) + "-64"; var pixels = new RasterPixels(16, 16, new byte[1024]);
        await cache.WriteAsync(key, pixels, 2100, default);
        await cache.WriteAsync(new string('B', 64) + "-64", pixels, 2100, default);
        Assert.Null(await cache.ReadAsync(key, default));
        string target = Path.Combine(profile.Root, "native-thumbnails-v1", new string('B', 64) + "-64.thumb");
        var bytes = File.ReadAllBytes(target); bytes[^1] = 10; File.WriteAllBytes(target, bytes);
        Assert.Null(await cache.ReadAsync(new string('B', 64) + "-64", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ReadAsync("../library", default));
    }
    [Fact] public async Task MissingLockedAndCancelledPhotoNeverProducePersonalCover()
    {
        using var profile = new TransactionProfile(); var codec = new WindowsRasterCodec(); var path = Path.Combine(profile.Root, "photo.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => codec.NormalizePhotoAsync(path, default));
        File.WriteAllBytes(path, await Png(80, 120)); using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await Assert.ThrowsAsync<IOException>(() => codec.NormalizePhotoAsync(path, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.NormalizePhotoAsync(path, cancellation.Token));
    }
    [Fact] public async Task ShutdownCancelsAndDrainsDecodes()
    {
        var codec = new CountingCodec(); var pipeline = new ImagePipeline(new Source([1]), codec, new NoDisk(), new());
        var tasks = Enumerable.Range(0, 20).Select(i => pipeline.LoadAsync("x" + i, 16, default)).ToArray(); await pipeline.DisposeAsync(); await Task.WhenAll(tasks);
        Assert.Equal(0, pipeline.Metrics.Outstanding); Assert.Equal(0, pipeline.Metrics.ActiveDecodes); Assert.Equal(0, pipeline.Metrics.CachedBytes);
    }
    private static async Task<byte[]> HugeHeader()
    {
        var bytes = await Png(10, 10);
        // Keep IHDR structurally valid so the dimensions guard, rather than a CRC error,
        // rejects the 225 MP claim before WIC is asked to decode any pixel data.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 15000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 15000);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes.AsSpan(12, 17))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), ~crc);
        return bytes;
    }
    internal static async Task<byte[]> Png(int width, int height, int variant = 0)
    {
        using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[width * height * 4]; for (int offset = 0; offset < pixels.Length; offset += 4) { pixels[offset] = (byte)(180 - variant * 40); pixels[offset + 1] = (byte)(offset / 4 % width * 255 / width); pixels[offset + 2] = (byte)(80 + variant * 40); pixels[offset + 3] = 255; }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels); await encoder.FlushAsync(); stream.Seek(0);
        var result = new byte[(int)stream.Size]; await stream.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None); return result;
    }
    private sealed class Source(byte[] bytes) : IImageSourceProvider { public Task<byte[]?> ReadAsync(string key, CancellationToken cancellation) => Task.FromResult<byte[]?>(bytes); }
    private sealed class NoDisk : IThumbnailDiskCache
    { public Task<RasterPixels?> ReadAsync(string key, CancellationToken cancellation) => Task.FromResult<RasterPixels?>(null); public Task WriteAsync(string key, RasterPixels pixels, long maximumBytes, CancellationToken cancellation) => Task.CompletedTask; }
    private sealed class CountingCodec : IRasterCodec
    {
        public async Task<RasterPixels> DecodeAsync(ReadOnlyMemory<byte> encoded, int edge, CancellationToken cancellation) { await Task.Delay(15, cancellation); return new(edge, edge, new byte[edge * edge * 4]); }
        public Task<LocalCover> NormalizePhotoAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
