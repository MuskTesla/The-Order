using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

internal sealed class AniListMemoryCache : IAniListCache, IMediaIdentityDecisionOwner
{
    private IMediaIdentityDecisions? choices;
    public IMediaIdentityDecisions IdentityDecisions => choices ??= new TestMediaIdentityDecisions(() => Refuse);
    public readonly Dictionary<string,AniListCacheValue> Values=new();public bool Refuse;
    public Task<AniListCacheValue?> ReadAsync(string key,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.FromResult(Values.GetValueOrDefault(key));}
    public Task WriteAsync(AniListCacheValue value,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();if(Refuse)throw new IOException("缓存不可写");Values[value.Key]=value;return Task.CompletedTask;}
}
internal sealed class TestMediaIdentityDecisions(Func<bool>? refuse = null) : IMediaIdentityDecisions
{
    private readonly SessionMediaIdentityDecisions inner = new();
    public Task<MediaIdentityDecisionDocument> ReadAsync(MediaIdentityContext source, CancellationToken cancellation) => inner.ReadAsync(source, cancellation);
    public Task<MediaIdentityDecisionDocument> ApplyAsync(MediaIdentityDecision decision, long expectedRevision, CancellationToken cancellation)
    { if (refuse?.Invoke() == true) throw new IOException("合成用户关联存储不可写"); return inner.ApplyAsync(decision, expectedRevision, cancellation); }
}
internal sealed class AniListFakeProvider : IAniListProvider
{
    public int Searches,Fetches,Resolves; public Func<int,int,CancellationToken,Task<AniListDetails>>? Fetch;
    public AniListMedia[] Results=[AniListTestData.Media()];
    public Task<AniListMedia[]> SearchAsync(AniListSource source,string? query,CancellationToken cancellation){Searches++;return Task.FromResult(Results);}
    // This fixture is an explicitly complete synthetic candidate universe.
    public async Task<AniListSearchCandidates> SearchCandidatesAsync(AniListSource source,string? query,CancellationToken cancellation)
        => new(await SearchAsync(source,query,cancellation),true);
    public Task<AniListMedia> ResolveAsync(AniListReference reference,CancellationToken cancellation){Resolves++;return Task.FromResult(Results[0]);}
    public Task<AniListDetails> FetchAsync(int id,AniListMediaKind kind,int page,CancellationToken cancellation){Fetches++;return Fetch?.Invoke(id,page,cancellation)??Task.FromResult(AniListTestData.Details(id,page));}
}
public sealed class AniListServiceTests
{
    [Fact] public async Task EpisodeAndDateDifferencesRemainUnconfirmedDespiteMatchingTitleAndType()
    {
        var provider=new AniListFakeProvider();var service=new AniListService(provider);var match=await service.MatchAsync(AniListTestData.Source(),null,false,default);
        Assert.Single(match.Candidates);Assert.Null(match.Binding);Assert.Equal(1,provider.Searches);Assert.Equal(0,provider.Fetches);
    }
    [Fact] public async Task OnlyExplicitCompatibleExternalIdCreatesAutomaticProof()
    {
        var provider=new AniListFakeProvider();var service=new AniListService(provider);
        var source=AniListTestData.Source()with{ExternalLinks=["https://myanimelist.net/anime/2167"]};var match=await service.MatchAsync(source,null,false,default);
        Assert.NotNull(match.Binding);Assert.Equal(2167,match.Binding.MediaId);Assert.Equal(1,provider.Resolves);Assert.Equal(0,provider.Searches);
    }
    [Fact] public async Task IncorrectExternalIdNeverCreatesBinding()
    {
        var service=new AniListService(new AniListFakeProvider());var match=await service.MatchAsync(AniListTestData.Source()with{ExternalLinks=["https://anilist.co/anime/4181"]},null,false,default);Assert.Null(match.Binding);
    }
    [Fact] public async Task ConfirmedChoiceSurvivesNewServiceButChangedBangumiEvidenceDoesNotReuseIt()
    {
        var disk=new AniListMemoryCache();var provider=new AniListFakeProvider();var source=AniListTestData.Source();
        await new AniListService(provider,disk).ConfirmAsync(source,AniListTestData.Details(),default);
        var reopened=new AniListService(provider,disk);Assert.NotNull((await reopened.MatchAsync(source,null,false,default)).Binding);
        // Changed ordinary facts do not erase the user's stable-ID decision;
        // final media validation in the view model still blocks identity conflicts.
        Assert.NotNull((await reopened.MatchAsync(source with{Date="2008-10-03"},null,false,default)).Binding);Assert.Equal(0,provider.Searches);
    }
    [Fact] public async Task FailedPersistenceCannotClaimConfirmedChoice()
    {
        var disk=new AniListMemoryCache{Refuse=true};var service=new AniListService(new AniListFakeProvider(),disk);
        await Assert.ThrowsAsync<IOException>(()=>service.ConfirmAsync(AniListTestData.Source(),AniListTestData.Details(),default));Assert.Null((await service.MatchAsync(AniListTestData.Source(),null,false,default)).Binding);
    }
    [Fact] public async Task WrongFormatCannotBeManuallyConfirmed()
    {
        var service=new AniListService(new AniListFakeProvider());var details=AniListTestData.Details();details=details with{Media=details.Media with{Format="MOVIE"}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ConfirmAsync(AniListTestData.Source(),details,default));
    }
    [Fact] public async Task CacheHitsAndStaleOfflineFallbackStayBounded()
    {
        var time=AniListTestData.Stamp;var provider=new AniListFakeProvider();var disk=new AniListMemoryCache();var service=new AniListService(provider,disk,()=>time);
        Assert.False((await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default)).FromCache);
        Assert.True((await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default)).FromCache);Assert.Equal(1,provider.Fetches);
        time+=TimeSpan.FromDays(2);provider.Fetch=(_,_,_)=>throw new AniListException(AniListFailure.Offline,"离线");
        var stale=await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default);Assert.True(stale.IsStale);Assert.True(stale.FromCache);
        time+=TimeSpan.FromDays(20);await Assert.ThrowsAsync<AniListException>(()=>service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default));
    }
    [Fact] public async Task RemovedOrMalformedMetadataCannotResurrectFromDiskAfterRestart()
    {
        var provider=new AniListFakeProvider();var disk=new AniListMemoryCache();var service=new AniListService(provider,disk);
        await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default);provider.Fetch=(_,_,_)=>throw new AniListException(AniListFailure.Unavailable,"已移除");
        await Assert.ThrowsAsync<AniListException>(()=>service.LoadAsync(2167,AniListMediaKind.Anime,true,1,default));
        await Assert.ThrowsAsync<AniListException>(()=>new AniListService(provider,disk).LoadAsync(2167,AniListMediaKind.Anime,false,1,default));
    }
    [Fact] public async Task LateOldResponseCannotReplaceNewCacheEntry()
    {
        var old=new TaskCompletionSource<AniListDetails>();int calls=0;var provider=new AniListFakeProvider{Fetch=(_,_,_)=>++calls==1?old.Task:Task.FromResult(AniListTestData.Details() with{Media=AniListTestData.Media()with{Title="New"}})};
        var service=new AniListService(provider);var first=service.LoadAsync(2167,AniListMediaKind.Anime,true,1,default);
        await service.LoadAsync(2167,AniListMediaKind.Anime,true,1,default);old.SetResult(AniListTestData.Details());await first;
        Assert.Equal("New",(await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default)).Details.Media.Title);
    }
    [Fact] public async Task PeoplePaginationIsOnlyFetchedOnExplicitDemand()
    {
        var provider=new AniListFakeProvider();var service=new AniListService(provider);await service.LoadAsync(2167,AniListMediaKind.Anime,false,1,default);Assert.Equal(1,provider.Fetches);
        Assert.Equal(2,(await service.LoadAsync(2167,AniListMediaKind.Anime,false,2,default)).Details.PeoplePage);Assert.Equal(2,provider.Fetches);
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(2167,AniListMediaKind.Anime,false,11,default));
    }
    [Fact] public async Task ProfileCachePersistsChoiceAndCannotBeAdoptedByAnotherProfile()
    {
        var root=Path.Combine(Path.GetTempPath(),"the-order-anilist-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string pathA=Path.Combine(root,"A"),pathB=Path.Combine(root,"B");
            using var a=NativeProfileStore.Open(new(NativeProfileKind.Test,pathA),new NativeLibraryCodec());using var b=NativeProfileStore.Open(new(NativeProfileKind.Test,pathB),new NativeLibraryCodec());
            var storeA=new IsolatedAniListCache(a.Configuration());var source=AniListTestData.Source();var entry=new AniListCacheValue("binding:"+source.Fingerprint,AniListTestData.Stamp,Binding:new(source.Fingerprint,2167,AniListMediaKind.Anime,"用户确认"));
            var manifest=File.ReadAllBytes(Path.Combine(pathA,"native-profile.json"));await storeA.WriteAsync(entry,default);Assert.Equal(2167,(await new IsolatedAniListCache(a.Configuration()).ReadAsync(entry.Key,default))!.Binding!.MediaId);
            var dirB=Path.Combine(pathB,IsolatedAniListCache.DirectoryName);Directory.CreateDirectory(dirB);var file=Directory.GetFiles(Path.Combine(pathA,IsolatedAniListCache.DirectoryName)).Single();File.Copy(file,Path.Combine(dirB,Path.GetFileName(file)));
            Assert.Null(await new IsolatedAniListCache(b.Configuration()).ReadAsync(entry.Key,default));Assert.Equal(manifest,File.ReadAllBytes(Path.Combine(pathA,"native-profile.json")));
        }
        finally{if(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(root).StartsWith("the-order-anilist-test-",StringComparison.Ordinal))Directory.Delete(root,true);}
    }
}
internal sealed class AniListBridgeReader : IBangumiSubjectDetailsReader
{
    public Func<string,CancellationToken,Task<BangumiSubjectDetailsResult>>? Load;
    public Task<BangumiSubjectDetailsResult> LoadAsync(string id,BangumiRelationKind kind,bool refresh,CancellationToken cancellation)=>Load?.Invoke(id,cancellation)??Task.FromResult(new BangumiSubjectDetailsResult(new(id,kind,"CLANNAD","CLANNAD -クラナド-",null,"Bangumi 简介","2007-10-04","TV",22,null,null,AniListTestData.Stamp),false,false,null));
}
public sealed class AniListDetailsViewModelTests
{
    private static BangumiRelationItem Item(string id="51")=>new(id,BangumiRelationKind.Anime,"CLANNAD","CLANNAD -クラナド-","动画",null);
    [Theory]
    [InlineData(BangumiRelationsFailure.Unavailable)]
    [InlineData(BangumiRelationsFailure.RateLimited)]
    [InlineData(BangumiRelationsFailure.Timeout)]
    [InlineData(BangumiRelationsFailure.Offline)]
    public async Task InitialBridgeFailureDoesNotClaimReadableFallback(BangumiRelationsFailure failure)
    {
        var bridge=new AniListBridgeReader{Load=(_,_)=>throw new BangumiRelationsException(failure,"Bangumi 基础资料读取失败，请重试。")};
        var provider=new AniListFakeProvider();using var vm=new AniListDetailsViewModel(bridge,new AniListService(provider));
        await vm.OpenAsync(Item());
        Assert.Equal(AniListDetailState.Failed,vm.State);Assert.Null(vm.Bridge);Assert.Null(vm.Source);
        Assert.Equal("Bangumi 基础资料读取失败，请重试。",vm.Message);Assert.DoesNotContain("仍可浏览",vm.Message);
        Assert.Equal(0,provider.Searches);Assert.True(vm.CanLoad);
    }
    [Fact] public async Task UnexpectedInitialFailureIsRedactedAndRetryRestoresOnlyCurrentIdentity()
    {
        var bridge=new AniListBridgeReader{Load=(_,_)=>throw new HttpRequestException("internal transport diagnostic")};
        using var vm=new AniListDetailsViewModel(bridge,new AniListService(new AniListFakeProvider()));
        await vm.OpenAsync(Item());Assert.Equal(AniListDetailState.Failed,vm.State);
        Assert.Contains("基础资料",vm.Message);Assert.DoesNotContain("internal",vm.Message);Assert.DoesNotContain("仍可浏览",vm.Message);
        bridge.Load=null;await vm.RefreshAsync();
        Assert.Equal("51",vm.Bridge!.Details.Id);Assert.Equal(AniListDetailState.Choosing,vm.State);
        Assert.Null(vm.Displayed);Assert.False(vm.IsBusy);Assert.False(vm.HasTrustedBridge);
    }
    [Fact] public async Task UnexpectedProfessionalFailureKeepsRealBridgeAndAccurateFallbackMessage()
    {
        var provider=new AniListFakeProvider{Fetch=(_,_,_)=>throw new HttpRequestException("internal transport diagnostic")};
        using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));
        await vm.OpenAsync(Item());await vm.PreviewAsync(2167);
        Assert.Equal(AniListDetailState.Failed,vm.State);Assert.NotNull(vm.Bridge);Assert.Null(vm.Displayed);
        Assert.Contains("Bangumi 资料仍可浏览",vm.Message);Assert.DoesNotContain("internal",vm.Message);Assert.True(vm.CanLoad);
    }
    [Fact] public async Task ExactTitleNeedsPreviewAndConfirmationBeforeSavedBinding()
    {
        var disk=new AniListMemoryCache();using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(new AniListFakeProvider(),disk));
        await vm.OpenAsync(Item());Assert.Equal(AniListDetailState.Choosing,vm.State);Assert.False(vm.CanConfirm);
        await vm.PreviewAsync(2167);Assert.Equal(AniListDetailState.Preview,vm.State);Assert.True(vm.CanConfirm);Assert.DoesNotContain(disk.Values,x=>x.Key.StartsWith("binding:"));
        await vm.ConfirmAsync();Assert.Equal(AniListDetailState.Ready,vm.State);
        Assert.NotNull((await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(vm.Source!),default)).Confirmed);
        Assert.DoesNotContain(disk.Values,x=>x.Key.StartsWith("binding:"));
    }
    [Fact] public async Task WrongCandidateTypeBlocksConfirmation()
    {
        var provider=new AniListFakeProvider{Fetch=(_,_,_)=>Task.FromResult(AniListTestData.Details()with{Media=AniListTestData.Media()with{Format="MOVIE"}})};
        using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));await vm.OpenAsync(Item());await vm.PreviewAsync(2167);Assert.False(vm.CanConfirm);await vm.ConfirmAsync();Assert.Equal(AniListDetailState.Preview,vm.State);
    }
    [Fact] public async Task FastPageSwitchDiscardsLateBridgeAndPreservesNewIdentity()
    {
        var first=new TaskCompletionSource<BangumiSubjectDetailsResult>();var bridge=new AniListBridgeReader();bridge.Load=(id,_)=>id=="51"?first.Task:Task.FromResult(new BangumiSubjectDetailsResult(new(id,BangumiRelationKind.Anime,"New","New",null,"new",null,null,null,null,null,AniListTestData.Stamp),false,false,null));
        using var vm=new AniListDetailsViewModel(bridge,new AniListService(new AniListFakeProvider()));var old=vm.OpenAsync(Item());await vm.OpenAsync(Item("876"));
        first.SetResult(new(new("51",BangumiRelationKind.Anime,"Old","Old",null,"old",null,null,null,null,null,AniListTestData.Stamp),false,false,null));await old;
        Assert.Equal("876",vm.Bridge!.Details.Id);Assert.Equal(AniListDetailState.Choosing,vm.State);
    }
    [Fact] public async Task DeactivatedPreviewCannotShowLateAniListResponse()
    {
        var late=new TaskCompletionSource<AniListDetails>();var provider=new AniListFakeProvider{Fetch=(_,_,_)=>late.Task};using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));
        await vm.OpenAsync(Item());var pending=vm.PreviewAsync(2167);vm.Deactivate();late.SetResult(AniListTestData.Details());await pending;Assert.Null(vm.Displayed);
    }
    [Fact] public async Task SequelNavigationReturnsCachedRootWithScrollAndDoesNotAlterBridge()
    {
        var sequel=AniListTestData.Media()with{Id=4181,Title="After Story",StartDate="2008-10-03"};var provider=new AniListFakeProvider{Fetch=(id,_,_)=>Task.FromResult(AniListTestData.Details(id)with{Relations=id==2167?[new(sequel,"SEQUEL")]:[]})};
        using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));await vm.OpenAsync(Item());await vm.PreviewAsync(2167);await vm.ConfirmAsync();var root=vm.Displayed;
        var firstVersion=vm.RestoreVersion;
        await vm.ExploreAsync(root!.Details.Relations[0],487);
        Assert.Equal(1,vm.HistoryDepth);Assert.Equal(4181,vm.Media!.Id);
        Assert.Equal(0,vm.RestoreOffset);Assert.True(vm.RestoreVersion>firstVersion);
        Assert.True(vm.GoBack());Assert.Same(root,vm.Displayed);Assert.Equal(487,vm.RestoreOffset);
        Assert.Equal("51",vm.Bridge!.Details.Id);Assert.False(vm.GoBack());
        var returnVersion=vm.RestoreVersion;
        await vm.ExploreAsync(root.Details.Relations[0],487);
        Assert.Equal(0,vm.RestoreOffset);Assert.True(vm.RestoreVersion>returnVersion);
        Assert.True(vm.GoBack());Assert.Equal(487,vm.RestoreOffset);
        var reopenedVersion=vm.RestoreVersion;
        await vm.OpenAsync(Item());
        Assert.Equal(0,vm.RestoreOffset);Assert.True(vm.RestoreVersion>reopenedVersion);
    }
    [Fact] public async Task NetworkFailureLeavesBangumiSummaryAndAllowsRetry()
    {
        var provider=new AniListFakeProvider{Fetch=(_,_,_)=>throw new AniListException(AniListFailure.Offline,"离线")};using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));await vm.OpenAsync(Item());await vm.PreviewAsync(2167);
        Assert.Equal("Bangumi 简介",vm.Bridge!.Details.Summary);Assert.Equal(AniListDetailState.Failed,vm.State);Assert.True(vm.CanLoad);
    }
    [Fact] public async Task CancellingRefreshCannotPromoteUnconfirmedPreviewToReady()
    {
        var provider=new AniListFakeProvider();using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));await vm.OpenAsync(Item());await vm.PreviewAsync(2167);
        var late=new TaskCompletionSource<AniListDetails>();provider.Fetch=(_,_,_)=>late.Task;var pending=vm.RefreshAsync();vm.Cancel();late.SetResult(AniListTestData.Details());await pending;
        Assert.Equal(AniListDetailState.Preview,vm.State);Assert.True(vm.CanConfirm);
    }
    [Fact] public async Task InspectingMorePeopleCannotConfirmAnUnreviewedCandidate()
    {
        var provider=new AniListFakeProvider();var cache=new AniListMemoryCache();using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider,cache));
        await vm.OpenAsync(Item());await vm.PreviewAsync(2167);await vm.MoreAsync();Assert.Equal(AniListDetailState.Preview,vm.State);Assert.True(vm.CanConfirm);Assert.DoesNotContain(cache.Values,x=>x.Key.StartsWith("binding:"));
    }
    [Fact] public async Task PreviewIdentityRemainsUnconfirmedDuringLoadingAndCancellation()
    {
        var provider=new AniListFakeProvider();using var vm=new AniListDetailsViewModel(new AniListBridgeReader(),new AniListService(provider));
        await vm.OpenAsync(Item());await vm.PreviewAsync(2167);Assert.True(vm.HasUnconfirmedPreview);
        var late=new TaskCompletionSource<AniListDetails>(TaskCreationOptions.RunContinuationsAsynchronously);provider.Fetch=(_,_,_)=>late.Task;
        var pending=vm.RefreshAsync();Assert.Equal(AniListDetailState.Loading,vm.State);Assert.True(vm.HasUnconfirmedPreview);
        vm.Cancel();late.SetResult(AniListTestData.Details());await pending;Assert.True(vm.HasUnconfirmedPreview);
        await vm.ConfirmAsync();Assert.False(vm.HasUnconfirmedPreview);
    }
}
