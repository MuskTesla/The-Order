using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

public sealed class GalgameQualityTests
{
    private static WorkMetadata MapVoices(string voices, string? secondCharacter = null)
    {
        using var body = JsonDocument.Parse("""
            {"object":"work","id":"86","medium":"galgame","display_name":"Synthetic",
             "characters":[{"id":"101","display_name":"Synthetic character","roster_role":"main",
             "image":{"url":"https://images.example/character.png"},"voices":
            """ + voices + "}" + (secondCharacter is null ? "" : "," + secondCharacter) + "]}");
        return new RemoteWorkDto(body.RootElement).Map(true, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void DifferentCreditNamesWithIdenticalSpellingRetainBothIdentities()
    {
        var work = MapVoices("""[{"id":"201","display_name":"同名声优"},{"id":"202","display_name":"同名声优"}]""");
        Assert.Equal(new[] { "201", "202" }, Assert.Single(work.Presentation!.Characters).Voices.Select(v => v.Id));
    }

    [Fact]
    public void NameOnlyVoiceMatchCannotMergeDifferentSupplementalCharacter()
    {
        var work = MapVoices("""[{"id":"201","display_name":"同名声优"}]""", """
            {"id":"102","display_name":"Synthetic character","roster_role":"unknown",
             "voices":[{"id":"202","display_name":"同名声优"}]}
            """);
        Assert.Equal(new[] { "101", "102" }, work.Presentation!.Characters.Select(c => c.Entity.Id));
    }

    [Fact]
    public void ExactPersonIdentityStillDeduplicatesAlternateCredits()
    {
        var work = MapVoices("""[{"id":"201","person_id":"301","display_name":"First name"},{"id":"202","person_id":"301","display_name":"Second name"}]""");
        Assert.Equal("201", Assert.Single(Assert.Single(work.Presentation!.Characters).Voices).Id);
    }

    [Fact]
    public void ExactCreditIdentityStillDeduplicatesWhenOnePersonIdIsMissing()
    {
        var work = MapVoices("""[{"id":"201","person_id":"301","display_name":"First name"},{"id":"201","display_name":"First name"}]""");
        Assert.Single(Assert.Single(work.Presentation!.Characters).Voices);
    }

    private static async Task<CatalogEntityDetail> Entity(string json, CatalogEntityKind kind = CatalogEntityKind.CreditName)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        return await new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")))
            .EntityAsync(kind, "68", default);
    }

    [Fact]
    public async Task CreditSourcesKeepPersonResourceAndExplicitVndbIdentity()
    {
        var detail = await Entity("""
            {"object":"credit_name","id":"68","display_name":"麻枝准",
             "refs":[{"source":"bangumi","external_id":"1916"},{"source":"vndb","external_id":"386"}],
             "links":[{"source":"bangumi","url":"https://bgm.tv/person/1916"},{"source":"vndb","url":"https://vndb.org/s224"}]}
            """);
        Assert.Contains(detail.References, r => r.Url == "https://bgm.tv/person/1916");
        Assert.Contains(detail.References, r => r.Url == "https://vndb.org/s224");
        Assert.DoesNotContain(detail.References, r => r.Url is not null && (r.Url.Contains("/subject/") || r.Url.EndsWith("s386")));
    }

    [Fact]
    public async Task CharacterSourcePreservesVndbCharacterResource()
    {
        var detail = await Entity("""
            {"object":"character","id":"68","display_name":"Synthetic","refs":[{"source":"vndb","external_id":"c190"}]}
            """, CatalogEntityKind.Character);
        Assert.Equal("https://vndb.org/c190", Assert.Single(detail.References).Url);
    }

    [Theory]
    [InlineData("https://bgm.tv/subject/1916")]
    [InlineData("https://bgm.tv.evil.example/person/1916")]
    [InlineData("https://user@bgm.tv/person/1916")]
    [InlineData("https://bgm.tv:8443/person/1916")]
    [InlineData("https://vndb.org/v224")]
    public async Task CreditSourceRejectsWrongResourceOrUntrustedDestination(string url)
    {
        var detail = await Entity(JsonSerializer.Serialize(new { @object = "credit_name", id = "68", display_name = "Synthetic", links = new[] { new { source = "bangumi", url } } }));
        Assert.DoesNotContain(detail.References, r => r.Url is not null);
    }

    [Fact]
    public async Task CharacterFactsPreserveProvidedBirthdayAndMeasurements()
    {
        var detail = await Entity("""
            {"object":"character","id":"68","display_name":"Synthetic","birthday":"05-13",
             "measurements":{"bust_cm":88,"waist_cm":58,"hip_cm":85,"cup":null},
             "aliases":[{"lang":"ja","value":"Ichinose Kotomi","is_machine":false}]}
            """, CatalogEntityKind.Character);
        Assert.Contains(detail.Facts, f => f.Contains("05-13"));
        Assert.Contains(detail.Facts, f => f.Contains("88") && f.Contains("58") && f.Contains("85"));
        Assert.Contains(detail.Facts, f => f.Contains("Ichinose Kotomi"));
    }

    [Fact]
    public async Task MalformedCharacterFactsAreNotMadeIntoValidDatesOrMeasurements()
    {
        var detail = await Entity("""
            {"object":"character","id":"68","display_name":"Synthetic","birthday":"99-99",
             "measurements":{"bust_cm":-1,"waist_cm":"58","hip_cm":1000000},
             "aliases":[{"lang":"ja","value":"","is_machine":false}]}
            """, CatalogEntityKind.Character);
        Assert.DoesNotContain(detail.Facts, f => f.Contains("99-99") || f.Contains("1000000") || f.Contains("-1"));
    }
}
