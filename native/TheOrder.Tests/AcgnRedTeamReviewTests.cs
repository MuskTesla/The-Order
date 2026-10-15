using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class AcgnRedTeamReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupplementVoiceCannotBecomeEvidenceForAnotherCharacter(bool reverseSupplements)
    {
        var anchor = Character("81", true, "1");
        var supplement = Character("82", false, "1", "2");
        var unrelated = Character("83", false, "2");
        var work = reverseSupplements
            ? Map(anchor, unrelated, supplement)
            : Map(anchor, supplement, unrelated);

        // 82 can supplement the unique original anchor through Actor 1.
        // Its additional Actor 2 cannot then authorize absorbing character 83.
        Assert.Equal(new[] { "81", "83" }, work.Presentation!.Characters.Select(x => x.Entity.Id).Order().ToArray());
        Assert.Equal(new[] { "81", "83" }, work.Characters.Select(x => x.Id).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalRowsOfTheSameExactCharacterMayAggregateVoiceEvidence(bool reverseExactRows)
    {
        var illustrated = Character("81", true, "1");
        var sameIdentity = Character("81", false, "2");
        var supplement = Character("82", false, "2");
        var work = reverseExactRows
            ? Map(sameIdentity, illustrated, supplement)
            : Map(illustrated, sameIdentity, supplement);

        Assert.Equal("81", Assert.Single(work.Presentation!.Characters).Entity.Id);
        Assert.Equal("81", Assert.Single(work.Characters).Id);
    }

    private static JsonObject Character(string id, bool illustrated, params string[] actors) => new()
    {
        ["id"] = id, ["display_name"] = "Synthetic same-name character",
        ["roster_role"] = illustrated ? "main" : "unknown", ["spoiler"] = "none",
        ["image"] = illustrated ? new JsonObject
        {
            ["url"] = "https://images.example/" + id + ".png", ["width"] = 250, ["height"] = 300
        } : null,
        ["voices"] = new JsonArray(actors.Select(actor => (JsonNode)new JsonObject
        {
            ["id"] = "10" + actor, ["person_id"] = actor, ["display_name"] = "Synthetic actor " + actor
        }).ToArray())
    };

    private static WorkMetadata Map(params JsonObject[] characters)
    {
        var root = new JsonObject
        {
            ["object"] = "work", ["id"] = "7001", ["medium"] = "galgame",
            ["display_name"] = "Synthetic roster review",
            ["characters"] = new JsonArray(characters.Cast<JsonNode>().ToArray())
        };
        using var body = JsonDocument.Parse(root.ToJsonString());
        return new RemoteWorkDto(body.RootElement).Map(true, DateTimeOffset.UnixEpoch);
    }
}
