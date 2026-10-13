using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiRelatedViewModelTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static BangumiRelationIdentity Identity(string local = "game-a", string subject = "42", string lifecycle = "lifecycle-a", string fingerprint = "A")
        => new(local, lifecycle, "100", subject, new string(fingerprint[0], 64));
    private static BangumiRelationItem Item(string id = "43", BangumiRelationKind kind = BangumiRelationKind.Anime)
        => new(id, kind, "关联名称", "Original", "原作", "https://lain.bgm.tv/pic/cover/l/test.jpg");
    private static BangumiRelationsLoadResult Result(string subject = "42", bool empty = false, bool cache = false, bool stale = false, string? warning = null)
        => new(new(subject, Stamp, empty ? [] : [Item()]), cache, stale, warning);
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Reader : IBangumiRelationsReader
    {
        public readonly List<(BangumiRelationIdentity Identity, bool Force, CancellationToken Token)> Calls = [];
        public Func<BangumiRelationIdentity, bool, CancellationToken, Task<BangumiRelationsLoadResult>> Read = (id, _, _) => Task.FromResult(Result(id.SubjectId));
        public Task<BangumiRelationsLoadResult> LoadAsync(BangumiRelationIdentity identity, bool forceRefresh, CancellationToken cancellation)
        { Calls.Add((identity, forceRefresh, cancellation)); return Read(identity, forceRefresh, cancellation); }
    }

    [Fact]
    public async Task ContextDoesNotFetchUntilExpandedAndReopeningDoesNotRefetch()
    {
        var reader = new Reader(); using var vm = new BangumiRelatedViewModel(reader);
        vm.SetContext(Identity()); Assert.Empty(reader.Calls); Assert.False(vm.IsExpanded);
        await vm.ExpandAsync(); Assert.Single(reader.Calls); Assert.Equal(BangumiRelatedState.Ready, vm.State);
        vm.Collapse(); await vm.ExpandAsync(); Assert.Single(reader.Calls); Assert.Single(vm.Items);
        await vm.RefreshAsync(); Assert.Equal(2, reader.Calls.Count); Assert.True(reader.Calls[1].Force);
    }

    [Theory]
    [InlineData("当前作品没有可靠的 Bangumi 编号。")]
    [InlineData("存在不同的 Bangumi 编号，请先核对。")]
    public async Task MissingOrConflictingIdentityNeverQueries(string reason)
    {
        var reader = new Reader(); using var vm = new BangumiRelatedViewModel(reader);
        vm.SetContext(null, reason); await vm.ExpandAsync(); await vm.RefreshAsync();
        Assert.Empty(reader.Calls); Assert.Equal(BangumiRelatedState.NoIdentity, vm.State);
        Assert.Equal(reason, vm.Message); Assert.False(vm.CanLoad);
    }

    [Fact]
    public async Task RepeatedExpansionWhileLoadingSharesTheCurrentRequest()
    {
        var gate = Gate<BangumiRelationsLoadResult>(); var reader = new Reader { Read = (_, _, _) => gate.Task };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity());
        var load = vm.ExpandAsync(); await vm.ExpandAsync(); await vm.RefreshAsync();
        Assert.Single(reader.Calls); Assert.True(vm.IsBusy); Assert.True(vm.CanCancel); Assert.False(vm.CanLoad);
        gate.SetResult(Result()); await load; Assert.False(vm.IsBusy); Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task FourMediaKindsAndRawRelationsAreRetained()
    {
        var rows = ImmutableArray.Create(Item("43", BangumiRelationKind.Anime), Item("44", BangumiRelationKind.Music), Item("45", BangumiRelationKind.Book), Item("46", BangumiRelationKind.Game));
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(new BangumiRelationsLoadResult(new("42", Stamp, rows), false, false, null)) };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); await vm.ExpandAsync();
        Assert.Equal(rows, vm.Items); Assert.All(vm.Items, row => Assert.Equal("原作", row.Relation));
        Assert.Equal("https://bgm.tv/subject/43", vm.Items[0].Url);
    }

    [Fact]
    public async Task VerifiedEmptyAndNetworkFailureAreDifferentStates()
    {
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(Result(empty: true)) };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); await vm.ExpandAsync();
        Assert.Equal(BangumiRelatedState.Empty, vm.State); Assert.True(vm.HasResult); Assert.Contains("暂无", vm.Message);
        vm.SetContext(Identity("game-b"));
        reader.Read = (_, _, _) => throw new BangumiRelationsException(BangumiRelationsFailure.Offline, "无法连接 Bangumi。");
        await vm.ExpandAsync(); Assert.Equal(BangumiRelatedState.Failed, vm.State); Assert.False(vm.HasResult);
        Assert.DoesNotContain("暂无", vm.Message); Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task CachedResultAndRefreshWarningRemainVisibleTogether()
    {
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(Result(cache: true, stale: true, warning: "刷新失败，保留缓存。")) };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); await vm.ExpandAsync();
        Assert.Equal(BangumiRelatedState.Ready, vm.State); Assert.True(vm.Result!.FromCache); Assert.True(vm.Result.IsStale);
        Assert.Contains("缓存", vm.Message); Assert.Contains("刷新失败", vm.Message); Assert.Single(vm.Items);
    }

    [Fact]
    public async Task RefreshFailurePreservesPreviouslyDisplayedResultAndRetries()
    {
        var reader = new Reader(); using var vm = new BangumiRelatedViewModel(reader);
        vm.SetContext(Identity()); await vm.ExpandAsync(); var original = vm.Result;
        reader.Read = (_, _, _) => throw new BangumiRelationsException(BangumiRelationsFailure.Server, "Bangumi 暂时不可用。");
        await vm.RefreshAsync(); Assert.Equal(BangumiRelatedState.Failed, vm.State); Assert.Same(original, vm.Result);
        Assert.Single(vm.Items); Assert.Contains("保留", vm.Message); Assert.True(vm.CanLoad);
        reader.Read = (_, _, _) => Task.FromResult(Result(empty: true)); await vm.RefreshAsync();
        Assert.Equal(BangumiRelatedState.Empty, vm.State); Assert.Empty(vm.Items);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("collapse")]
    [InlineData("leave")]
    [InlineData("dispose")]
    public async Task CancelledOrHiddenPageRejectsEvenAProviderThatIgnoresCancellation(string action)
    {
        var gate = Gate<BangumiRelationsLoadResult>(); var reader = new Reader { Read = (_, _, _) => gate.Task };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); var load = vm.ExpandAsync();
        switch (action) { case "cancel": vm.Cancel(); break; case "collapse": vm.Collapse(); break; case "leave": vm.Deactivate(); break; default: vm.Dispose(); break; }
        Assert.True(reader.Calls[0].Token.IsCancellationRequested);
        gate.SetResult(Result()); await load; Assert.False(vm.HasResult);
        if (action == "cancel") { Assert.Equal(BangumiRelatedState.Cancelled, vm.State); Assert.True(vm.CanLoad); }
        else Assert.False(vm.CanLoad);
    }

    [Fact]
    public async Task OldARequestCannotReplaceNewAAfterAtoBtoA()
    {
        var old = Gate<BangumiRelationsLoadResult>(); var reader = new Reader();
        reader.Read = (id, _, _) => reader.Calls.Count == 1 ? old.Task : Task.FromResult(Result(id.SubjectId, empty: true));
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); var pending = vm.ExpandAsync();
        vm.SetContext(Identity("game-b", "52")); await vm.ExpandAsync();
        vm.SetContext(Identity()); await vm.ExpandAsync(); var latest = vm.Result;
        old.SetResult(Result()); await pending;
        Assert.Same(latest, vm.Result); Assert.Equal(BangumiRelatedState.Empty, vm.State); Assert.Empty(vm.Items);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("lifecycle")]
    [InlineData("subject")]
    [InlineData("fingerprint")]
    [InlineData("nextmoe")]
    public async Task EveryIdentityComponentInvalidatesInFlightData(string change)
    {
        var gate = Gate<BangumiRelationsLoadResult>(); var reader = new Reader { Read = (_, _, _) => gate.Task };
        using var vm = new BangumiRelatedViewModel(reader); var original = Identity(); vm.SetContext(original); var pending = vm.ExpandAsync();
        var replacement = change switch
        {
            "local" => original with { LocalGameId = "game-b" }, "lifecycle" => original with { Lifecycle = "recreated" },
            "subject" => original with { SubjectId = "52" }, "nextmoe" => original with { NextMoeId = "101" },
            _ => original with { Fingerprint = new string('B', 64) }
        };
        vm.SetContext(replacement); gate.SetResult(Result()); await pending;
        Assert.Equal(replacement, vm.Identity); Assert.False(vm.HasResult); Assert.False(vm.IsExpanded); Assert.Equal(BangumiRelatedState.Idle, vm.State);
    }

    [Fact]
    public async Task CurrentLibraryCheckRejectsAnIdentityChangeBeforeVisualNotification()
    {
        var gate = Gate<BangumiRelationsLoadResult>(); var reader = new Reader { Read = (_, _, _) => gate.Task }; var current = true;
        using var vm = new BangumiRelatedViewModel(reader, _ => current); vm.SetContext(Identity()); var pending = vm.ExpandAsync();
        current = false; gate.SetResult(Result()); await pending;
        Assert.False(vm.HasResult); Assert.False(vm.IsBusy); Assert.Null(vm.Identity); Assert.Equal(BangumiRelatedState.Cancelled, vm.State);
        await vm.RefreshAsync(); Assert.Single(reader.Calls);
    }

    [Fact]
    public async Task CancellationDuringRefreshRetainsCardsAndAllowsANewRequest()
    {
        var reader = new Reader(); using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); await vm.ExpandAsync();
        var original = vm.Result; var old = Gate<BangumiRelationsLoadResult>(); reader.Read = (_, _, _) => old.Task;
        var pending = vm.RefreshAsync(); vm.Cancel(); Assert.Same(original, vm.Result); Assert.Single(vm.Items);
        reader.Read = (_, _, _) => Task.FromResult(Result(empty: true)); await vm.RefreshAsync();
        old.SetResult(Result()); await pending; Assert.Equal(BangumiRelatedState.Empty, vm.State); Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task WrongSubjectAndMalformedItemsNeverBecomeSuccess()
    {
        var reader = new Reader { Read = (_, _, _) => Task.FromResult(Result("52")) };
        using var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity()); await vm.ExpandAsync();
        Assert.Equal(BangumiRelatedState.Failed, vm.State); Assert.False(vm.HasResult);
        reader.Read = (_, _, _) => Task.FromResult(new BangumiRelationsLoadResult(new("42", Stamp, [Item("43", (BangumiRelationKind)99)]), false, false, null));
        await vm.RefreshAsync(); Assert.Equal(BangumiRelatedState.Failed, vm.State); Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task DeactivatedContextCanBeReenteredAndDisposedInstanceNeverRunsAgain()
    {
        var reader = new Reader(); var vm = new BangumiRelatedViewModel(reader); vm.SetContext(Identity());
        vm.Deactivate(); await vm.ExpandAsync(); Assert.Empty(reader.Calls);
        vm.SetContext(Identity()); await vm.ExpandAsync(); Assert.Single(reader.Calls);
        vm.Dispose(); vm.Dispose(); vm.SetContext(Identity("another")); await vm.ExpandAsync(); await vm.RefreshAsync();
        Assert.Single(reader.Calls); Assert.False(vm.CanLoad);
    }
}
