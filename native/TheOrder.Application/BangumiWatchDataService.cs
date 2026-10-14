namespace TheOrder.Application;

// One bounded index and a weekly, on-demand update. No timer, recursive fetch,
// credentials, library writes, or unowned background task.
public sealed class BangumiWatchDataService(IBangumiWatchDataProvider provider, IBangumiWatchSnapshotStore store,
    Func<DateTimeOffset>? clock = null) : IBangumiWatchLinksReader, IAsyncDisposable
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(7);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim gate = new(1);
    private BangumiWatchSnapshot? snapshot; private BangumiWatchDataIndex? index; private DateTimeOffset attempt; private bool disposed;
    public async Task<WatchDataResult> ReadAsync(WatchDataQuery query, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); await InitializeAsync(cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); var result = index!.Find(query); return result with { Message = result.Message ?? snapshot?.UpdateWarning }; }
        finally { gate.Release(); }
    }
    public async Task<bool> UpdateIfDueAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); await InitializeAsync(cancellation).ConfigureAwait(false);
            var current = now(); if (current - attempt < UpdateInterval) return false;
            attempt = current; var previous = snapshot!; BangumiWatchSnapshot next; BangumiWatchDataIndex parsed;
            try
            {
                next = await provider.UpdateAsync(previous, cancellation).ConfigureAwait(false);
                parsed = await Task.Run(() => new BangumiWatchDataIndex(next), cancellation).ConfigureAwait(false);
                if (parsed.InputItems < index!.InputItems * .75 || parsed.IndexedItems < index.IndexedItems * .75) throw new InvalidDataException("放送目录不完整。");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException or ArgumentException)
            {
                next = previous with { CheckedAtUtc = current, UpdateWarning = "放送站点数据暂未更新，继续使用已保存的数据。" }; parsed = index!;
            }
            next = next with { CheckedAtUtc = current }; cancellation.ThrowIfCancellationRequested();
            // The old durable snapshot survives a failed replacement. Once an
            // atomic write begins the store completes it, preserving consistency.
            try { await store.WriteAsync(next, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            { return false; }
            snapshot = next;
            index = parsed;
            return true;
        }
        finally { gate.Release(); }
    }
    private async Task InitializeAsync(CancellationToken cancellation)
    {
        if (index is not null) return;
        var built = await provider.BuiltInAsync(cancellation).ConfigureAwait(false);
        var selected = built;
        try
        {
            var cached = await store.ReadAsync(cancellation).ConfigureAwait(false);
            if (cached is not null && cached.Primary.UpdatedAtUtc >= built.Primary.UpdatedAtUtc && cached.CheckedAtUtc <= now().AddMinutes(5))
            {
                await Task.Run(() => new BangumiWatchDataIndex(cached), cancellation).ConfigureAwait(false); selected = cached;
                // Correct bundled provenance metadata for identical data bytes,
                // without discarding the cache's successful check timestamp.
                if (cached.Primary.Revision == built.Primary.Revision && cached.Primary.Sha256 == built.Primary.Sha256) selected = selected with { Primary = built.Primary };
                if (cached.Supplement?.Revision == built.Supplement?.Revision && cached.Supplement?.Sha256 == built.Supplement?.Sha256) selected = selected with { Supplement = built.Supplement };
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException) { }
        index = await Task.Run(() => new BangumiWatchDataIndex(selected), cancellation).ConfigureAwait(false); snapshot = selected; attempt = selected.CheckedAtUtc;
    }
    public async ValueTask DisposeAsync() { await gate.WaitAsync().ConfigureAwait(false); try { disposed = true; snapshot = null; index = null; } finally { gate.Release(); } }
}
