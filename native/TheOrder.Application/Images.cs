using System.Collections.Immutable;
using System.Security.Cryptography;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record ImageOptions(int DecodeConcurrency = 2, long MemoryBytes = 64 * 1024 * 1024, long DiskBytes = 128 * 1024 * 1024, int MaximumEdge = 1600, int MemoryItems = 512, int PendingRequests = 256);
public sealed record RasterPixels(int Width, int Height, byte[] Bgra) { public long Bytes => Bgra.LongLength; }
public sealed record ImageResult(RasterPixels? Pixels, string Status);
public sealed record ImageMetrics(long Requests, long Loads, long Hits, long Misses, long Errors, int Outstanding, int ActiveDecodes, int MaximumActiveDecodes, long CachedBytes, int CachedItems, int PendingOperations = 0, long Coalesced = 0, long Rejected = 0, int PendingWaiters = 0);
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
    bool TryGet(string key,int edge,out RasterPixels? pixels){pixels=null;return false;}
    Task<ImageResult> LoadAsync(string? key, int edge, CancellationToken cancellation);
    ImageMetrics Metrics { get; }
}

// Cache owns immutable decoded buffers. Views own only currently realized image surfaces.
// Decoder slots bound simultaneous IO/decode; realized views cancel recycled requests.
public sealed class ImagePipeline(IImageSourceProvider source, IRasterCodec codec, IThumbnailDiskCache disk, ImageOptions options) : IImagePipeline
{
    public bool TryGet(string key,int edge,out RasterPixels? pixels)
    {lock(gate){pixels=TryCachedImage(key,edge);if(pixels is not null)hits++;return pixels is not null;}}
    private readonly SemaphoreSlim slots = new(Math.Clamp(options.DecodeConcurrency, 1, 8));
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, RasterPixels Pixels)>> cache = [];
    private readonly LinkedList<(string Key, RasterPixels Pixels)> lru = [];
    private sealed class SharedRequest(CancellationToken stopping)
    {
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public Task<ImageResult> Work = null!;
        public int Waiters;
    }
    private readonly Dictionary<string, SharedRequest> operations = [];
    private long requests, loads, hits, misses, errors, bytes;
    private int outstanding, active, maximum, waiters;
    private bool disposed;
    private Task? disposal;
    private long coalesced, rejected;
    public ImageMetrics Metrics { get { lock (gate) return new(requests, loads, hits, misses, errors, outstanding, active, maximum, bytes, cache.Count, operations.Count, coalesced, rejected, waiters); } }
    public Task<ImageResult> LoadAsync(string? key, int edge, CancellationToken cancellation)
        => LoadRequestAsync(key, edge, cancellation, countRequest: true);
    private Task<ImageResult> LoadRequestAsync(string? key, int edge, CancellationToken cancellation, bool countRequest)
    {
        lock (gate)
        {
            if (disposed) return Task.FromResult(new ImageResult(null, "cancelled"));
            if (countRequest) requests++;
            if (cancellation.IsCancellationRequested) return Task.FromResult(new ImageResult(null, "cancelled"));
            if (string.IsNullOrEmpty(key)) return Task.FromResult(new ImageResult(null, "placeholder"));
            edge = Math.Clamp(edge, 16, Math.Clamp(options.MaximumEdge, 16, ArtworkSizing.MaximumEdge));
            var cacheKey = key + "-" + edge;
            if (TryCachedImage(key, edge) is { } cached) { hits++; return Task.FromResult(new ImageResult(cached, "memory")); }
            if (waiters >= Math.Clamp(options.PendingRequests, 1, 1024)) { rejected++; return Task.FromResult(new ImageResult(null, "busy")); }
            operations.TryGetValue(cacheKey, out var shared);
            // A completed abandoned request may still be waiting for its cleanup continuation.
            if (shared is not null && shared.Work.IsCompleted && shared.Cancellation.IsCancellationRequested)
            { operations.Remove(cacheKey); shared = null; }
            if (shared is null)
            {
                if (operations.Count >= Math.Clamp(options.PendingRequests, 1, 1024)) { rejected++; return Task.FromResult(new ImageResult(null, "busy")); }
                shared = new(stopping.Token); operations.Add(cacheKey, shared);
                var request = shared;
                request.Work = Task.Run(() => LoadCoreAsync(key, edge, request.Cancellation.Token));
                _ = request.Work.ContinueWith(completed =>
                {
                    // A recycled/cancelled view may no longer await an unexpected decoder fault.
                    if (completed.IsFaulted) _ = completed.Exception;
                    lock (gate)
                    {
                        if (operations.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, request)) operations.Remove(cacheKey);
                        request.Cancellation.Dispose();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else coalesced++;
            shared.Waiters++; waiters++;
            return WaitAsync(shared, key, edge, shared.Cancellation.IsCancellationRequested, cancellation);
        }
    }
    private async Task<ImageResult> WaitAsync(SharedRequest request, string key, int edge, bool abandoned, CancellationToken cancellation)
    {
        ImageResult result;
        try { result = await request.Work.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(null, "cancelled"); }
        finally { lock (gate) { request.Waiters--; waiters--; if (request.Waiters == 0 && !request.Work.IsCompleted) request.Cancellation.Cancel(); } }
        // New consumers wait within the same waiter budget until abandoned IO/decode drains,
        // then enter ordinary admission again. Never inherit another view's cancellation.
        if (abandoned && result.Status == "cancelled" && !cancellation.IsCancellationRequested)
            return await LoadRequestAsync(key, edge, cancellation, countRequest: false).ConfigureAwait(false);
        return result;
    }
    private async Task<ImageResult> LoadCoreAsync(string key, int edge, CancellationToken cancellation)
    {
        var token = cancellation;
        bool acquired = false, decoding = false;
        var cacheKey = key + "-" + edge;
        lock (gate) outstanding++;
        try
        {
            token.ThrowIfCancellationRequested();
            lock (gate) if (TryCachedImage(key, edge) is { } cached) { hits++; return new(cached, "memory"); }
            await slots.WaitAsync(token).ConfigureAwait(false); acquired = true;
            lock (gate) if (TryCachedImage(key, edge) is { } cached) { hits++; return new(cached, "memory"); }
            lock (gate) misses++;
            RasterPixels? pixels = null;
            try { pixels = await disk.ReadAsync(cacheKey, token).ConfigureAwait(false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException) { }
            // Import prepares full-resolution CGs. Reuse them after reopening instead of
            // decoding a second bitmap for every window/DPI bucket. Cover requests stay small.
            if (pixels is null && options.MaximumEdge >= ArtworkSizing.MaximumEdge && edge >= 1200 && edge < ArtworkSizing.MaximumEdge)
                try { pixels = await disk.ReadAsync(key + "-" + ArtworkSizing.MaximumEdge, token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException) { }
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
                if (pixels.Bytes <= options.MemoryBytes && options.MemoryItems > 0 && !cache.ContainsKey(cacheKey))
                {
                    while ((bytes + pixels.Bytes > options.MemoryBytes || cache.Count >= options.MemoryItems) && lru.Last is { } oldest) { bytes -= oldest.Value.Pixels.Bytes; cache.Remove(oldest.Value.Key); lru.RemoveLast(); }
                    cache.Add(cacheKey, lru.AddFirst((cacheKey, pixels))); bytes += pixels.Bytes;
                }
            }
            return new(pixels, "loaded");
        }
        catch (OperationCanceledException) { return new(null, "cancelled"); }
        catch (Exception e) when (e is IOException or InvalidDataException or DataIsolationException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { lock (gate) errors++; return new(null, "invalid"); }
        finally { lock (gate) { outstanding--; if (decoding) active--; } if (acquired) slots.Release(); }
    }
    private RasterPixels? TryCached(string key)
    { if (!cache.TryGetValue(key, out var node)) return null; lru.Remove(node); lru.AddFirst(node); return node.Value.Pixels; }
    private RasterPixels? TryCachedImage(string key, int edge)
    {
        if (TryCached(key + "-" + edge) is { } exact) return exact;
        if (edge >= 1200 && edge < ArtworkSizing.MaximumEdge)
            foreach (var larger in new[] { ArtworkSizing.MaximumEdge, 2560, 1920, 1600 })
                if (larger > edge && TryCached(key + "-" + larger) is { } prepared) return prepared;
        return null;
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true; stopping.Cancel();
            return new(disposal = DrainAsync([.. operations.Values.Select(r => r.Work)]));
        }
    }
    private async Task DrainAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        finally
        {
            lock (gate) { cache.Clear(); lru.Clear(); bytes = 0; }
            stopping.Dispose(); slots.Dispose();
        }
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
