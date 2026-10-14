using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicBrainzDeepeningTests
{
    private const string WorkId = "423cadec-2e5b-4569-9855-6148de881084";
    private static JsonObject AlbumJson() => JsonNode.Parse(MusicTestData.AlbumJson)!.AsObject();
    private static JsonObject FirstRecording(JsonObject album) => album["media"]![0]!["tracks"]![0]!["recording"]!.AsObject();
    private static JsonObject WithWork()
    {
        var album = AlbumJson();
        var work = FirstRecording(album)["relations"]![0]!["work"]!.AsObject();
        work["id"] = WorkId; work["title"] = "汐"; work["type"] = null;
        work["languages"] = new JsonArray("zxx");
        return album;
    }
    private static MusicBrainzAlbum Map(JsonObject album)
    {
        using var data = JsonDocument.Parse(album.ToJsonString());
        return MusicBrainzProvider.MapAlbum(MusicTestData.Release, data.RootElement, MusicTestData.Stamp);
    }
    [Fact] public void RecordingAndWorkKeepDistinctIdentitiesAndCreditProvenance()
    {
        var track = Map(WithWork()).Discs[0].Tracks[0];
        Assert.Equal(MusicTestData.Recording, track.RecordingId);
        var work = Assert.Single(track.Works);
        Assert.Equal(WorkId, work.Id); Assert.NotEqual(track.RecordingId, work.Id);
        Assert.Equal("汐", work.Title); Assert.Null(work.Type); Assert.Equal(new[] { "zxx" }, work.Languages);
        Assert.Equal("composer", Assert.Single(work.Credits).Role);
        Assert.Equal("https://musicbrainz.org/work/" + WorkId, work.Url);
        Assert.Single(track.Credits); // The former aggregate credit contract remains readable.
    }
    [Theory]
    [InlineData(MusicBrainzEntityKind.Work, "work")]
    [InlineData(MusicBrainzEntityKind.Artist, "artist")]
    public void TypedSourceReferencesCannotAutomaticallyBindAnAlbum(MusicBrainzEntityKind kind, string path)
    {
        Assert.True(MusicBrainzReference.TryParse("https://musicbrainz.org/" + path + "/" + WorkId, out var value));
        Assert.Equal(kind, value!.Kind);
        var source = MusicTestData.Source(); source = source with { Facts = source.Facts with { ExternalIds = [value] } };
        Assert.Null(MusicBrainzMatching.Automatic(source, MusicTestData.Search()));
    }
    [Theory]
    [InlineData("http://musicbrainz.org/work/")]
    [InlineData("https://musicbrainz.org.evil/artist/")]
    [InlineData("https://musicbrainz.org/recording/../work/")]
    public void NewSourceKindsRetainTheCanonicalOriginGuard(string prefix)
        => Assert.False(MusicBrainzReference.TryParse(prefix + WorkId, out _));
    [Theory]
    [InlineData("bad-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("423CADEC-2E5B-4569-9855-6148DE881084")]
    public void InvalidWorkIdentityRejectsTheResponse(string id)
    {
        var album = WithWork(); FirstRecording(album)["relations"]![0]!["work"]!["id"] = id;
        Assert.Throws<MusicBrainzException>(() => Map(album));
    }
    [Theory]
    [InlineData("id")][InlineData("title")]
    public void PartiallyClaimedWorkIdentityIsNotFilledFromTheRecording(string missing)
    {
        var album = WithWork(); FirstRecording(album)["relations"]![0]!["work"]!.AsObject().Remove(missing);
        Assert.Throws<MusicBrainzException>(() => Map(album));
    }
    [Fact] public void LegacyCreditOnlyRelationsRemainReadableWithoutInventingAWork()
    {
        var track = Map(AlbumJson()).Discs[0].Tracks[0];
        Assert.Empty(track.Works); Assert.Equal("composer", Assert.Single(track.Credits).Role);
    }
    [Fact] public void RepeatedSameWorkDoesNotDuplicateItsIdentityOrCredits()
    {
        var album = WithWork(); var relations = FirstRecording(album)["relations"]!.AsArray();
        relations.Add(relations[0]!.DeepClone()); var track = Map(album).Discs[0].Tracks[0];
        Assert.Single(track.Works); Assert.Single(track.Credits);
    }
    [Fact] public void ConflictingSnapshotsForTheSameWorkAreRejected()
    {
        var album = WithWork(); var relations = FirstRecording(album)["relations"]!.AsArray();
        var other = relations[0]!.DeepClone(); other["work"]!["title"] = "其他作品"; relations.Add(other);
        Assert.Throws<MusicBrainzException>(() => Map(album));
    }
    [Fact] public void WorkIdentityCountIsBounded()
    {
        var album = WithWork(); var relations = FirstRecording(album)["relations"]!.AsArray();
        for (int i = 1; i <= 16; i++)
        {
            var relation = relations[0]!.DeepClone(); relation["work"]!["id"] = "00000000-0000-0000-0000-" + i.ToString("D12");
            relations.Add(relation);
        }
        Assert.Throws<MusicBrainzException>(() => Map(album));
    }
    [Fact] public void EditionFactsPreserveMediumFormatAndPartialTerritoryDates()
    {
        var album = AlbumJson(); album["disambiguation"] = "Steam edition"; album["packaging"] = "None";
        album["barcode"] = "4933032003335";
        album["text-representation"] = JsonNode.Parse("""{"language":"jpn","script":"Latn"}""");
        album["release-events"] = JsonNode.Parse("""[{"date":"2025-05","area":{"iso-3166-1-codes":["XW"]}},{"date":null,"area":null}]""");
        album["media"]![0]!["format"] = "Digital Media";
        var release = Map(album).Release;
        Assert.Equal("Steam edition", release.Disambiguation); Assert.Equal("None", release.Packaging);
        Assert.Equal("jpn", release.TextLanguage); Assert.Equal("Latn", release.TextScript);
        Assert.Equal(new[] { "Digital Media", "CD" }, release.MediaFormats);
        Assert.Equal(new MusicBrainzReleaseEvent("2025-05", "XW"), release.ReleaseEvents[0]);
        Assert.Equal(new MusicBrainzReleaseEvent(null, null), release.ReleaseEvents[1]);
    }
    [Fact] public void MalformedEditionDatesCannotBeExposedAsValidFacts()
    {
        var album = AlbumJson(); album["release-events"] = JsonNode.Parse("""[{"date":"2025-99","area":null}]""");
        Assert.Throws<MusicBrainzException>(() => Map(album));
    }
    [Fact] public void TrackOrderFollowsPositionRatherThanResponseArrayOrder()
    {
        var album = AlbumJson(); var medium = album["media"]![0]!.AsObject(); var tracks = medium["tracks"]!.AsArray();
        var second = tracks[0]!.DeepClone(); second["position"] = 2; second["number"] = "2"; second["title"] = "后曲";
        tracks.Insert(0, second); medium["track-count"] = 2;
        Assert.Equal(new[] { 1, 2 }, Map(album).Discs[0].Tracks.Select(x => x.Position));
    }
    [Fact] public void LegacyCacheWithoutAddedPropertiesUsesEmptyDefaults()
    {
        var value = new MusicBrainzCacheValue("release:" + MusicTestData.Release, MusicTestData.Stamp, Album: MusicTestData.Album());
        var json = JsonSerializer.SerializeToNode(value)!.AsObject(); var album = json["Album"]!.AsObject();
        var release = album["Release"]!.AsObject();
        foreach (var name in new[] { "Disambiguation", "Packaging", "TextLanguage", "TextScript", "MediaFormats", "ReleaseEvents" }) release.Remove(name);
        foreach (var disc in album["Discs"]!.AsArray()) foreach (var track in disc!["Tracks"]!.AsArray()) track!.AsObject().Remove("Works");
        var restored = JsonSerializer.Deserialize<MusicBrainzCacheValue>(json.ToJsonString(), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })!;
        Assert.True(MusicBrainzValidation.Cache(restored)); Assert.Empty(restored.Album!.Release.ReleaseEvents);
        Assert.Empty(restored.Album.Discs[0].Tracks[0].Works);
    }
}
