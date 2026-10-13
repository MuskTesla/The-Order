using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

public sealed class RelatedWorksProjectionTests
{
    private static BangumiRelationItem Item(string id, BangumiRelationKind kind, string relation, string title = "Same title") => new(id, kind, title, title, relation, null);
    private static CatalogEntityRef Catalog(string id, string type, string? phrase = null) => new(CatalogEntityKind.Work, id, "Same title", phrase, Relation: type) { KunGalGalgameId = id };
    [Fact]
    public void SameNamesAndSameNumbersFromDifferentProvidersRemainSeparate()
    {
        var cards = RelatedWorksProjection.Build([Catalog("51", "sequel_of", "续作")], [Item("51", BangumiRelationKind.Game, "游戏"), Item("52", BangumiRelationKind.Game, "游戏")]);
        Assert.Equal(3, cards.Length); Assert.Equal(3, cards.Select(card => card.Key).Distinct().Count());
    }
    [Fact]
    public void ExactCrossSourceProofKeepsKunGalCardAndBothSourcesEvidence()
    {
        var cards = RelatedWorksProjection.Build([Catalog("3407", "sequel_of", "有续作")],
            [Item("3660", BangumiRelationKind.Game, "外传", "A different display title")],
            gameIdentities: [new("3407", "3407", "3660")]);
        var card = Assert.Single(cards);
        Assert.Equal("nextmoe:3407", card.Key); Assert.Null(card.Bangumi);
        Assert.Equal("3660", card.LinkedBangumiSubjectId); Assert.Equal("有续作", card.Label);
        Assert.Contains("Bangumi 关系：外传", card.Evidence); Assert.Contains("KunGal 3407 → Bangumi 3660", card.Evidence);
    }
    [Fact]
    public void MultipleCatalogClaimsForOneBridgeIdentityCannotSuppressAnyCard()
    {
        var cards = RelatedWorksProjection.Build([Catalog("1", "same_series"), Catalog("2", "same_series")],
            [Item("51", BangumiRelationKind.Game, "游戏")], gameIdentities: [new("1", "1", "51"), new("2", "2", "51")]);
        Assert.Equal(3, cards.Length); Assert.All(cards, c => Assert.Null(c.LinkedBangumiSubjectId));
    }
    [Fact]
    public void ConflictingBridgeReferencesDoNotMergeOrGuessTheChapter()
    {
        var cards = RelatedWorksProjection.Build([Catalog("1", "side_story_of")],
            [Item("51", BangumiRelationKind.Game, "外传"), Item("52", BangumiRelationKind.Game, "外传")],
            gameIdentities: [new("1", "1", "51"), new("1", "1", "52")]);
        Assert.Equal(3, cards.Length);
    }
    [Theory]
    [InlineData(BangumiRelationKind.Anime)]
    [InlineData(BangumiRelationKind.Music)]
    [InlineData(BangumiRelationKind.Book)]
    public void GameProofCannotSuppressAnotherMediaType(BangumiRelationKind kind)
    {
        var cards = RelatedWorksProjection.Build([Catalog("1", "side_story_of")], [Item("51", kind, "其他")], gameIdentities: [new("1", "1", "51")]);
        Assert.Equal(2, cards.Length);
    }
    [Fact]
    public void WrongKunGalClaimAndMediaTypeConflictAreNotAcceptedAsBridgeProof()
    {
        Assert.Equal(2, RelatedWorksProjection.Build([Catalog("1", "side_story_of")], [Item("51", BangumiRelationKind.Game, "外传")],
            gameIdentities: [new("1", "99", "51")]).Length);
        Assert.Equal(2, RelatedWorksProjection.Build([Catalog("1", "side_story_of")],
            [Item("51", BangumiRelationKind.Game, "外传"), Item("51", BangumiRelationKind.Anime, "动画")], gameIdentities: [new("1", "1", "51")]).Length);
    }
    [Fact]
    public void ReliableDuplicatesRetainEveryDifferentRelationInEvidence()
    {
        var cards = RelatedWorksProjection.Build([Catalog("1", "fandisc_of", "Fandisc"), Catalog("1", "shares_character", "角色出演")],
            [Item("51", BangumiRelationKind.Anime, "动画"), Item("51", BangumiRelationKind.Anime, "角色出演")]);
        Assert.Equal(2, cards.Length);
        Assert.All(cards, card => { Assert.Contains("角色出演", card.Evidence); Assert.Equal("多种关系 · 查看来源", card.Label); });
        Assert.Contains("fandisc_of", cards.Single(card => card.Catalog is not null).Evidence);
        Assert.Contains("动画", cards.Single(card => card.Bangumi is not null).Evidence);
    }
    [Fact]
    public void ExplicitReverseRolesSeparateCrossoversWithoutInferringFromTitles()
    {
        var items = new[] { Item("333598", BangumiRelationKind.Anime, "动画", "Completely unrelated display name"),
            Item("51", BangumiRelationKind.Anime, "动画", "键等"), Item("876", BangumiRelationKind.Anime, "动画", "Another title") };
        var cards = RelatedWorksProjection.Build([], items, [new("13", "333598", "角色出演"), new("13", "51", "游戏"), new("13", "876", "游戏")]);
        Assert.Equal(new[] { "51", "876", "333598" }, cards.Select(card => card.Id));
        Assert.Equal("角色出演 · 跨作品关联", cards[^1].Label);
        Assert.All(cards.Take(2), card => { Assert.Equal("动画关联", card.Label); Assert.DoesNotContain("改编", card.Label); });
        Assert.Contains("333598 → 13：角色出演", cards[^1].Evidence); Assert.Equal("动画", cards[^1].RawRelation);
    }
    [Fact]
    public void ConflictingReverseEdgesOrAnchorIdentityCannotAssertCrossover()
    {
        var item = Item("51", BangumiRelationKind.Anime, "动画");
        var cards = RelatedWorksProjection.Build([], [item], [new("13", "51", "游戏"), new("13", "51", "角色出演")]);
        Assert.Equal("关系待核对", Assert.Single(cards).Label);
        cards = RelatedWorksProjection.Build([], [item], [new("13", "51", "角色出演"), new("14", "51", "角色出演")]);
        Assert.Equal("动画关联 · 关系未细分", Assert.Single(cards).Label);
    }
    [Fact]
    public void WrongKindOrWrongTargetReverseEvidenceDoesNotReclassify()
    {
        var cards = RelatedWorksProjection.Build([], [Item("51", BangumiRelationKind.Anime, "动画"), Item("616", BangumiRelationKind.Music, "原声集")],
            [new("13", "52", "角色出演"), new("13", "616", "角色出演")]);
        Assert.Equal("原声音乐", cards[0].Label); Assert.Equal("动画关联 · 关系未细分", cards[1].Label);
    }
    [Theory]
    [InlineData("原声集", "原声音乐", 10)]
    [InlineData("片头曲", "片头曲", 20)]
    [InlineData("片尾曲", "片尾曲", 20)]
    [InlineData("插入歌", "插入歌", 20)]
    [InlineData("广播剧", "广播剧", 40)]
    [InlineData("印象曲", "印象曲", 40)]
    [InlineData("未收录的关系", "未收录的关系", 80)]
    public void MusicSemanticPriorityComesFromRelationshipNotTitle(string raw, string label, int priority)
    {
        var card = Assert.Single(RelatedWorksProjection.Build([], [Item("616", BangumiRelationKind.Music, raw)]));
        Assert.Equal(label, card.Label); Assert.Equal(priority, card.Priority); Assert.Equal(raw, card.RawRelation);
    }
    [Fact]
    public void SoundtrackTitleWithUnknownRelationIsNeverPromoted()
    {
        var cards = RelatedWorksProjection.Build([], [Item("1", BangumiRelationKind.Music, "其他", "CLANNAD Original Soundtrack"), Item("2", BangumiRelationKind.Music, "原声集", "Unknown")]);
        Assert.Equal("2", cards[0].Id); Assert.Equal("其他关联", cards[1].Label);
    }
    [Theory]
    [InlineData("fandisc_of", "本篇")]
    [InlineData("side_story_of", "本篇")]
    [InlineData("sequel_of", "前作")]
    [InlineData("unexpected_relation", "来源自定义说明")]
    public void CatalogDirectionAndUnknownPhraseArePreserved(string type, string phrase)
    {
        var card = Assert.Single(RelatedWorksProjection.Build([Catalog("1", type, phrase)], []));
        Assert.Equal(phrase, card.Label); Assert.Contains(type, card.Evidence);
    }
    [Fact]
    public void ClaimedSequelIsNotPromotedAsVerifiedOfficialAndCompatibleRelationsRemainVisible()
    {
        var cards = RelatedWorksProjection.Build([
            Catalog("8526", "fandisc_of", "有 Fandisc"), Catalog("8526", "side_story_of", "有外传"),
            Catalog("3407", "sequel_of", "有续作"), Catalog("3407", "side_story_of", "有外传"),
            Catalog("12367", "sequel_of", "有续作")], []);
        Assert.Equal(new[] { "8526", "3407", "12367" }, cards.Select(card => card.Id));
        Assert.All(cards, card => Assert.Equal(40, card.Priority));
        Assert.Equal("有 Fandisc / 有外传", cards[0].Label); Assert.Equal("衍生与外传", cards[0].GroupName);
        Assert.Equal("有续作 / 有外传", cards[1].Label); Assert.Equal("前后作与衍生", cards[1].GroupName);
        Assert.Equal("前后作关联", cards[2].GroupName); Assert.DoesNotContain("正式", cards[2].Label);
    }
    [Theory]
    [InlineData("前作", "有外传")]
    [InlineData("是…的续作", "有外传")]
    [InlineData("有续作", "本篇")]
    [InlineData("未知方向", "有外传")]
    public void OppositeOrUnknownDirectionsCannotBeCombinedAsDerivedWorks(string sequelPhrase, string sideStoryPhrase)
    {
        var card = Assert.Single(RelatedWorksProjection.Build([Catalog("1", "sequel_of", sequelPhrase), Catalog("1", "side_story_of", sideStoryPhrase)], []));
        Assert.Equal(80, card.Priority); Assert.Equal("多种关系 · 查看来源", card.Label);
    }
    [Fact]
    public void AdaptationDirectionIsNotGuessedFromRelationType()
    {
        var cards = RelatedWorksProjection.Build([Catalog("1", "adaptation_of", "被改编为"), Catalog("2", "adaptation_of", "改编自"), Catalog("3", "adaptation_of")], []);
        Assert.Equal("改编作品", cards[0].Label); Assert.Equal("改编来源", cards[1].Label); Assert.Equal("关系未细分", cards[2].Label);
    }
    [Fact]
    public void LargeProjectionNeverDropsTheEndOfEitherSource()
    {
        var catalog = Enumerable.Range(1, 100).Select(id => Catalog(id.ToString(), "unknown"));
        var bangumi = Enumerable.Range(1, 256).Select(id => Item(id.ToString(), BangumiRelationKind.Book, "书籍"));
        var cards = RelatedWorksProjection.Build(catalog, bangumi);
        Assert.Equal(356, cards.Length); Assert.Contains(cards, c => c.Key == "nextmoe:100"); Assert.Contains(cards, c => c.Key == "bangumi:256");
    }
    [Fact]
    public void MediaTypeConflictRemainsConservativeAndUnverifiedCatalogRowsStayExcluded()
    {
        var cards = RelatedWorksProjection.Build([new(CatalogEntityKind.Work, "1", "No claimed page")],
            [Item("51", BangumiRelationKind.Anime, "动画"), Item("51", BangumiRelationKind.Game, "游戏")]);
        Assert.Equal("媒体类型待核对", Assert.Single(cards).Label);
    }
    [Theory]
    [InlineData("游戏")]
    [InlineData("角色出演")]
    public void ReverseEvidenceCannotOverrideConflictingMediaTypes(string reverse)
    {
        var cards = RelatedWorksProjection.Build([], [Item("51", BangumiRelationKind.Anime, "动画"), Item("51", BangumiRelationKind.Game, "动画")],
            [new("13", "51", reverse)]);
        Assert.Equal("媒体类型待核对", Assert.Single(cards).Label);
    }
}
