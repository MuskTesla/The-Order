using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

// Separate baseline attacks from Agent A's design review. Production changes
// for these cases require main-agent adjudication; no real API is contacted.
public sealed class AcgnRedTeamCatalogTests
{
    [Theory]
    [InlineData("anime")]
    [InlineData("manga")]
    [InlineData("novel")]
    [InlineData("music")]
    public void ExplicitNonGameMediumMustNotMapToGameMetadata(string medium)
    {
        using var body = JsonDocument.Parse(WorkRow(medium).ToJsonString());
        Assert.Throws<OnlineException>(() => new RemoteWorkDto(body.RootElement).Map(true, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task DetailEndpointMustRejectExplicitAnimeInsteadOfManufacturingGalgameUrl()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(WorkRow("anime").ToJsonString()))));
        var catalog = new NextMoeCatalog(new CatalogTransport(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var error = await Assert.ThrowsAsync<OnlineException>(() => catalog.DetailsAsync("7001", false, default));
        Assert.Equal(OnlineFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCompleteCharactersWithSameNameAndVoiceKeepDistinctIds(bool reverse)
    {
        var a = Character("81", "main", true); var b = Character("82", "secondary", true);
        var rows = reverse ? new[] { b, a } : new[] { a, b };
        var work = MapCharacters(rows);
        Assert.Equal(2, work.Presentation!.Characters.Length);
        Assert.Equal(new[] { "81", "82" }, work.Presentation.Characters.Select(c => c.Entity.Id).Order().ToArray());
        Assert.Equal(2, work.Characters.Length);
    }

    [Fact]
    public void AmbiguousSupplementMustNotBridgeTwoCompleteHomonymsTransitively()
    {
        var work = MapCharacters(Character("81", "main", true), Character("83", "unknown", false), Character("82", "secondary", true));
        Assert.Equal(3, work.Presentation!.Characters.Length);
        Assert.Equal(new[] { "81", "82", "83" }, work.Presentation.Characters.Select(c => c.Entity.Id).Order().ToArray());
    }

    [Fact]
    public void Control_GalgameMappingAndExactCharacterIdDeduplicationRemainSupported()
    {
        var work = MapCharacters(Character("81", "main", true), Character("81", "main", true));
        Assert.Equal("7001", work.Id); Assert.Equal("https://www.kungal.com/galgame/7001", work.WebUrl);
        Assert.Equal("81", Assert.Single(work.Presentation!.Characters).Entity.Id);
    }

    private static JsonObject WorkRow(string medium) => new() { ["object"] = "work", ["id"] = "7001",
        ["display_name"] = "Synthetic ACGN homonym", ["medium"] = medium, ["content_limit"] = "sfw" };
    private static JsonObject Character(string id, string role, bool illustrated) => new()
    {
        ["id"] = id, ["display_name"] = "同名角色", ["roster_role"] = role, ["spoiler"] = "none",
        ["image"] = illustrated ? new JsonObject { ["url"] = "https://images.example/" + id + ".png", ["width"] = 250, ["height"] = 300 } : null,
        ["voices"] = new JsonArray(new JsonObject { ["id"] = "99", ["person_id"] = "77", ["display_name"] = "同一声优" })
    };
    private static WorkMetadata MapCharacters(params JsonObject[] rows)
    {
        var root = WorkRow("galgame"); root["characters"] = new JsonArray(rows.Cast<JsonNode>().ToArray());
        using var body = JsonDocument.Parse(root.ToJsonString());
        return new RemoteWorkDto(body.RootElement).Map(true, DateTimeOffset.UnixEpoch);
    }
}
