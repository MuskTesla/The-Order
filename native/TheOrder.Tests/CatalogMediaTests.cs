using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
namespace TheOrder.Tests;

public sealed class CatalogMediaTests
{
    private static WorkMetadata Artwork()=>Work("200") with{CoverUrl="https://images.example/main",Covers=[new("1","https://images.example/alternative",null,null,0,null)],
        Presentation=new("kungal",[],[],[],[],[],[new(new(CatalogEntityKind.Character,"7","角色",ImageUrl:"https://images.example/character"),"","main","none",[])],[],[new CatalogEntityRef(CatalogEntityKind.Work,"201","关联作品"){KunGalGalgameId="201"}]){LanguageSchema=CatalogLanguage.DisplaySchema,RelatedSchema=CatalogRelatedWorks.Schema}};
    private sealed class Catalog:IOnlineCatalog,ICatalogCoverSource
    {
        public WorkMetadata Value=Artwork();public int Details,Batches;public bool Missing;public Func<string,CancellationToken,Task>? BeforeDetails;
        public async Task<WorkMetadata> DetailsAsync(string id,bool refresh,CancellationToken token){Details++;if(BeforeDetails is not null)await BeforeDetails(id,token);return Value with{Id=id};}
        public Task<ImmutableDictionary<string,string?>> CoversAsync(ImmutableArray<string> ids,CancellationToken token)
        {Batches++;Assert.Equal("201",Assert.Single(ids));return Task.FromResult(Missing?ImmutableDictionary<string,string?>.Empty:ImmutableDictionary<string,string?>.Empty.Add("201","https://images.example/related"));}
        public Task<OnlinePage> SearchAsync(string q,string? cursor,CancellationToken token)=>throw new NotSupportedException();
        public Task TestConnectionAsync(CancellationToken token)=>Task.CompletedTask;public void ClearCache(){}
    }
    private sealed class Store:ICatalogPresentationCache
    {
        public CatalogPresentationEntry? Entry;public bool Refuse;
        public Task<CatalogPresentationEntry?> ReadAsync(string id,CancellationToken token)=>Task.FromResult(Entry?.Work.Id==id?Entry:null);
        public Task WriteAsync(CatalogPresentationEntry entry,CancellationToken token){token.ThrowIfCancellationRequested();if(Refuse)throw new IOException("synthetic disk failure");Entry=entry;return Task.CompletedTask;}
    }
    private sealed class Source:IRemoteCoverRegistry,IRemoteArtworkPreparation
    {
        public HashSet<string> Prepared=[];public bool Fail;
        public string? Register(string? url,string revision)=>ImageSourceKeys.Remote(url,revision);
        public Task<bool> EnsureCachedAsync(string key,CancellationToken token){Prepared.Add(key);return Task.FromResult(!Fail);}
    }
    private sealed class Images:IImagePipeline
    {
        public HashSet<string> Keys=[],Missing=[];public Func<CancellationToken,Task>? Hold;
        public async Task<ImageResult> LoadAsync(string? key,int edge,CancellationToken token){if(Hold is not null)await Hold(token);token.ThrowIfCancellationRequested();Keys.Add(key!);return Missing.Contains(key!)?new(null,"unavailable"):new(new(4,4,new byte[64]),"loaded");}
        public ImageMetrics Metrics=>new(0,0,0,0,0,0,0,0,0,0);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Detector:IArtworkTextDetector
    {
        public Task<ArtworkTextDetection> AnalyzeAsync(RasterPixels p,CancellationToken token)=>throw new InvalidOperationException("There is no CG to screen");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static CatalogPresentationService Service(Catalog c,Images i,Store s,Source r)=>new(c,i,r,s,()=>new Detector(),new OnlineAuthContext());
    [Theory][InlineData(true)][InlineData(false)]
    public async Task LegacyCoverUpgradePreservesPreparedArtworkAndRecoversOnlyPrimary(bool supplement)
    {
        var c=new Catalog();
        var front=new WorkCover("vndb:cv1","https://t.vndb.org/cv/01/1.jpg",411,560,null,"safe");
        var old=Artwork() with{CoverUrl="https://images.example/disc",Covers=supplement?[front]:[]};
        var current=CachedCatalogWork.From(old);
        var s=new Store{Entry=new(CatalogPresentationService.Algorithm,current,null,CatalogPresentationService.Signature(current,null),null,true){ArtworkSchema=4,BannerSchema=1,MediaSchema=4,MediaPrepared=true}};
        await using(var service=Service(c,new Images(),s,new Source()))
        {
            Assert.True(await service.PrepareAsync("200"));
            Assert.Equal(supplement?front.Url:c.Value.CoverUrl,s.Entry!.Work.CoverUrl);
            Assert.Equal(6,s.Entry.MediaSchema);Assert.Equal(2,s.Entry.CoverSchema);
            Assert.True(s.Entry.Prepared);Assert.Equal(4,s.Entry.ArtworkSchema);
            Assert.Equal(0,service.Metrics.Analyses);Assert.Equal(1,c.Details);
        }
        await using(var reopened=Service(c,new Images(),s,new Source()))
        {Assert.True(await reopened.PrepareAsync("200"));Assert.Equal(1,c.Details);}
    }
    [Fact] public async Task LegacyUnclassifiedCoverRecoveryFailureRemainsRetryableWithoutChangingVersion()
    {
        var c=new Catalog{BeforeDetails=(_,_)=>throw new OnlineException(OnlineFailure.InvalidResponse)};
        var current=CachedCatalogWork.From(Artwork() with{CoverUrl="https://images.example/back",Covers=[]});
        var s=new Store{Entry=new(CatalogPresentationService.Algorithm,current,null,CatalogPresentationService.Signature(current,null),null,true){ArtworkSchema=4,MediaSchema=4,MediaPrepared=true}};
        await using var service=Service(c,new Images(),s,new Source());
        Assert.False(await service.PrepareAsync("200"));Assert.Null(s.Entry!.Work.CoverUrl);
        Assert.Equal("200",s.Entry.Work.Id);Assert.Equal(0,s.Entry.CoverSchema);Assert.True(s.Entry.Prepared);
        c.BeforeDetails=null;Assert.True(await service.PrepareAsync("200"));Assert.Equal(c.Value.CoverUrl,s.Entry!.Work.CoverUrl);
    }
    [Fact] public async Task CompositionDecisionSurvivesRestartWithoutLoadingUnchosenCandidatesAgain()
    {
        var c=new Catalog();var front=new WorkCover("vndb:cv1","https://t.vndb.org/cv/01/1.jpg",411,560,null,"safe");
        c.Value=c.Value with{Covers=[front]};var s=new Store();
        await using(var service=Service(c,new Images(),s,new Source()))
        {
            Assert.True(await service.PrepareAsync("200"));
            Assert.Equal(front.Url,s.Entry!.Work.CoverUrl);Assert.Equal(2,s.Entry.CoverDecision!.Assessments.Length);
            Assert.Equal(c.Value.CoverUrl,s.Entry.CatalogCover!.Url);
            var recommendation=await service.RecommendationAsync("200",default);
            Assert.True(recommendation.Ready);Assert.Equal(front.Url,recommendation.Url);
        }
        var decision=s.Entry!.CoverDecision;var images=new Images();
        await using(var service=Service(c,images,s,new Source()))
        {
            Assert.True(await service.PrepareAsync("200"));Assert.Same(decision,s.Entry.CoverDecision);
            Assert.DoesNotContain(ImageSourceKeys.Remote(c.Value.CoverUrl,c.Value.FetchedAt)!,images.Keys);
            Assert.Equal(1,c.Details);
        }
    }
    [Fact] public async Task FailedCompositionCandidatePreservesEligibleCoverAndRetriesWithoutRefetchingMetadata()
    {
        var c=new Catalog();var front=new WorkCover("vndb:cv1","https://t.vndb.org/cv/01/1.jpg",411,560,null,"safe");
        c.Value=c.Value with{Covers=[front]};var images=new Images();
        images.Missing.Add(ImageSourceKeys.Remote(front.Url,c.Value.FetchedAt)!);
        var s=new Store();await using var service=Service(c,images,s,new Source());
        Assert.False(await service.PrepareAsync("200"));Assert.Equal(c.Value.CoverUrl,s.Entry!.Work.CoverUrl);
        Assert.False(s.Entry.CoverDecision!.Complete);Assert.Equal(0,s.Entry.CoverSchema);
        images.Missing.Clear();Assert.True(await service.PrepareAsync("200"));
        Assert.Equal(front.Url,s.Entry.Work.CoverUrl);Assert.Equal(2,s.Entry.CoverSchema);Assert.Equal(1,c.Details);
    }
    [Fact] public async Task ForegroundPriorityMovesPendingWorkWithoutDuplicatingActivePreparation()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var order=new List<string>();
        var c=new Catalog{BeforeDetails=async(id,t)=>{order.Add(id);if(id=="200"){entered.TrySetResult();await release.Task.WaitAsync(t);}}};
        await using var service=Service(c,new Images(),new Store(),new Source());service.Queue(["200","201","202"]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));service.Queue(["202","200"],priority:true);release.TrySetResult();
        await service.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(new[]{"200","202","201"},order);Assert.Equal(0,service.Metrics.Queued);
    }
    [Fact] public async Task ImportPreparesCoversCharactersAndDirectRelatedCardsWithoutRecursiveCatalogFetch()
    {
        var c=new Catalog();var images=new Images();var source=new Source();var store=new Store();await using var service=Service(c,images,store,source);
        Assert.True(await service.PrepareAsync("200"));var entry=await service.GetAsync("200");
        Assert.True(entry.MediaPrepared);Assert.Equal(3,source.Prepared.Count);Assert.Equal(source.Prepared,images.Keys);
        Assert.DoesNotContain(ImageSourceKeys.Remote("https://images.example/alternative",entry.Work.FetchedAt)!,source.Prepared);
        Assert.Equal("https://images.example/related",Assert.Single(entry.Work.Presentation!.RelatedWorks).ImageUrl);
        Assert.Equal(1,c.Details);Assert.Equal(1,c.Batches);Assert.Equal("200",entry.Work.Id);
        await service.PrepareAsync("200");Assert.Equal(1,c.Batches);Assert.Equal(1,c.Details);
    }
    [Fact] public async Task MissingBatchRowIsRetryableAndNeverLabeledReady()
    {
        var c=new Catalog{Missing=true};var images=new Images();var source=new Source();var store=new Store();await using var service=Service(c,images,store,source);
        Assert.False(await service.PrepareAsync("200"));Assert.False(store.Entry!.MediaPrepared);c.Missing=false;
        Assert.True(await service.PrepareAsync("200"));Assert.Equal(2,c.Batches);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task EitherArtworkOrMetadataPersistenceFailureCannotClaimReady(bool artwork)
    {
        var c=new Catalog();var store=new Store{Refuse=!artwork};var source=new Source{Fail=artwork};await using var service=Service(c,new Images(),store,source);
        Assert.False(await service.PrepareAsync("200"));Assert.False((await service.GetAsync("200")).MediaPrepared);
    }
    [Fact] public async Task CancellationWhileWarmingDoesNotPublishReadyOrLateRelatedImages()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var images=new Images{Hold=async t=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);}};
        var store=new Store();await using var service=Service(new Catalog(),images,store,new Source());using var cancel=new CancellationTokenSource();
        var prepare=service.PrepareAsync("200",cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>prepare);Assert.False(store.Entry!.MediaPrepared);Assert.Null(Assert.Single(store.Entry.Work.Presentation!.RelatedWorks).ImageUrl);
    }
    [Fact] public async Task LatePreparationCannotReplaceRefreshedMetadata()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c=new Catalog();var store=new Store();var images=new Images{Hold=async t=>{entered.TrySetResult();await release.Task.WaitAsync(t);}};await using var service=Service(c,images,store,new Source());
        var prepare=service.PrepareAsync("200");await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));c.Value=Artwork() with{Title="新资料"};await service.GetAsync("200",true);release.TrySetResult();
        Assert.False(await prepare);Assert.Equal("新资料",(await service.GetAsync("200")).Work.Title);Assert.False(store.Entry!.MediaPrepared);
    }
    [Fact] public async Task CoverBatchUsesBoundedAuthenticatedRequestAndRejectsForeignRows()
    {
        using var http=new HttpClient(new OnlineTransportTests.Handler((request,_)=>{
            var query=request.RequestUri!.Query;Assert.Contains("include=covers",query);Assert.Contains("limit=100",query);Assert.NotNull(request.Headers.Authorization);
            return Task.FromResult(OnlineTransportTests.Json("""{"object":"list","items":[{"object":"work","id":"201","medium":"galgame","claim":{"site":"kungal","state":"live","site_work_id":"201"},"cover":{"url":"https://images.example/good"}},{"object":"work","id":"999","medium":"galgame","claim":{"site":"kungal","state":"live","site_work_id":"999"},"cover":{"url":"https://images.example/foreign"}},{"object":"work","id":"202","medium":"anime","claim":{"site":"kungal","state":"live","site_work_id":"202"},"cover":{"url":"https://images.example/anime"}}]}"""));
        }));
        var c=new NextMoeCatalog(new CatalogTransport(http,new OnlineTransportTests.Credential(),new("https://api.nextmoe.dev/v2/")));
        var covers=await c.CoversAsync(["201","202"],default);Assert.Equal("https://images.example/good",covers["201"]);Assert.False(covers.ContainsKey("999"));Assert.False(covers.ContainsKey("202"));
    }
}
