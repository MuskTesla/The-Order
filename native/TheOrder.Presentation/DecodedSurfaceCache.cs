using System.Runtime.CompilerServices;
using TheOrder.Application;

namespace TheOrder.Presentation;

// Keys are the immutable buffers already owned by the bounded image pipeline.
// Neither this table nor its surfaces retain evicted decoded buffers. A new
// decode is a new identity, even when a cover name or dimensions are unchanged.
public sealed class DecodedSurfaceCache<TSurface>(Func<RasterPixels, TSurface> create) where TSurface : class
{
    private readonly ConditionalWeakTable<RasterPixels, TSurface> surfaces = new();
    private long requests, created, copiedBytes;
    public (long Requests, long Created, long CopiedBytes) Metrics =>
        (Interlocked.Read(ref requests), Interlocked.Read(ref created), Interlocked.Read(ref copiedBytes));

    public TSurface Get(RasterPixels pixels)
    {
        Interlocked.Increment(ref requests);
        return surfaces.GetValue(pixels, Create);
    }
    private TSurface Create(RasterPixels pixels)
    {
        var surface = create(pixels);
        Interlocked.Increment(ref created);
        Interlocked.Add(ref copiedBytes, pixels.Bytes);
        return surface;
    }
}
