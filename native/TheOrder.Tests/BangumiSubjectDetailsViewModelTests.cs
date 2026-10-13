using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiSubjectDetailsViewModelTests
{
    private static BangumiRelationItem Item(string id = "51") => new(id, BangumiRelationKind.Anime, "卡片标题", "original", "动画", null);
    private static BangumiSubjectDetailsResult Result(string id = "51", BangumiRelationKind kind = BangumiRelationKind.Anime)
        => new(new(id, kind, "详情标题", "original", null, "简介", null, null, null, null, null, DateTimeOffset.UtcNow), false, false, null);
    private sealed class Reader : IBangumiSubjectDetailsReader
    {
        public readonly List<(string Id, bool Force, CancellationToken Token)> Calls = [];
        public Func<string, BangumiRelationKind, CancellationToken, Task<BangumiSubjectDetailsResult>> Read = (id, kind, _) => Task.FromResult(Result(id, kind));
        public Task<BangumiSubjectDetailsResult> LoadAsync(string subjectId, BangumiRelationKind expectedKind, bool forceRefresh, CancellationToken cancellation)
        { Calls.Add((subjectId, forceRefresh, cancellation)); return Read(subjectId, expectedKind, cancellation); }
    }
    [Fact] public async Task OpenLoadsExactItemAndRetainsRelationEvidence()
    {
        var reader = new Reader(); using var vm = new BangumiSubjectDetailsViewModel(reader); var item = Item(); await vm.OpenAsync(item);
        Assert.Same(item, vm.Item); Assert.Equal("动画", vm.Item!.Relation); Assert.Equal(BangumiSubjectDetailsState.Ready, vm.State);
        Assert.Equal("详情标题", vm.Result!.Details.Title); Assert.Empty(vm.Message); Assert.True(vm.CanLoad); Assert.False(vm.CanCancel);
        await vm.RefreshAsync(); Assert.True(reader.Calls[1].Force);
    }
    [Fact] public async Task RapidOpenAndReturnRejectLateResultsIncludingSameIdRoundTrip()
    {
        var first = new TaskCompletionSource<BangumiSubjectDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously); int count = 0;
        var reader = new Reader { Read = (id, kind, _) => ++count == 1 ? first.Task : Task.FromResult(Result(id, kind)) };
        using var vm = new BangumiSubjectDetailsViewModel(reader); var old = vm.OpenAsync(Item()); await vm.OpenAsync(Item("876")); await vm.OpenAsync(Item());
        first.SetResult(Result() with { Details = Result().Details with { Title = "old" } }); await old;
        Assert.True(reader.Calls[0].Token.IsCancellationRequested); Assert.Equal("详情标题", vm.Result!.Details.Title); Assert.Equal("51", vm.Item!.Id);
    }
    [Fact] public async Task ExplicitCancelIsRetryableAndLateSuccessCannotChangeIt()
    {
        var gate = new TaskCompletionSource<BangumiSubjectDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Read = (_, _, _) => gate.Task }; using var vm = new BangumiSubjectDetailsViewModel(reader); var load = vm.OpenAsync(Item());
        Assert.True(vm.CanCancel); await vm.RefreshAsync(); Assert.Single(reader.Calls);
        vm.Cancel(); gate.SetResult(Result()); await load;
        Assert.Equal(BangumiSubjectDetailsState.Cancelled, vm.State); Assert.Null(vm.Result); Assert.True(vm.CanLoad); Assert.False(vm.CanCancel);
        reader.Read = (id, kind, _) => Task.FromResult(Result(id, kind)); await vm.RefreshAsync(); Assert.Equal(BangumiSubjectDetailsState.Ready, vm.State);
    }
    [Fact] public async Task NavigationCancellationCannotLeaveBusyState()
    {
        var gate = new TaskCompletionSource<BangumiSubjectDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Read = (_, _, _) => gate.Task }; using var vm = new BangumiSubjectDetailsViewModel(reader); using var token = new CancellationTokenSource();
        var load = vm.OpenAsync(Item(), token.Token); token.Cancel(); gate.SetResult(Result()); await load;
        Assert.Equal(BangumiSubjectDetailsState.Cancelled, vm.State); Assert.False(vm.IsBusy); Assert.Null(vm.Result);
    }
    [Fact] public async Task DeactivationCancelsAndPreventsHiddenRefresh()
    {
        var gate = new TaskCompletionSource<BangumiSubjectDetailsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Read = (_, _, _) => gate.Task }; using var vm = new BangumiSubjectDetailsViewModel(reader); var load = vm.OpenAsync(Item());
        vm.Deactivate(); gate.SetResult(Result()); await load; await vm.RefreshAsync(); Assert.Single(reader.Calls); Assert.Null(vm.Result); Assert.False(vm.CanLoad); Assert.False(vm.CanCancel);
    }
    [Theory] [InlineData(BangumiRelationsFailure.Unavailable)][InlineData(BangumiRelationsFailure.InvalidResponse)]
    public async Task AccessOrIdentityFailureClearsExistingDetail(BangumiRelationsFailure failure)
    {
        var reader = new Reader(); using var vm = new BangumiSubjectDetailsViewModel(reader); await vm.OpenAsync(Item());
        reader.Read = (_, _, _) => throw new BangumiRelationsException(failure, "拒绝"); await vm.RefreshAsync();
        Assert.Equal(BangumiSubjectDetailsState.Failed, vm.State); Assert.Null(vm.Result); Assert.Equal("动画", vm.Item!.Relation); Assert.True(vm.CanLoad);
    }
    [Fact] public async Task WrongReturnedTypeCannotPopulatePage()
    {
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(Result(kind: BangumiRelationKind.Game)) }; using var vm = new BangumiSubjectDetailsViewModel(reader);
        await vm.OpenAsync(Item()); Assert.Equal(BangumiSubjectDetailsState.Failed, vm.State); Assert.Null(vm.Result);
    }
    [Fact] public async Task OfflineStaleSuccessHasVisibleWarningButNoRoutineCacheTimestamp()
    {
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(Result() with { FromCache = true, IsStale = true, Warning = "离线，保留上次资料。" }) };
        using var vm = new BangumiSubjectDetailsViewModel(reader); await vm.OpenAsync(Item()); Assert.Equal(BangumiSubjectDetailsState.Ready, vm.State); Assert.Contains("离线", vm.Message);
        reader.Read = (_, _, _) => Task.FromResult(Result() with { FromCache = true }); await vm.RefreshAsync(); Assert.Empty(vm.Message);
    }
    [Fact] public async Task InvalidCardNeverRequestsAndDisposePreventsNewWork()
    {
        var reader = new Reader(); var vm = new BangumiSubjectDetailsViewModel(reader); await vm.OpenAsync(Item("../51")); Assert.Empty(reader.Calls);
        vm.Dispose(); await vm.OpenAsync(Item()); await vm.RefreshAsync(); Assert.Empty(reader.Calls);
    }
}
