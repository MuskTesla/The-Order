using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;
namespace TheOrder.Infrastructure.Online;
public sealed partial class NextMoeCatalog
{
    private static string Family(CatalogEntityKind kind)=>kind switch {CatalogEntityKind.Character=>"characters",CatalogEntityKind.Company=>"companies",CatalogEntityKind.CreditName=>"credit-names",CatalogEntityKind.Person=>"persons",CatalogEntityKind.Tag=>"tags",CatalogEntityKind.Engine=>"engines",CatalogEntityKind.Series=>"series",CatalogEntityKind.Trait=>"traits",_=>throw new OnlineException(OnlineFailure.InvalidRequest)};
    public Task<CatalogEntityDetail> EntityAsync(CatalogEntityKind kind,string id,CancellationToken cancellation)
    {
        if(!ValidId(id))throw new OnlineException(OnlineFailure.InvalidRequest);var family=Family(kind);
        return transport.Operation(async scope=> {
            var p=Parameters();var include=kind switch {CatalogEntityKind.Character=>"image,figure,intros,refs,aliases,traits,gender,birthday,height_cm,weight_kg,measurements,blood_type,instance_of_id,work_count",CatalogEntityKind.Company=>"aliases,logo,intros,links",CatalogEntityKind.CreditName=>"aliases,photo,siblings,intros,links,refs",CatalogEntityKind.Series=>"intros,refs",CatalogEntityKind.Tag=>"intros",CatalogEntityKind.Trait=>"aliases,description,intros",_=>""};
            if(include.Length>0)p["include"]=include;
            var current=id;var visited=new HashSet<string>();
            for(var i=0;i<4;i++) {
                if(!visited.Add(current))throw new OnlineException(OnlineFailure.MergeLoop);
                try {using var body=await transport.GetAsync("catalog/"+family+"/"+current,p,scope).ConfigureAwait(false);
                    var detail=CatalogDisplayDto.Entity(body.RootElement,kind,current);
                    if(kind==CatalogEntityKind.Person){using var names=await transport.GetAsync("catalog/persons/"+current+"/credit-names",Parameters(),scope).ConfigureAwait(false);RequireList(names.RootElement,500);detail=detail with {Related=[..Rows(names.RootElement,"items").Where(n=>ValidId(Text(n,"id"))).Select(n=>new CatalogEntityRef(CatalogEntityKind.CreditName,Text(n,"id"),Localized(n)) { Names=CatalogDisplayDto.Names(n) })]};}
                    scope.Checkpoint(OnlineAcceptanceStage.Mapped);return detail;
                }catch(MergedWorkException merged){current=merged.CanonicalId;}
            }
            throw new OnlineException(OnlineFailure.MergeLimit);
        },cancellation);
    }
    public Task<CatalogEntityWorks> EntityWorksAsync(CatalogEntityKind kind,string id,string? cursor,CancellationToken cancellation)
    {
        if(!ValidId(id)||cursor is not null&&(!cursor.StartsWith("cur_",StringComparison.Ordinal)||cursor.Length>512))throw new OnlineException(OnlineFailure.InvalidRequest);
        if(kind==CatalogEntityKind.Person)return Task.FromResult(new CatalogEntityWorks([],null));
        return transport.Operation(async scope=> {
            var p=Parameters();p["limit"]="20";if(cursor is not null)p["cursor"]=cursor;
            var path=kind switch {CatalogEntityKind.Character=>"catalog/characters/"+id+"/appearances",CatalogEntityKind.CreditName=>"catalog/credit-names/"+id+"/credits",CatalogEntityKind.Company or CatalogEntityKind.Tag or CatalogEntityKind.Engine or CatalogEntityKind.Series=>"catalog/works",_=>throw new OnlineException(OnlineFailure.InvalidRequest)};
            if(path=="catalog/works") {p[kind switch {CatalogEntityKind.Company=>"company_id",CatalogEntityKind.Tag=>"tag_id",CatalogEntityKind.Engine=>"engine_id",_=>"series_id"}]=id;p["include"]="titles,refs,covers";}
            using var body=await transport.GetAsync(path,p,scope).ConfigureAwait(false);RequireList(body.RootElement,100);
            var next=Text(body.RootElement,"next_cursor");if(next.Length>0&&(!next.StartsWith("cur_",StringComparison.Ordinal)||next.Length>512))throw new OnlineException(OnlineFailure.InvalidResponse);
            var works=Rows(body.RootElement,"items").Select(r=>Text(r,"object")=="work"?r:Field(r,"work")).Where(r=>KunGalGalgameId(r) is not null).Select(r=>new RemoteWorkDto(r).Map(includeExplicit,Now)).DistinctBy(w=>w.Id).ToImmutableArray();
            // Appearance and credit endpoints embed compact work summaries without covers.
            // Resolve display artwork in one bounded batch after the global Galgame filter;
            // never fetch each full detail or infer another work's cover from a similar name.
            var missing=works.Where(w=>w.CoverUrl is null).Select(w=>w.Id).ToImmutableArray();
            if(!missing.IsEmpty)
            {
                var covers=await ReadCoversAsync(missing,scope).ConfigureAwait(false);
                works=[..works.Select(w=>w.CoverUrl is null&&covers.TryGetValue(w.Id,out var url)?w with{CoverUrl=url}:w)];
            }
            scope.Checkpoint(OnlineAcceptanceStage.Mapped);return new CatalogEntityWorks(works,next.Length==0?null:next);
        },cancellation);
    }
    public Task<CatalogEntityPage> TraitCharactersAsync(string id, string? cursor, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!ValidId(id) || cursor is not null && (!cursor.StartsWith("cur_", StringComparison.Ordinal) || cursor.Length > 512))
            throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation(async scope =>
        {
            var query = Parameters();
            query["trait_id"] = id; query["include"] = "image,figure,work_count"; query["limit"] = "20";
            if (cursor is not null) query["cursor"] = cursor;
            using var body = await transport.GetAsync("catalog/characters", query, scope).ConfigureAwait(false);
            RequireList(body.RootElement, 20);
            var next = Text(body.RootElement, "next_cursor");
            if (next.Length > 0 && (!next.StartsWith("cur_", StringComparison.Ordinal) || next.Length > 512))
                throw new OnlineException(OnlineFailure.InvalidResponse);
            var characters = new List<CatalogEntityRef>();
            foreach (var row in Rows(body.RootElement, "items"))
            {
                if (Text(row, "object") != "character" || !ValidId(Text(row, "id")))
                    throw new OnlineException(OnlineFailure.InvalidResponse);
                var image = Field(row, "image");
                var imageUrl = !includeExplicit && Text(image, "sexual") == "explicit" ? null : Image(image);
                characters.Add(new CatalogEntityRef(CatalogEntityKind.Character, Text(row, "id"), Localized(row), ImageUrl: imageUrl) { Names = CatalogDisplayDto.Names(row) });
            }
            scope.Checkpoint(OnlineAcceptanceStage.Mapped);
            return new CatalogEntityPage([..characters.DistinctBy(c => c.Id)], next.Length == 0 ? null : next);
        }, cancellation);
    }

}
