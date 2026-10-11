using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record CatalogBrowsePreference(bool ShowNsfw = false);
public sealed record CatalogBrowsePreferenceRead(CatalogBrowsePreference Preference, string? Warning = null, bool ReadOnly = false);
public interface ICatalogBrowsePreferencesStore
{
    Task<CatalogBrowsePreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(CatalogBrowsePreference preference, CancellationToken cancellation = default);
}

// Runtime browsing intent is separate from library data, recognition and image selection.
public sealed class CatalogBrowsePolicy
{
    private readonly object gate = new();
    private CatalogBrowseScope value = new(0, false);
    public event Action? Changed;
    public CatalogBrowseScope Capture() { lock (gate) return value; }
    public void Set(bool showNsfw)
    {
        lock (gate) { if (value.ShowNsfw == showNsfw) return; value = new(value.Generation + 1, showNsfw); }
        Changed?.Invoke();
    }
    public void Check(CatalogBrowseScope scope, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (scope != Capture()) throw new OperationCanceledException("Catalog browsing preference changed", cancellation);
    }
}
public readonly record struct CatalogBrowseScope(long Generation, bool ShowNsfw);

public interface IContentFilteredCatalog
{
    Task<OnlinePage> SearchVisibleAsync(string query, string? cursor, bool showNsfw, CancellationToken cancellation);
    Task<CatalogEntityWorks> TagWorksVisibleAsync(string id, string? cursor, bool showNsfw, CancellationToken cancellation);
}

// Only discovery search and tag-result lists are restricted. Direct local metadata,
// recognition, cover choice and refresh retain their original catalog contract.
public sealed class CatalogBrowseClient(IOnlineCatalog catalog, IEntityCatalog entities,
    IContentFilteredCatalog filtered, CatalogBrowsePolicy policy) : IOnlineCatalog, IEntityCatalog
{
    private async Task<T> Current<T>(Func<bool, Task<T>> request, CancellationToken cancellation)
    {
        var scope = policy.Capture(); policy.Check(scope, cancellation);
        var result = await request(scope.ShowNsfw).ConfigureAwait(false);
        policy.Check(scope, cancellation); return result;
    }
    public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation) =>
        Current(show => filtered.SearchVisibleAsync(query, cursor, show, cancellation), cancellation);
    public Task<CatalogEntityWorks> EntityWorksAsync(CatalogEntityKind kind, string id, string? cursor, CancellationToken cancellation) =>
        Current(show => kind == CatalogEntityKind.Tag ? filtered.TagWorksVisibleAsync(id, cursor, show, cancellation)
            : entities.EntityWorksAsync(kind, id, cursor, cancellation), cancellation);
    public Task<CatalogEntityDetail> EntityAsync(CatalogEntityKind kind, string id, CancellationToken cancellation) =>
        Current(_ => entities.EntityAsync(kind, id, cancellation), cancellation);
    public Task<CatalogEntityPage> TraitCharactersAsync(string id, string? cursor, CancellationToken cancellation) =>
        Current(_ => entities.TraitCharactersAsync(id, cursor, cancellation), cancellation);
    public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation) => catalog.DetailsAsync(id, refresh, cancellation);
    public Task TestConnectionAsync(CancellationToken cancellation) => catalog.TestConnectionAsync(cancellation);
    public void ClearCache() => catalog.ClearCache();
}
