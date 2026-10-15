using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicArtistCreditQualityTests
{
    private const string Artist = "84c2a1ee-f797-4760-a0d2-b20f608911a6";
    private const string OtherArtist = "9782d6e1-9a4e-4013-b60b-3e050b03a067";
    private static JsonArray Credits(string id = Artist) => JsonNode.Parse("""
        [{"name":"Credited stage name","joinphrase":" & ","artist":{"id":"ARTIST","name":"Canonical person"}},
         {"name":"Name without identity","joinphrase":""}]
        """.Replace("ARTIST", id))!.AsArray();
    private static JsonObject AlbumJson()
    {
        var value = JsonNode.Parse(MusicTestData.AlbumJson)!.AsObject();
        value["artist-credit"] = Credits();
        value["media"]![0]!["tracks"]![0]!["artist-credit"] = Credits(OtherArtist);
        return value;
    }
    private static MusicBrainzAlbum Map(JsonObject value)
    {
        using var doc = JsonDocument.Parse(value.ToJsonString());
        return MusicBrainzProvider.MapAlbum(MusicTestData.Release, doc.RootElement, MusicTestData.Stamp);
    }
    private static JsonObject RecordingJson(string artist = Artist) => new()
    {
        ["id"] = MusicTestData.Recording, ["title"] = "Synthetic recording", ["length"] = 1000,
        ["artist-credit"] = Credits(artist), ["relations"] = new JsonArray()
    };
    private static MusicBrainzEntityDetail MapRecording(JsonObject value)
    {
        using var doc = JsonDocument.Parse(value.ToJsonString());
        return MusicBrainzProvider.MapEntity(new(MusicBrainzEntityKind.Recording, MusicTestData.Recording), doc.RootElement, MusicTestData.Stamp);
    }
    private static JsonArray Artists(object value)
    {
        var node = JsonSerializer.SerializeToNode(value)!;
        Assert.NotNull(node["ArtistCredits"]);
        return node["ArtistCredits"]!.AsArray();
    }
    [Fact] public void ReleaseAndTrackKeepSeparateArtistIdsCreditedNamesAndJoinPhrases()
    {
        var album = Map(AlbumJson());
        var release = Artists(album.Release); var track = Artists(album.Discs[0].Tracks[0]);
        Assert.Equal(Artist, release[0]!["ArtistId"]!.GetValue<string>());
        Assert.Equal(OtherArtist, track[0]!["ArtistId"]!.GetValue<string>());
        Assert.Equal("Credited stage name", release[0]!["Name"]!.GetValue<string>());
        Assert.Equal(" & ", release[0]!["JoinPhrase"]!.GetValue<string>());
        Assert.Equal("Name without identity", release[1]!["Name"]!.GetValue<string>());
        Assert.Null(release[1]!["ArtistId"]);
        Assert.True(MusicBrainzValidation.Album(album));
    }
    [Fact] public void RecordingRetainsArtistLinksWithoutInventingRelationships()
    {
        var recording = MapRecording(RecordingJson());
        Assert.Equal(Artist, Artists(recording)[0]!["ArtistId"]!.GetValue<string>());
        Assert.Empty(recording.Relations);
        Assert.Contains(recording.Facts, f => f.Label == "艺术家");
        Assert.True(MusicBrainzEntityValidation.Detail(recording));
    }
    [Theory]
    [InlineData("bad-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("84C2A1EE-F797-4760-A0D2-B20F608911A6")]
    public void SuppliedInvalidArtistIdentityIsRejectedInReleaseTrackAndRecording(string id)
    {
        var release = AlbumJson(); release["artist-credit"]![0]!["artist"]!["id"] = id;
        Assert.Throws<MusicBrainzException>(() => Map(release));
        var track = AlbumJson(); track["media"]![0]!["tracks"]![0]!["artist-credit"]![0]!["artist"]!["id"] = id;
        Assert.Throws<MusicBrainzException>(() => Map(track));
        Assert.Throws<MusicBrainzException>(() => MapRecording(RecordingJson(id)));
    }
    [Fact] public void MissingArtistIdentityNeverBorrowsRecordingOrReleaseId()
    {
        var raw = RecordingJson(); raw["artist-credit"]![0]!["artist"]!.AsObject().Remove("id");
        var credits = Artists(MapRecording(raw));
        Assert.All(credits, credit => Assert.Null(credit!["ArtistId"]));
    }
    [Fact] public void CanonicalArtistNameIsOnlyAFallbackWhenCreditedNameIsAbsent()
    {
        var raw = RecordingJson(); raw["artist-credit"]![0]!.AsObject().Remove("name");
        Assert.Equal("Canonical person", Artists(MapRecording(raw))[0]!["Name"]!.GetValue<string>());
    }
    [Theory] [InlineData("")] [InlineData("   ")]
    public void ExplicitlyEmptyArtistIdIsNotTreatedAsAnUnknownIdentity(string id)
        => Assert.Throws<MusicBrainzException>(() => MapRecording(RecordingJson(id)));
    [Fact] public void OversizedArtistCreditListRejectsRatherThanClaimingCompleteness()
    {
        var raw = RecordingJson(); var artists = raw["artist-credit"]!.AsArray();
        while (artists.Count < 65) artists.Add(artists[0]!.DeepClone());
        Assert.Throws<MusicBrainzException>(() => MapRecording(raw));
    }
    [Fact] public void NullArraysAndForgedCachedArtistIdsFailValidation()
    {
        var album = Map(AlbumJson()); var track = album.Discs[0].Tracks[0]; var detail = MapRecording(RecordingJson());
        Assert.False(MusicBrainzValidation.Candidate(album.Release with { ArtistCredits = null! }));
        Assert.False(MusicBrainzEntityValidation.Detail(detail with { ArtistCredits = null! }));
        var forged = new MusicBrainzArtistCredit("Person", "invalid-id", "");
        Assert.False(MusicBrainzValidation.Candidate(album.Release with { ArtistCredits = [forged] }));
        Assert.False(MusicBrainzEntityValidation.Detail(detail with { ArtistCredits = [forged] }));
        var discs = album.Discs.ToArray();
        discs[0] = discs[0] with { Tracks = [track with { ArtistCredits = null! }] };
        Assert.False(MusicBrainzValidation.Album(album with { Discs = discs }));
    }
    [Fact] public void LegacyAlbumCacheWithoutArtistCreditsRemainsReadable()
    {
        var cache = new MusicBrainzCacheValue("release:" + MusicTestData.Release, MusicTestData.Stamp, Album: Map(AlbumJson()));
        var node = JsonSerializer.SerializeToNode(cache)!;
        node["Album"]!["Release"]!.AsObject().Remove("ArtistCredits");
        foreach (var disc in node["Album"]!["Discs"]!.AsArray())
            foreach (var track in disc!["Tracks"]!.AsArray()) track!.AsObject().Remove("ArtistCredits");
        var restored = JsonSerializer.Deserialize<MusicBrainzCacheValue>(node.ToJsonString(), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })!;
        Assert.True(MusicBrainzValidation.Cache(restored));
        Assert.Empty(Artists(restored.Album!.Release)); Assert.Empty(Artists(restored.Album.Discs[0].Tracks[0]));
    }
    [Fact] public void ArtistCreditsDoNotChangeAutomaticReleaseDecision()
    {
        var source = MusicTestData.Source(); var plain = Map(JsonNode.Parse(MusicTestData.AlbumJson)!.AsObject());
        var enriched = Map(AlbumJson());
        Assert.Equal(MusicBrainzMatching.Assess(source, plain.Release).Strong, MusicBrainzMatching.Assess(source, enriched.Release).Strong);
        Assert.Equal(MusicBrainzMatching.Automatic(source, new([plain.Release], 1, false, null, null)),
            MusicBrainzMatching.Automatic(source, new([enriched.Release], 1, false, null, null)));
    }
    [Fact] public async Task ReturnedArtistArraysCannotMutateFreshOrOfflineEntityCache()
    {
        var clock = new MusicTestData.Clock(); bool offline = false;
        using var handler = new MusicTestData.Handler((_, _) => offline ? throw new HttpRequestException()
            : Task.FromResult(MusicTestData.Response(RecordingJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        var reference = new MusicBrainzReference(MusicBrainzEntityKind.Recording, MusicTestData.Recording);
        var property = typeof(MusicBrainzEntityDetail).GetProperty("ArtistCredits"); Assert.NotNull(property);
        var other = (Array)property.GetValue(MapRecording(RecordingJson(OtherArtist)))!;
        var first = await provider.LoadAsync(reference, false, default);
        ((Array)property.GetValue(first.Detail)!).SetValue(other.GetValue(0), 0);
        var fresh = await provider.LoadAsync(reference, false, default);
        Assert.Equal(Artist, Artists(fresh.Detail)[0]!["ArtistId"]!.GetValue<string>());
        ((Array)property.GetValue(fresh.Detail)!).SetValue(other.GetValue(0), 0);
        clock.Advance(TimeSpan.FromDays(2)); offline = true;
        var stale = await provider.LoadAsync(reference, false, default);
        Assert.True(stale.IsStale); Assert.Equal(Artist, Artists(stale.Detail)[0]!["ArtistId"]!.GetValue<string>());
        ((Array)property.GetValue(stale.Detail)!).SetValue(other.GetValue(0), 0);
        Assert.Equal(Artist, Artists((await provider.LoadAsync(reference, false, default)).Detail)[0]!["ArtistId"]!.GetValue<string>());
    }
}
