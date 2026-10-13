using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

public sealed partial class NextMoeCatalog : IRelatedGameIdentityReader
{
    public Task<ImmutableArray<RelatedGameIdentity>> LoadRelatedIdentitiesAsync(ImmutableArray<CatalogEntityRef> games, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (games.IsDefault || games.Length > 100 || games.Any(g => !CatalogRelatedWorks.Eligible(g) || !ValidId(g.Id)))
            throw new OnlineException(OnlineFailure.InvalidRequest);
        if (games.IsEmpty) return Task.FromResult(ImmutableArray<RelatedGameIdentity>.Empty);
        var requested = games.GroupBy(g => g.Id, StringComparer.Ordinal).Where(g => g.Select(x => x.KunGalGalgameId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().KunGalGalgameId!, StringComparer.Ordinal);
        if (requested.Count == 0) return Task.FromResult(ImmutableArray<RelatedGameIdentity>.Empty);
        return transport.Operation(async scope =>
        {
            var parameters = Parameters(); parameters["ids"] = string.Join(',', requested.Keys);
            parameters["include"] = "refs"; parameters["limit"] = "100";
            using var response = await transport.GetAsync("catalog/works", parameters, scope).ConfigureAwait(false);
            RequireList(response.RootElement, 100);
            var result = ImmutableArray.CreateBuilder<RelatedGameIdentity>();
            foreach (var group in Rows(response.RootElement, "items").GroupBy(row => Text(row, "id"), StringComparer.Ordinal))
            {
                if (!requested.TryGetValue(group.Key, out var claimedId)) continue;
                var rows = group.ToArray();
                if (rows.Any(row => KunGalGalgameId(row) != claimedId)) continue;
                var references = rows.SelectMany(row => Rows(row, "refs")).Where(row => Text(row, "source") == "bangumi").ToArray();
                if (references.Any(row => !BangumiRelationIdentityResolver.ValidSubjectId(Text(row, "external_id")))) continue;
                var subjects = references.Select(row => Text(row, "external_id")).Distinct(StringComparer.Ordinal).ToArray();
                if (subjects.Length == 1) result.Add(new(group.Key, claimedId, subjects[0]));
            }
            scope.Checkpoint(OnlineAcceptanceStage.Mapped);
            return result.ToImmutable();
        }, cancellation);
    }
}
