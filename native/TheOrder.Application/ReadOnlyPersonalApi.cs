using System.Collections.Immutable;

namespace TheOrder.Application;

// Used only by explicitly opted-in, isolated Test acceptance. Production never selects this decorator.
public sealed class ReadOnlyPersonalApi(INextMoePersonalApi inner) : INextMoePersonalApi
{
    public Task<CloudPage<CloudPlaytime>> PlaytimesAsync(string? cursor, CancellationToken token) => inner.PlaytimesAsync(cursor, token);
    public Task<CloudPage<CloudWorkState>> StatesAsync(string? cursor, CancellationToken token) => inner.StatesAsync(cursor, token);
    public Task<CloudPage<CloudRating>> RatingsAsync(string? cursor, CancellationToken token) => inner.RatingsAsync(cursor, token);
    public Task<CloudPage<CloudFolder>> FoldersAsync(string? cursor, CancellationToken token) => inner.FoldersAsync(cursor, token);
    public Task<CloudPage<string>> FolderItemsAsync(string folder, string? cursor, CancellationToken token) => inner.FolderItemsAsync(folder, cursor, token);
    public Task<ImmutableArray<PersonalWriteResult>> WriteAsync(PersonalSyncKind kind, string? folder, ImmutableArray<PersonalWrite> items, string key, CancellationToken token)
        => throw new OnlineException(OnlineFailure.Forbidden);
    public Task<CloudFolder> CreateFolderAsync(string name, string key, CancellationToken token) => throw new OnlineException(OnlineFailure.Forbidden);
    public Task DeleteFolderItemAsync(string folder, string work, CancellationToken token) => throw new OnlineException(OnlineFailure.Forbidden);
}
