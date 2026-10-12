using System.Collections.Immutable;
using TheOrder.Application;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

public sealed partial class NextMoeCatalog : ICatalogSyncIdentities
{
    public Task<ImmutableArray<string>> ConfirmWorkIdsAsync(ImmutableArray<string> ids, CancellationToken cancellation)
    {
        if (ids.IsDefault || ids.Length is < 1 or > 100 || ids.Any(x => !ValidId(x)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation(async budget =>
        {
            var parameters = Parameters(); parameters["ids"] = string.Join(',', ids); parameters["fields"] = "id,object";
            using var json = await transport.GetAsync("catalog/works", parameters, budget).ConfigureAwait(false);
            RequireList(json.RootElement); var present = ImmutableArray.CreateBuilder<string>();
            foreach (var row in Rows(json.RootElement, "items"))
            {
                var id = Text(row, "id");
                if (Text(row, "object") != "work" || !ids.Contains(id) || present.Contains(id)) throw new OnlineException(OnlineFailure.InvalidResponse);
                present.Add(id);
            }
            // Missing/merged IDs cannot authorize a personal-data write; the coordinator requires every target.
            return present.ToImmutable();
        }, cancellation);
    }
}
