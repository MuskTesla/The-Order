using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Presentation;

// Capture the complete committed library, independent of the current shelf query.
// Catalog versions stay distinct; names, source aliases and parent relations are not membership.
public sealed class CatalogShelfMembership
{
    private readonly ImmutableHashSet<string> workIds;
    public CatalogShelfMembership(LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Access == LibraryAccess.InvalidInput)
            throw new InvalidOperationException(UiText.Get("书架未能读取，暂时无法区分相关作品。"));
        workIds = snapshot.Entries.Where(e => e.Metadata is not null)
            .Select(e => e.Metadata!.Id).ToImmutableHashSet(StringComparer.Ordinal);
    }

    public bool Contains(WorkMetadata work) => workIds.Contains(work.Id);
}
