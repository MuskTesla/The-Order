namespace TheOrder.Application;

public sealed class BookBibliographyService(IBookBibliographyProvider provider, IBookBibliographyCache? cache = null,
    IBookPublicationBindings? choices = null, Func<DateTimeOffset>? clock = null) : IBookBibliographyReader
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7), MaximumStaleAge = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim reads = new(1), writes = new(1);
    private readonly Dictionary<string, BookCacheValue> memory = new(StringComparer.Ordinal);
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public async Task<BookSearchResult> SearchAsync(BookSearchQuery query, bool refresh, CancellationToken cancellation)
    {
        if (!query.IsValid) throw new InvalidDataException("请填写原文标题或有效 ISBN。");
        var (value, cached, stale, warning) = await ReadAsync(query.Key, refresh, async token =>
        {
            var page = await provider.SearchAsync(query, token).ConfigureAwait(false);
            if (!BookValidation.Page(page) || query.Isbn is not null && page.Publications.Any(p => !p.Isbns.Contains(query.Isbn, StringComparer.Ordinal)))
                throw new BookProviderException(BookFailure.InvalidResponse, "书目 ISBN 或资料格式不一致。");
            return new BookCacheValue(query.Key, Now, Search: page);
        }, cancellation).ConfigureAwait(false);
        return new(value.Search!, cached, stale, warning);
    }
    public async Task<BookPublicationResult> LoadAsync(string id, bool refresh, CancellationToken cancellation)
    {
        if (!BookValidation.NdlId(id)) throw new InvalidDataException("书目编号无效。");
        var (value, cached, stale, warning) = await ReadAsync("publication:" + id, refresh, async token =>
        {
            var p = await provider.FetchAsync(id, token).ConfigureAwait(false);
            if (!BookValidation.Publication(p) || p.Id != id) throw new BookProviderException(BookFailure.InvalidResponse, "书目编号或资料格式不一致。");
            return new BookCacheValue("publication:" + id, Now, Publication: p);
        }, cancellation).ConfigureAwait(false);
        return new(value.Publication!, cached, stale, warning);
    }
    private async Task<(BookCacheValue Value, bool Cached, bool Stale, string? Warning)> ReadAsync(string key, bool refresh,
        Func<CancellationToken, Task<BookCacheValue>> fetch, CancellationToken cancellation)
    {
        await reads.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var previous = memory.GetValueOrDefault(key);
            if (previous is null && cache is not null)
                try { previous = await cache.ReadAsync(key, cancellation).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            if (!BookValidation.Cache(previous) || previous!.Rejected || previous.Key != key || previous.CachedAtUtc > Now.AddMinutes(5) || Now - previous.CachedAtUtc > MaximumStaleAge) previous = null;
            if (!refresh && previous is not null && Now - previous.CachedAtUtc < FreshFor)
            { Put(previous); return (previous, true, false, null); }
            try
            {
                var value = await fetch(cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested();
                if (!BookValidation.Cache(value)) throw new BookProviderException(BookFailure.InvalidResponse, "书目响应无效。");
                string? warning = null;
                if (cache is not null)
                    try { await cache.WriteAsync(value, cancellation).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warning = "当前书目已读取，本地缓存暂不可保存。"; }
                cancellation.ThrowIfCancellationRequested(); Put(value); return (value, false, false, warning);
            }
            catch (BookProviderException e) when (previous is not null && e.Failure is BookFailure.Offline or BookFailure.Timeout or BookFailure.RateLimited or BookFailure.Server)
            { cancellation.ThrowIfCancellationRequested(); Put(previous); return (previous, true, true, "暂时无法更新，显示上次书目资料。" + e.Message); }
            catch (BookProviderException e)
            {
                if (e.Failure is BookFailure.Unavailable or BookFailure.InvalidResponse or BookFailure.ResponseTooLarge)
                {
                    var rejected = new BookCacheValue(key, Now, Rejected: true); Put(rejected);
                    if (cache is not null)
                        try { await cache.WriteAsync(rejected, cancellation).ConfigureAwait(false); }
                        catch (Exception storage) when (storage is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        finally { reads.Release(); }
    }
    private void Put(BookCacheValue value)
    {
        memory.Remove(value.Key); memory.Add(value.Key, value);
        while (memory.Count > 64) memory.Remove(memory.Keys.First());
    }
    public async Task<BookPublicationBinding[]> BindingsAsync(BookSource source, CancellationToken cancellation)
    {
        if (!source.IsValid) throw new InvalidDataException("书籍身份无效。");
        if (choices is null) return [];
        var saved = await choices.ReadAsync(source.Identity, cancellation).ConfigureAwait(false);
        if (saved.Length > 64 || saved.Any(x => !BookValidation.Binding(x) || x.SourceIdentity != source.Identity)) throw new IOException("出版关联不可读取，原文件保留。");
        return saved.Where(x => x.SourceFingerprint == source.Fingerprint).ToArray();
    }
    public async Task ConfirmAsync(BookSource source, BookPublication publication, CancellationToken cancellation)
    {
        if (choices is null) throw new IOException("当前环境无法保存出版关联。");
        if (!source.IsValid || !BookValidation.Publication(publication) || publication.IsAggregate
            || source.Identity.Level is BookEntityLevel.Volume or BookEntityLevel.Publication && source.Isbns.Length > 0
                && !source.Isbns.Intersect(publication.Isbns, StringComparer.Ordinal).Any()) throw new InvalidDataException("该出版记录与当前单卷 ISBN 不一致，或属于聚合书目。");
        await writes.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // A search summary is not confirmation evidence. Reload the exact NDL
            // independent source record and ensure it did not change underneath UI.
            var loaded = await LoadAsync(publication.Id, false, cancellation).ConfigureAwait(false);
            if (loaded.IsStale) throw new IOException("出版资料暂时无法更新，保留已有关联；请更新后再确认新的关联。");
            var verified = loaded.Publication;
            if (!SameReviewedPublication(publication, verified))
                throw new InvalidDataException("书目已变化，请重新核对后确认。");
            var existing = await choices.ReadAsync(source.Identity, cancellation).ConfigureAwait(false);
            if (existing.Length > 64 || existing.Any(x => !BookValidation.Binding(x) || x.SourceIdentity != source.Identity)) throw new IOException("出版关联不可更新，原文件保留。");
            var updated = existing.Where(x => x.PublicationId != publication.Id).Append(new BookPublicationBinding(source.Identity,
                source.Fingerprint, publication.Id, Now, "用户核对出版记录 · " + source.Identity.Key + " → NDL " + publication.Id)).ToArray();
            if (updated.Length > 64) throw new IOException("该条目的出版关联已达上限。");
            cancellation.ThrowIfCancellationRequested(); await choices.WriteAsync(source.Identity, updated, cancellation).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    private static bool SameReviewedPublication(BookPublication reviewed, BookPublication current)
    {
        static bool SameSet(string[] left, string[] right) => left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
        // Edition-level facts authorize the choice. Freshness, contributor wording
        // and bibliographic-series annotations can change without changing that choice.
        return reviewed.Id == current.Id && reviewed.Title == current.Title && reviewed.BaseTitle == current.BaseTitle
            && reviewed.Volume == current.Volume && reviewed.Edition == current.Edition
            && reviewed.PublicationDate == current.PublicationDate && !current.IsAggregate
            && SameSet(reviewed.Isbns, current.Isbns) && SameSet(reviewed.SetIsbns, current.SetIsbns)
            && SameSet(reviewed.Publishers, current.Publishers) && SameSet(reviewed.Languages, current.Languages);
    }
    public async Task RemoveAsync(BookSource source, string publicationId, CancellationToken cancellation)
    {
        if (!source.IsValid || !BookValidation.NdlId(publicationId) || choices is null) throw new InvalidDataException("出版关联无效。");
        await writes.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var saved = await choices.ReadAsync(source.Identity, cancellation).ConfigureAwait(false);
            if (saved.Length > 64 || saved.Any(x => !BookValidation.Binding(x) || x.SourceIdentity != source.Identity)) throw new IOException("出版关联不可更新，原文件保留。");
            await choices.WriteAsync(source.Identity, saved.Where(x => x.PublicationId != publicationId).ToArray(), cancellation).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
}
