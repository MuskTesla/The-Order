using TheOrder.Domain;
namespace TheOrder.Application;
public interface IEntityCatalog
{
    Task<CatalogEntityDetail> EntityAsync(CatalogEntityKind kind,string id,CancellationToken cancellation);
    Task<CatalogEntityWorks> EntityWorksAsync(CatalogEntityKind kind,string id,string? cursor,CancellationToken cancellation);
    Task<CatalogEntityPage> TraitCharactersAsync(string id,string? cursor,CancellationToken cancellation);
}
