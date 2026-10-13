using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

public sealed class BangumiRelatedSemanticsTests
{
    private static BangumiRelationItem Item(string id, string relation = "动画", BangumiRelationKind kind = BangumiRelationKind.Anime) => new(id, kind, "Synthetic", "Synthetic", relation, null);
    private static string Rows(params (int Id, int Type, string Relation)[] values) => JsonSerializer.Serialize(values.Select(v => new { id = v.Id, type = v.Type, name = "Synthetic", name_cn = "", relation = v.Relation }));
    [Fact]
    public async Task ExactReverseIdentityAndKindAreRequiredAndCacheIsReused()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Rows((14, 4, "角色出演"), (13, 4, "游戏"))));
        }));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        var result = await provider.LoadAsync("13", [Item("51")], default);
        Assert.Equal(new RelatedSemanticEvidence("13", "51", "游戏"), Assert.Single(result));
        Assert.Equal(result, await provider.LoadAsync("13", [Item("51")], default));
        Assert.Equal(new[] { "/v0/subjects/51/subjects" }, calls);
    }
    [Fact]
    public async Task DuplicateReverseLabelsAreKeptForConservativeConflictResolution()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Rows((13, 4, "游戏"), (13, 4, "角色出演"))))));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        var evidence = await provider.LoadAsync("13", [Item("51")], default);
        Assert.Equal(2, evidence.Length);
        Assert.Equal("关系待核对", Assert.Single(RelatedWorksProjection.Build([], [Item("51")], evidence)).Label);
    }
    [Fact]
    public async Task BoundedRequestsOnlyRefineGenericAnimeAndNeverLoseOtherCards()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Rows((13, 4, "游戏")))); }));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        ImmutableArray<BangumiRelationItem> items = [..Enumerable.Range(100, 12).Select(id => Item(id.ToString())), Item("616", "原声集", BangumiRelationKind.Music), Item("99", "角色出演")];
        var evidence = await provider.LoadAsync("13", items, default);
        Assert.Equal(BangumiRelationsProvider.MaximumSemanticSubjects, calls); Assert.Equal(8, evidence.Length);
        Assert.Equal(items.Length, RelatedWorksProjection.Build([], items, evidence).Length);
    }
    [Fact]
    public async Task FailuresLeaveConservativeLabelsAndAreNotCached()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1 ? Json("{}", 404) : Json(Rows((13, 4, "角色出演"))))));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        Assert.Empty(await provider.LoadAsync("13", [Item("51")], default));
        Assert.Equal("角色出演", Assert.Single(await provider.LoadAsync("13", [Item("51")], default)).ReverseRelation);
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task WrongKindAndWrongRootCannotClassify()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(++calls == 1 ? Rows((13, 2, "角色出演")) : "{}"))));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        Assert.Empty(await provider.LoadAsync("13", [Item("51"), Item("52")], default));
    }
    [Fact]
    public async Task CancellationDuringBodyReadNeverPublishesPartialEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) => { cancellation.Cancel(); return Task.FromResult(Json(Rows((13, 4, "角色出演")))); }));
        var provider = new BangumiRelationsProvider(http, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoadAsync("13", [Item("51")], cancellation.Token));
    }
    [Fact]
    public async Task DeadlineReturnsNoInventedEvidenceAndDefaultAuthenticationIsRejectedBeforeSend()
    {
        using var http = new HttpClient(new Handler(async (_, c) => { await Task.Delay(500, c); return Json("[]"); }));
        var provider = new BangumiRelationsProvider(http, operationTimeout: TimeSpan.FromMilliseconds(10));
        Assert.Empty(await provider.LoadAsync("13", [Item("51")], default));
        http.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic");
        await Assert.ThrowsAsync<BangumiRelationsException>(() => provider.LoadAsync("13", [Item("51")], default));
    }
}
