using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class DecodedSurfaceCacheTests
{
    private sealed record Surface(byte[] Copy);
    [Fact]
    public void RepeatedViewsReuseTheSameDecodedSurfaceWithoutCopyingAgain()
    {
        var cache = new DecodedSurfaceCache<Surface>(p => new((byte[])p.Bgra.Clone()));
        var pixels = new RasterPixels(2, 2, new byte[16]);
        var original = cache.Get(pixels);
        for (int i = 0; i < 5000; i++) Assert.Same(original, cache.Get(pixels));
        Assert.Equal((5001L, 1L, 16L), cache.Metrics);
    }
    [Fact]
    public void NewDecodeWithSameDimensionsCannotReusePreviousArtwork()
    {
        var cache = new DecodedSurfaceCache<Surface>(p => new((byte[])p.Bgra.Clone()));
        var previous = new RasterPixels(1, 1, [0, 0, 0, 255]);
        var replacement = new RasterPixels(1, 1, [255, 255, 255, 255]);
        var first = cache.Get(previous); var next = cache.Get(replacement);
        Assert.NotSame(first, next);
        Assert.Equal(replacement.Bgra, next.Copy); Assert.Equal(previous.Bgra, first.Copy);
        Assert.Equal((2L, 2L, 8L), cache.Metrics);
    }
    [Fact]
    public void EqualValueBuffersAreSeparateOwnershipIdentities()
    {
        var cache = new DecodedSurfaceCache<object>(_ => new());
        byte[] shared = [0, 0, 0, 255];
        var a = new RasterPixels(1, 1, shared); var b = new RasterPixels(1, 1, shared);
        Assert.Equal(a, b); Assert.NotSame(cache.Get(a), cache.Get(b));
    }
}
