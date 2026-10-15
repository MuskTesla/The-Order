using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class AniListUnknownRelationFormatTests
{
    // The live 2026-10-15 response for anime 30 linked anime 208192 with
    // format:null. The parent had complete cast, staff, trailer and TV format.
    // Keep the fixture synthetic; only the nullable relation shape is needed.
    private static JsonObject ParentWithUpcomingRelation()
    {
        var parent = JsonNode.Parse(AniListTestData.DetailJson)!.AsObject();
        var relation = JsonNode.Parse(AniListTestData.MediaJson)!.AsObject();
        relation["id"] = 208192;
        relation["idMal"] = null;
        relation["format"] = null;
        relation["status"] = "NOT_YET_RELEASED";
        relation["startDate"] = new JsonObject();
        relation["endDate"] = new JsonObject();
        parent["relations"] = new JsonObject
        {
            ["edges"] = new JsonArray(new JsonObject { ["relationType"] = "OTHER", ["node"] = relation })
        };
        return parent;
    }
    private static AniListDetails Map(JsonNode node)
    {
        using var json = JsonDocument.Parse(node.ToJsonString());
        return AniListProvider.MapDetails(json.RootElement, 1, AniListTestData.Stamp);
    }
    [Fact]
    public void UnknownRelationFormatPreservesParentDetailsAndRelatedIdentity()
    {
        var details = Map(ParentWithUpcomingRelation());
        Assert.Equal(2167, details.Media.Id);
        Assert.Equal("TV", details.Media.Format);
        Assert.NotEmpty(details.Description);
        Assert.Single(details.Characters);
        Assert.Single(details.Staff);
        var relation = Assert.Single(details.Relations);
        Assert.Equal(208192, relation.Media.Id);
        Assert.Equal(AniListMediaKind.Anime, relation.Media.Kind);
        Assert.Equal("", relation.Media.Format);
        Assert.Equal("OTHER", relation.Relation);
        Assert.True(AniListValidation.Details(details));
    }
    [Fact]
    public void UnknownRelationFormatSurvivesCacheRoundTripWithoutBecomingPrimaryIdentity()
    {
        var details = Map(ParentWithUpcomingRelation());
        var cache = new AniListCacheValue("media:0:2167:1", AniListTestData.Stamp, Details: details);
        var restored = JsonSerializer.Deserialize<AniListCacheValue>(JsonSerializer.Serialize(cache));
        Assert.True(AniListValidation.Cache(restored));
        var media = Assert.Single(restored!.Details!.Relations).Media;
        Assert.False(AniListValidation.Media(media));
        Assert.False(AniListMatching.StrongEvidence(AniListTestData.Source(), media));
    }
    [Fact]
    public void PrimaryMediaStillRequiresKnownFormat()
    {
        var parent = ParentWithUpcomingRelation();
        parent["format"] = null;
        var error = Assert.Throws<AniListException>(() => Map(parent));
        Assert.Equal(AniListFailure.InvalidResponse, error.Failure);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void MalformedRelationFormatStillRejectsResponse(string value)
    {
        var parent = ParentWithUpcomingRelation();
        parent["relations"]!["edges"]![0]!["node"]!["format"] = JsonNode.Parse(value);
        Assert.Throws<AniListException>(() => Map(parent));
    }
    [Fact]
    public void OptionalRelationMetadataDoesNotWeakenStableIdValidation()
    {
        var parent = ParentWithUpcomingRelation();
        parent["relations"]!["edges"]![0]!["node"]!["id"] = 0;
        Assert.Throws<AniListException>(() => Map(parent));
    }
    [Fact]
    public void UnknownBookRelationDoesNotInventMangaOrNovelFormat()
    {
        var parent = ParentWithUpcomingRelation();
        parent["relations"]!["edges"]![0]!["node"]!["type"] = "MANGA";
        var relation = Assert.Single(Map(parent).Relations);
        Assert.Equal(AniListMediaKind.Manga, relation.Media.Kind);
        Assert.Equal("书籍", relation.Media.TypeName);
        Assert.Equal("https://anilist.co/manga/208192", relation.Media.Url);
    }
    [Fact]
    public void RelationCacheRejectsWhitespaceFormatRatherThanTreatingItAsUnknown()
    {
        var details = AniListTestData.Details();
        var relation = new AniListRelation(AniListTestData.Media() with { Format = " " }, "OTHER");
        Assert.False(AniListValidation.Details(details with { Relations = [relation] }));
    }
}
