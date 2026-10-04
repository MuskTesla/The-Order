using System.Collections.Immutable;
using System.Security.Cryptography;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record ImageOptions(int DecodeConcurrency = 2, long MemoryBytes = 64 * 1024 * 1024, long DiskBytes = 128 * 1024 * 1024, int MaximumEdge = 1600);
public sealed record RasterPixels(int Width, int Height, byte[] Bgra) { public long Bytes => Bgra.LongLength; }
public sealed record ImageResult(RasterPixels? Pixels, string Status);
public sealed record ImageMetrics(long Requests, long Loads, long Hits, long Misses, long Errors, int Outstanding, int ActiveDecodes, int MaximumActiveDecodes, long CachedBytes, int CachedItems);
public interface IRasterCodec
{
    Task<RasterPixels> DecodeAsync(ReadOnlyMemory<byte> encoded, int edge, CancellationToken cancellation);
    Task<LocalCover> NormalizePhotoAsync(string path, CancellationToken cancellation);
}
public interface IThumbnailDiskCache
{
    Task<RasterPixels?> ReadAsync(string key, CancellationToken cancellation);
    Task WriteAsync(string key, RasterPixels pixels, long maximumBytes, CancellationToken cancellation);
}
public interface IImageSourceProvider { Task<byte[]?> ReadAsync(string key, CancellationToken cancellation); }
public interface IImagePipeline : IAsyncDisposable
{
    Task<ImageResult> LoadAsync(string? key, int edge, CancellationToken cancellation);
    ImageMetrics Metrics { get; }
}

// Cache owns immutable decoded buffers. Views own only currently realized image surfaces.
// Decoder slots bound simultaneous IO/decode; realized views cancel recycled requests.
public sealed class ImagePipeline(IImageSourceProvider source, IRasterCodec codec, IThumbnailDiskCache disk, ImageOptions options) : IImagePipeline
{
    private readonly SemaphoreSlim slots = new(Math.Clamp(options.DecodeConcurrency, 1, 8));
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, RasterPixels Pixels)>> cache = [];
    private readonly LinkedList<(string Key, RasterPixels Pixels)> lru = [];
    private readonly HashSet<Task> operations = [];
    private long requests, loads, hits, misses, errors, bytes;
    private int outstanding, active, maximum;
    private bool disposed;
    public ImageMetrics Metrics { get { lock (gate) return new(requests, loads, hits, misses, errors, outstanding, active, maximum, bytes, cache.Count); } }
    public Task<ImageResult> LoadAsync(string? key, int edge, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (disposed) return Task.FromResult(new ImageResult(null, "cancelled"));
            requests++;
            if (string.IsNullOrEmpty(key)) return Task.FromResult(new ImageResult(null, "placeholder"));
            var operation = Task.Run(() => LoadCoreAsync(key, Math.Clamp(edge, 16, Math.Clamp(options.MaximumEdge, 16, 1600)), cancellation));
            operations.Add(operation);
            _ = operation.ContinueWith(t => { lock (gate) operations.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }
    private async Task<ImageResult> LoadCoreAsync(string key, int edge, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token); var token = linked.Token;
        bool acquired = false, decoding = false;
        var cacheKey = key + "-" + edge;
        lock (gate) outstanding++;
        try
        {
            token.ThrowIfCancellationRequested();
            lock (gate) if (TryCached(cacheKey) is { } cached) { hits++; return new(cached, "memory"); }
            await slots.WaitAsync(token).ConfigureAwait(false); acquired = true;
            lock (gate) if (TryCached(cacheKey) is { } cached) { hits++; return new(cached, "memory"); }
            lock (gate) misses++;
            RasterPixels? pixels = null;
            try { pixels = await disk.ReadAsync(cacheKey, token).ConfigureAwait(false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException) { }
            if (pixels is null)
            {
                var encoded = await source.ReadAsync(key, token).ConfigureAwait(false);
                if (encoded is null) return new(null, "unavailable");
                lock (gate) { active++; maximum = Math.Max(maximum, active); decoding = true; }
                pixels = await codec.DecodeAsync(encoded, edge, token).ConfigureAwait(false);
                lock (gate) { loads++; active--; decoding = false; }
                try { await disk.WriteAsync(cacheKey, pixels, options.DiskBytes, token).ConfigureAwait(false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException) { }
            }
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (pixels.Bytes <= options.MemoryBytes && !cache.ContainsKey(cacheKey))
                {
                    while (bytes + pixels.Bytes > options.MemoryBytes && lru.Last is { } oldest) { bytes -= oldest.Value.Pixels.Bytes; cache.Remove(oldest.Value.Key); lru.RemoveLast(); }
                    cache.Add(cacheKey, lru.AddFirst((cacheKey, pixels))); bytes += pixels.Bytes;
                }
            }
            return new(pixels, "loaded");
        }
        catch (OperationCanceledException) { return new(null, "cancelled"); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { lock (gate) errors++; return new(null, "invalid"); }
        finally { lock (gate) { outstanding--; if (decoding) active--; } if (acquired) slots.Release(); }
    }
    private RasterPixels? TryCached(string key)
    { if (!cache.TryGetValue(key, out var node)) return null; lru.Remove(node); lru.AddFirst(node); return node.Value.Pixels; }
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate) { if (disposed) return; disposed = true; stopping.Cancel(); pending = [.. operations]; }
        await Task.WhenAll(pending).ConfigureAwait(false);
        lock (gate) { cache.Clear(); lru.Clear(); bytes = 0; }
        stopping.Dispose(); slots.Dispose();
    }
}

public sealed class LocalCoverSources(OfflineLibraryClient client) : IImageSourceProvider
{
    private readonly object gate = new();
    private LibrarySnapshot? indexed;
    private Dictionary<string, string> data = [];
    public static string Key(LocalCover cover) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cover.DataUrl)));
    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            var snapshot = client.Required().Snapshot;
            if (!ReferenceEquals(snapshot, indexed))
            {
                data = [];
                foreach (var item in snapshot.Entries) { cancellation.ThrowIfCancellationRequested(); if (item.LocalCover is { } cover) data.TryAdd(Key(cover), cover.DataUrl); }
                indexed = snapshot;
            }
            if (!data.TryGetValue(key, out var url)) return null;
            const string prefix = "data:image/jpeg;base64,";
            return url.StartsWith(prefix, StringComparison.Ordinal) ? Convert.FromBase64String(url[prefix.Length..]) : null;
        }
    }, cancellation);
}
