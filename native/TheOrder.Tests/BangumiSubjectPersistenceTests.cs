using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class BangumiSubjectPersistenceTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
    private static BangumiSubjectDetails Sample(string id = "51", BangumiRelationKind kind = BangumiRelationKind.Anime, string title = "合成作品")
        => new(id, kind, title, "Original", null, "日本語・中文 😀", null, null, null, null, null, Stamp);
    private sealed class Provider(Func<string, BangumiRelationKind, CancellationToken, Task<BangumiSubjectDetails>> fetch) : IBangumiSubjectDetailsProvider
    {
        public int Calls;
        public Task<BangumiSubjectDetails> FetchAsync(string id, BangumiRelationKind kind, CancellationToken token)
        { Calls++; return fetch(id, kind, token); }
    }
    private static Provider Success(string title = "合成作品") => new((id, kind, _) => Task.FromResult(Sample(id, kind, title)));
    private static Provider Failure(BangumiRelationsFailure failure = BangumiRelationsFailure.Offline)
        => new((_, _, _) => throw new BangumiRelationsException(failure, "合成故障"));
    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "the-order-subject-cache-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "test-profile");
        public string CacheRoot => Path.Combine(Root, IsolatedBangumiSubjectDetailsCache.DirectoryName);
        public NativeProfileStore Profile { get; private set; }
        public Fixture() => Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec());
        public IsolatedBangumiSubjectDetailsCache Store(Func<PersistenceStage, Task>? probe = null) => new(Profile.Configuration(), probe);
        public void Reopen() { Profile.Dispose(); Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec()); }
        public void Dispose()
        {
            Profile.Dispose();
            var path = Path.GetFullPath(Parent);
            if (!path.StartsWith(Path.Combine(Path.GetTempPath(), "the-order-subject-cache-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
    private static BangumiSubjectDetailsService Service(Provider provider, Fixture f, DateTimeOffset? now = null, BangumiAccessContext? access = null)
        => new(provider, () => now ?? Stamp, access, f.Store());
    [Theory]
    [InlineData(BangumiRelationKind.Anime)] [InlineData(BangumiRelationKind.Book)] [InlineData(BangumiRelationKind.Music)] [InlineData(BangumiRelationKind.Game)]
    public async Task OnlineThenOfflineProcessRestartRestoresMetadataWithoutChangingLibrary(BangumiRelationKind kind)
    {
        using var f = new Fixture(); var before = ProfileFixture.Hashes(f.Root);
        await Service(Success(), f).LoadAsync("51", kind, false, default);
        f.Reopen(); var offline = Failure();
        var result = await Service(offline, f).LoadAsync("51", kind, false, default);
        Assert.Equal(JsonSerializer.Serialize(Sample(kind: kind)), JsonSerializer.Serialize(result.Details)); Assert.True(result.FromCache); Assert.Equal(0, offline.Calls);
        Assert.Equal(before, ProfileFixture.Hashes(f.Root));
    }
    [Theory]
    [InlineData(BangumiRelationsFailure.Offline)] [InlineData(BangumiRelationsFailure.Timeout)]
    [InlineData(BangumiRelationsFailure.Server)] [InlineData(BangumiRelationsFailure.RateLimited)]
    public async Task RestartedStaleCacheSurvivesTransientFailureAndRecoveryReplacesIt(BangumiRelationsFailure failure)
    {
        using var f = new Fixture(); await Service(Success(), f).LoadAsync("51", BangumiRelationKind.Anime, false, default);
        f.Reopen();
        var stale = await Service(Failure(failure), f, Stamp.AddHours(2)).LoadAsync("51", BangumiRelationKind.Anime, false, default);
        Assert.True(stale.IsStale); Assert.NotNull(stale.Warning);
        var fresh = await Service(Success("recovered"), f, Stamp.AddHours(2)).LoadAsync("51", BangumiRelationKind.Anime, true, default);
        Assert.False(fresh.FromCache); Assert.Equal("recovered", (await f.Store().ReadAsync("anonymous", "51", BangumiRelationKind.Anime, default))!.Title);
    }
    [Theory]
    [InlineData(BangumiRelationsFailure.Unavailable)] [InlineData(BangumiRelationsFailure.InvalidResponse)]
    [InlineData(BangumiRelationsFailure.ResponseTooLarge)] [InlineData(BangumiRelationsFailure.Unauthorized)]
    public async Task DenialRemovesDiskEntryRatherThanResurrectingItAfterRestart(BangumiRelationsFailure failure)
    {
        using var f = new Fixture(); await Service(Success(), f).LoadAsync("51", BangumiRelationKind.Anime, false, default);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => Service(Failure(failure), f).LoadAsync("51", BangumiRelationKind.Anime, true, default));
        f.Reopen();
        Assert.Null(await f.Store().ReadAsync("anonymous", "51", BangumiRelationKind.Anime, default));
        await Assert.ThrowsAsync<BangumiRelationsException>(() => Service(Failure(), f).LoadAsync("51", BangumiRelationKind.Anime, false, default));
    }
    [Theory] [InlineData(-8)] [InlineData(1)]
    public async Task ExpiredOrFutureCacheDoesNotClaimAnOfflineSuccess(int dayOffset)
    {
        using var f = new Fixture(); await f.Store().WriteAsync("anonymous", Sample() with { FetchedAtUtc = Stamp.AddDays(dayOffset) }, () => true, default);
        await Assert.ThrowsAsync<BangumiRelationsException>(() => Service(Failure(), f).LoadAsync("51", BangumiRelationKind.Anime, false, default));
    }
    [Theory] [InlineData("Version")] [InlineData("ProfileId")] [InlineData("StoreId")] [InlineData("Scope")]
    [InlineData("SubjectId")] [InlineData("Kind")] [InlineData("Hash")] [InlineData("Payload")] [InlineData("FutureField")]
    public async Task ForeignFutureCorruptOrMismatchedEntryIsPreservedAndIgnored(string fault)
    {
        using var f = new Fixture(); await f.Store().WriteAsync("anonymous", Sample(), () => true, default);
        var path = Assert.Single(Directory.GetFiles(f.CacheRoot)); var node = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (fault)
        {
            case "Version": node[fault] = 2; break;
            case "ProfileId": case "StoreId": node[fault] = Guid.NewGuid().ToString(); break;
            case "Scope": node[fault] = "authorized-" + new string('A',64); break;
            case "SubjectId": node[fault] = "52"; break;
            case "Kind": node[fault] = (int)BangumiRelationKind.Book; break;
            case "Payload": node[fault] = null; break;
            default: node[fault] = "unknown"; break;
        }
        File.WriteAllText(path, node.ToJsonString()); var before = File.ReadAllBytes(path);
        Assert.Null(await f.Store().ReadAsync("anonymous", "51", BangumiRelationKind.Anime, default));
        await Assert.ThrowsAsync<IOException>(() => f.Store().WriteAsync("anonymous", Sample(), () => true, default));
        await f.Store().RemoveAsync("anonymous", "51", BangumiRelationKind.Anime, () => true, default);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task ValidChecksumCannotImportDifferentMediaIdentity()
    {
        using var f = new Fixture(); await f.Store().WriteAsync("anonymous", Sample(), () => true, default);
        var path = Assert.Single(Directory.GetFiles(f.CacheRoot)); var node = JsonNode.Parse(File.ReadAllBytes(path))!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Sample(kind: BangumiRelationKind.Book));
        node["Payload"] = Convert.ToBase64String(bytes); node["Hash"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllText(path, node.ToJsonString());
        Assert.Null(await f.Store().ReadAsync("anonymous", "51", BangumiRelationKind.Anime, default));
    }
    [Fact]
    public async Task AccessEpochMediaAndSubjectCannotShareARecordAndNoTokenIsSerialized()
    {
        using var f = new Fixture(); var first = "authorized-"+new string('A',64); var second = "authorized-"+new string('B',64);
        await f.Store().WriteAsync(first, Sample(), () => true, default);
        Assert.NotNull(await f.Store().ReadAsync(first,"51",BangumiRelationKind.Anime,default));
        foreach(var scope in new[]{second,"anonymous"})Assert.Null(await f.Store().ReadAsync(scope,"51",BangumiRelationKind.Anime,default));
        Assert.Null(await f.Store().ReadAsync(first,"52",BangumiRelationKind.Anime,default));
        Assert.Null(await f.Store().ReadAsync(first,"51",BangumiRelationKind.Book,default));
        Assert.DoesNotContain("Bearer", File.ReadAllText(Assert.Single(Directory.GetFiles(f.CacheRoot))));
    }
    [Theory]
    [InlineData(PersistenceStage.BeforeTempWrite)] [InlineData(PersistenceStage.DuringTempWrite)]
    [InlineData(PersistenceStage.AfterTempWrite)] [InlineData(PersistenceStage.BeforeReplace)]
    public async Task InterruptedWritePreservesOldCacheAndCleansTemporaryFile(PersistenceStage stage)
    {
        using var f = new Fixture(); await f.Store().WriteAsync("anonymous",Sample(),()=>true,default);
        var path=Assert.Single(Directory.GetFiles(f.CacheRoot));var before=File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(()=>f.Store(s=>s==stage?throw new IOException("injected"):Task.CompletedTask).WriteAsync("anonymous",Sample(title:"new"),()=>true,default));
        Assert.Equal(before,File.ReadAllBytes(path));Assert.Empty(Directory.GetFiles(f.CacheRoot,"*.tmp"));
    }
    [Fact]
    public async Task CancelOrSupersedeImmediatelyBeforeReplaceCannotPublishLateDiskValue()
    {
        using var f=new Fixture(); await f.Store().WriteAsync("anonymous",Sample(),()=>true,default);
        bool current=true;
        var store=f.Store(s=>{if(s==PersistenceStage.BeforeReplace)current=false;return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.WriteAsync("anonymous",Sample(title:"late"),()=>current,default));
        Assert.Equal("合成作品",(await f.Store().ReadAsync("anonymous","51",BangumiRelationKind.Anime,default))!.Title);
    }
    [Fact]
    public async Task LateProviderCompletionCannotOverwriteNewerPersistedRefresh()
    {
        using var f=new Fixture();var late=new TaskCompletionSource<BangumiSubjectDetails>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        var provider=new Provider((_,_,_)=>++calls==1?late.Task:Task.FromResult(Sample(title:"newer")));
        var service=Service(provider,f);var first=service.LoadAsync("51",BangumiRelationKind.Anime,true,default);
        await service.LoadAsync("51",BangumiRelationKind.Anime,true,default);late.SetResult(Sample(title:"older"));await first;
        f.Reopen(); Assert.Equal("newer",(await Service(Failure(),f).LoadAsync("51",BangumiRelationKind.Anime,false,default)).Details.Title);
    }
    [Fact]
    public async Task SupersedingAWriteAlreadyWaitingAtAtomicCommitKeepsTheNewerValue()
    {
        using var f=new Fixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int writes=0,calls=0;
        var store=f.Store(async stage=>{if(stage==PersistenceStage.BeforeReplace&&Interlocked.Increment(ref writes)==1){entered.SetResult();await release.Task;}});
        var provider=new Provider((id,kind,_)=>Task.FromResult(Sample(id,kind,++calls==1?"older":"newer")));
        var service=new BangumiSubjectDetailsService(provider,()=>Stamp,cache:store);
        var first=service.LoadAsync("51",BangumiRelationKind.Anime,true,default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second=service.LoadAsync("51",BangumiRelationKind.Anime,true,default);release.SetResult();await Task.WhenAll(first,second);
        Assert.Equal("newer",(await f.Store().ReadAsync("anonymous","51",BangumiRelationKind.Anime,default))!.Title);
        Assert.Empty(Directory.GetFiles(f.CacheRoot,"*.tmp"));
    }
    [Fact]
    public async Task ForeignProfileCacheCannotBeReadOverwrittenOrDeleted()
    {
        using var first=new Fixture();using var second=new Fixture();await first.Store().WriteAsync("anonymous",Sample(),()=>true,default);
        Directory.CreateDirectory(second.CacheRoot);var path=Path.Combine(second.CacheRoot,Path.GetFileName(Assert.Single(Directory.GetFiles(first.CacheRoot))));
        File.Copy(Assert.Single(Directory.GetFiles(first.CacheRoot)),path);var before=File.ReadAllBytes(path);
        Assert.Null(await second.Store().ReadAsync("anonymous","51",BangumiRelationKind.Anime,default));
        await Assert.ThrowsAsync<IOException>(()=>second.Store().WriteAsync("anonymous",Sample(title:"new"),()=>true,default));
        await second.Store().RemoveAsync("anonymous","51",BangumiRelationKind.Anime,()=>true,default);Assert.Equal(before,File.ReadAllBytes(path));
    }
    private sealed class CredentialStore : IBangumiCredentialStore
    {
        public Task<string?> ReadAsync(CancellationToken token)=>Task.FromResult<string?>("Synthetic_scope_test_token_123456789");
        public Task SaveAsync(string value,CancellationToken token)=>Task.CompletedTask;
        public Task ClearAsync(CancellationToken token)=>Task.CompletedTask;
    }
    private sealed class Accounts : IBangumiAccountProvider
    { public Task<BangumiAccount> ValidateAsync(string token,CancellationToken cancellation)=>Task.FromResult(new BangumiAccount("123","synthetic","合成账号")); }
    [Fact]
    public async Task DisconnectBeforeAtomicCommitCannotPersistOrReuseAuthorizedResponse()
    {
        using var f=new Fixture();var access=new BangumiAccessContext();var auth=new BangumiAuthorization(new CredentialStore(),new Accounts(),access);
        await auth.InitializeAsync(default);var scope=access.Capture().Scope;
        var cache=f.Store(async stage=>{if(stage==PersistenceStage.BeforeReplace)await auth.ClearAsync(default);});
        var service=new BangumiSubjectDetailsService(Success(),()=>Stamp,access,cache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.LoadAsync("51",BangumiRelationKind.Anime,false,default));
        Assert.Null(await f.Store().ReadAsync(scope,"51",BangumiRelationKind.Anime,default));
        Assert.Null(await f.Store().ReadAsync("anonymous","51",BangumiRelationKind.Anime,default));
        Assert.Empty(Directory.GetFiles(f.CacheRoot,"*.tmp"));
    }
    [Fact]
    public async Task CacheFullEvictsOnlyOwnedMetadataAndDoesNotEditUserComponents()
    {
        using var f=new Fixture();var before=ProfileFixture.Hashes(f.Root);
        for(int i=1;i<=IsolatedBangumiSubjectDetailsCache.MaximumFiles+1;i++)await f.Store().WriteAsync("anonymous",Sample(i.ToString()),()=>true,default);
        Assert.Equal(IsolatedBangumiSubjectDetailsCache.MaximumFiles,Directory.GetFiles(f.CacheRoot).Length);
        Assert.NotNull(await f.Store().ReadAsync("anonymous","129",BangumiRelationKind.Anime,default)); Assert.Equal(before,ProfileFixture.Hashes(f.Root));
    }
    [Fact]
    public async Task UnwritableCacheDoesNotDiscardSuccessfulOnlineData()
    {
        using var f=new Fixture();Directory.CreateDirectory(f.CacheRoot);
        for(int i=0;i<=IsolatedBangumiSubjectDetailsCache.MaximumFiles;i++)File.WriteAllText(Path.Combine(f.CacheRoot,"unowned-"+i),"preserve");
        var result=await Service(Success(),f).LoadAsync("51",BangumiRelationKind.Anime,false,default);
        Assert.False(result.FromCache);Assert.NotNull(result.Warning);Assert.Equal(129,Directory.GetFiles(f.CacheRoot).Length);
    }
}
