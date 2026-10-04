using TheOrder.Domain;

namespace TheOrder.Application;

public sealed class OnlineLibraryClient(OfflineLibraryClient offline, IOnlineCatalog catalog)
{
    public async Task<ClientResult> AddAsync(string workId, CancellationToken cancellation)
    {
        try { var work = await catalog.DetailsAsync(workId, false, cancellation).ConfigureAwait(false); return OfflineLibraryClient.Present(await offline.Required().AddAsync(work, cancellation).ConfigureAwait(false)); }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
    }
    public async Task<ClientResult> RefreshAsync(string gameId, CancellationToken cancellation)
    {
        var library = offline.Required(); var entry = library.Snapshot.Entries.FirstOrDefault(e => e.Id == gameId);
        if (entry?.Metadata is null) return new(ClientOutcome.ValidationFailure, "此条目尚未关联在线作品。");
        var token = await library.BeginRequestAsync(gameId, cancellation).ConfigureAwait(false);
        try
        {
            var work = await catalog.DetailsAsync(entry.Metadata.Id, true, cancellation).ConfigureAwait(false);
            return OfflineLibraryClient.Present(await library.CommitLinkAsync(gameId, work, token, preserveName: true, preserveMergedName: true, cancellation: cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
        finally { await library.FinishRequestAsync(token).ConfigureAwait(false); }
    }
    public async Task<ClientResult> RemoveWishlistAsync(string gameId, CancellationToken cancellation)
    {
        try { return OfflineLibraryClient.Present(await offline.Required().DeleteWishlistAsync(gameId, cancellation).ConfigureAwait(false)); }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
    }
}
public sealed partial class TransactionalLibrary
{
    public Task<CommitResult<bool>> DeleteWishlistAsync(string id, CancellationToken cancellation) => Mutate(games =>
    {
        var entry = Find(games, id); if (!entry.Installations.IsEmpty) throw new InvalidDataException("此条目已有本地安装，不能作为愿望条目移除。");
        return (games.Remove(entry), true);
    }, cancellation);
}
public sealed class CombinedCoverSources(LocalCoverSources local, OfflineLibraryClient library, IRemoteCoverRegistry registry, IImageSourceProvider remote) : IImageSourceProvider
{
    private readonly object gate = new();
    private LibrarySnapshot? indexed;
    private Dictionary<string, (string? Url, string Revision)> sources = [];
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellation)
    {
        var bytes = library.Capability.Loaded ? await local.ReadAsync(key, cancellation).ConfigureAwait(false) : null; if (bytes is not null) return bytes;
        if (library.Capability.Loaded)
        {
            lock (gate)
            {
                var snapshot = library.Required().Snapshot;
                if (!ReferenceEquals(snapshot, indexed))
                {
                    sources = [];
                    foreach (var entry in snapshot.Entries) { cancellation.ThrowIfCancellationRequested(); if (entry.LocalCover is null && ImageSourceKeys.For(entry) is { } id) sources[id] = (CoverSources.Selected(entry), entry.Metadata?.FetchedAt ?? ""); }
                    indexed = snapshot;
                }
                if (sources.TryGetValue(key, out var source)) registry.Register(source.Url, source.Revision);
            }
        }
        return await remote.ReadAsync(key, cancellation).ConfigureAwait(false);
    }
}
