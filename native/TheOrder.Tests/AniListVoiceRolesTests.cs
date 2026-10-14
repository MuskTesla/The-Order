using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class AniListVoiceRolesTests
{
    [Fact] public void ChildVoiceRoleKeepsActorIdentityAndDoesNotReplaceCharacterOrMainActor()
    {
        var row = JsonNode.Parse(AniListTestData.DetailJson)!;
        var edge = row["characters"]!["edges"]![0]!;
        edge["voiceActorRoles"] = JsonNode.Parse("""[{"voiceActor":{"id":12,"name":{"full":"Yuichi Nakamura","native":"中村悠一"}},"roleNotes":null,"dubGroup":null},{"voiceActor":{"id":95935,"name":{"full":"Fuyuka Ooura","native":"大浦冬華"}},"roleNotes":"Child","dubGroup":null}]""");
        using var json = JsonDocument.Parse(row.ToJsonString());
        var value = AniListProvider.MapDetails(json.RootElement, 1, AniListTestData.Stamp);
        var character = Assert.Single(value.Characters);
        Assert.Equal(11, character.Person.Id); Assert.Equal(12, Assert.Single(character.VoiceActors).Id);
        Assert.Equal(2, character.VoiceRoles.Length); Assert.Equal("Child", character.VoiceRoles[1].RoleNotes);
        Assert.Equal(95935, character.VoiceRoles[1].Person.Id); Assert.True(AniListValidation.Details(value));
        Assert.Contains("voiceActorRoles(language:JAPANESE)", AniListProvider.DetailQuery);
    }
    [Fact] public void LegacyCachedCharactersRemainValidWithoutNewVoiceRoles()
    {
        var value = AniListTestData.Details(); Assert.Empty(value.Characters[0].VoiceRoles);
        Assert.True(AniListValidation.Details(JsonSerializer.Deserialize<AniListDetails>(JsonSerializer.Serialize(value))));
    }
    [Fact] public void InvalidActorOrUnboundedNotesCannotEnterProfessionalDetailCache()
    {
        var value = AniListTestData.Details(); var character = value.Characters[0];
        Assert.False(AniListValidation.Details(value with { Characters = [character with { VoiceRoles = [new(new(0, "bad", "", null), "Child", null)] }] }));
        Assert.False(AniListValidation.Details(value with { Characters = [character with { VoiceRoles = [new(character.VoiceActors[0], new string('x', 1001), null)] }] }));
    }
}
