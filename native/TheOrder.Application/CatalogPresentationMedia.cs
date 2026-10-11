using System.Collections.Immutable;
using TheOrder.Domain;
namespace TheOrder.Application;
public sealed partial class CatalogPresentationService
{
    private const int MediaSchema=6;
    private readonly HashSet<string> mediaReady=[];
    private readonly Dictionary<string,string?> relatedCovers=[];
    private async Task<CatalogPresentationEntry> PrepareMediaAsync(CatalogPresentationEntry entry,CancellationToken token)
    {
        var id=entry.Work.Id;
        lock(gate)if(entry.MediaPrepared&&entry.MediaSchema==MediaSchema&&CoverReady(entry)&&mediaReady.Contains(entry.Signature))return entry;
        var identity=await auth.CaptureAsync(token).ConfigureAwait(false);
        using var request=CancellationTokenSource.CreateLinkedTokenSource(token,identity.Changed);
        var cancellation=request.Token;
        var coverEntry=await PrepareCoverAsync(entry,cancellation).ConfigureAwait(false);
        var work=coverEntry.Work;bool complete=coverEntry.CoverSchema==CoverSchemaVersion;
        if(entry.MediaSchema!=MediaSchema && work.Presentation is {RelatedWorks.IsEmpty:false} presentation)
        {
            var missing=CatalogRelatedWorks.Visible(presentation).Where(w=>w.ImageUrl is null&&ValidId(w.Id)).Select(w=>w.Id).Distinct().ToArray();
            var resolved=new Dictionary<string,string?>();
            foreach(var relatedId in missing)
            {
                lock(gate)if(relatedCovers.TryGetValue(relatedId,out var url)){resolved[relatedId]=url;continue;}
                var known=Cached(relatedId);
                if(known is null)try{known=await disk.ReadAsync(relatedId,cancellation).ConfigureAwait(false);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or DataIsolationException){}
                if(known is not null&&Valid(known,relatedId))resolved[relatedId]=known.Work.CoverUrl;
            }
            var needed=missing.Where(i=>!resolved.ContainsKey(i)).ToImmutableArray();
            try
            {
                if(!needed.IsEmpty)
                {
                    if(catalog is ICatalogCoverSource covers)
                    {
                        var batch=await covers.CoversAsync(needed,cancellation).ConfigureAwait(false);lock(gate)catalogLoads++;
                        foreach(var relatedId in needed)
                            if(batch.TryGetValue(relatedId,out var url))resolved[relatedId]=url;else complete=false;
                    }
                    else foreach(var relatedId in needed)
                    {
                        var detail=await catalog.DetailsAsync(relatedId,false,cancellation).ConfigureAwait(false);lock(gate)catalogLoads++;
                        if(detail.Id==relatedId)resolved[relatedId]=detail.CoverUrl;else complete=false;
                    }
                }
            }
            catch(OnlineException){complete=false;}
            lock(gate)foreach(var value in resolved){if(relatedCovers.Count>=2048&&!relatedCovers.ContainsKey(value.Key))relatedCovers.Remove(relatedCovers.Keys.First());relatedCovers[value.Key]=value.Value;}
            var rows=presentation.RelatedWorks.Select(r=>r.ImageUrl is null&&resolved.TryGetValue(r.Id,out var url)?r with{ImageUrl=url}:r).ToImmutableArray();
            work=work with{Presentation=presentation with{RelatedWorks=rows}};
        }
        var current=CachedCatalogWork.From(work);var updated=coverEntry with{Current=current,Signature=Signature(current,entry.Reference),MediaSchema=complete?MediaSchema:0,MediaPrepared=false};
        // Cache the images that these game pages actually display, not every unselected CG.
        var urls=new List<(string? Url,int Edge)> {(work.CoverUrl,512)};
        // Unchosen cover variants are fetched when the picker opens, not during every import.
        foreach(var character in (work.Presentation?.Characters??[]).Take(100))urls.Add((character.Entity.ImageUrl,256));
        if(work.Presentation is null)foreach(var character in work.Characters.Take(100))urls.Add((character.ImageUrl,256));
        foreach(var related in CatalogRelatedWorks.Visible(work.Presentation))urls.Add((related.ImageUrl,320));
        if(entry.Banner is {} banner)urls.Add((banner.Url,ArtworkSizing.MaximumEdge));
        foreach(var (url,edge) in urls.Where(u=>u.Url is {Length:>0}).DistinctBy(u=>u.Url))
        {
            cancellation.ThrowIfCancellationRequested();var key=registry.Register(url,work.FetchedAt);
            if(key is null){complete=false;continue;}
            var result=await images.LoadAsync(key,edge,cancellation).ConfigureAwait(false);
            if(result.Pixels is null||registry is IRemoteArtworkPreparation source&&!await source.EnsureCachedAsync(key,cancellation).ConfigureAwait(false))complete=false;
        }
        updated=updated with{MediaPrepared=complete};
        await operations.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            bool accepted=auth.Accept(identity,()=>{
                cancellation.ThrowIfCancellationRequested();lock(gate)
                {
                    if(memory.TryGetValue(id,out var active)&&active.Entry.Signature!=entry.Signature)return false;
                    Remember(updated);if(complete){if(mediaReady.Count>=MemoryItems)mediaReady.Remove(mediaReady.First());mediaReady.Add(updated.Signature);}return true;
                }
            });
            if(!accepted)return entry with{MediaPrepared=false};
            if(!await SaveAsync(updated,cancellation).ConfigureAwait(false))
            {updated=updated with{MediaPrepared=false};lock(gate){mediaReady.Remove(updated.Signature);Remember(updated);}}
        }
        finally{operations.Release();}
        Prepared?.Invoke(id);return updated;
    }
}
