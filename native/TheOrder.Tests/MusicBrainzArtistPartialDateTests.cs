using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicBrainzArtistPartialDateTests
{
    private static readonly MusicBrainzReference Artist = new(MusicBrainzEntityKind.Artist, "aabc3d78-218b-4e98-95b7-fe6388adef2d");
    private static MusicBrainzEntityDetail Map(string? begin, string? end = null, string? relationBegin = null)
    {
        var root = JsonNode.Parse("""
            {"id":"aabc3d78-218b-4e98-95b7-fe6388adef2d","name":"Synthetic artist","type":"Person","life-span":{},"relations":[]}
            """)!;
        root["life-span"]!["begin"] = begin; root["life-span"]!["end"] = end;
        if (relationBegin is not null)
        {
            var relation = JsonNode.Parse("""
                {"type":"member of band","direction":"forward","target-type":"artist","artist":{"id":"9782d6e1-9a4e-4013-b60b-3e050b03a067","name":"Synthetic group"}}
                """)!;
            relation["begin"] = relationBegin; root["relations"]!.AsArray().Add(relation);
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        return MusicBrainzProvider.MapEntity(Artist, document.RootElement, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("????-10-21")]
    [InlineData("????-10")]
    [InlineData("2004-??-21")]
    [InlineData("????-??-21")]
    [InlineData("????-02-29")]
    [InlineData("2004")]
    [InlineData("2004-02")]
    [InlineData("2004-02-29")]
    public void LifespanRetainsKnownAndUnknownComponentsWithoutInventingDates(string date)
    {
        var result = Map(date, date);
        Assert.Equal(Artist, result.Reference);
        Assert.Contains(result.Facts, fact => fact.Label == "起始日期" && fact.Value == date);
        Assert.Contains(result.Facts, fact => fact.Label == "结束日期" && fact.Value == date);
        Assert.True(MusicBrainzEntityValidation.Detail(result));
    }

    [Theory]
    [InlineData("????-13-21")]
    [InlineData("????-00-21")]
    [InlineData("????-10-00")]
    [InlineData("????-10-32")]
    [InlineData("????-02-30")]
    [InlineData("????-04-31")]
    [InlineData("2003-02-29")]
    [InlineData("0000-??-21")]
    [InlineData("2004-??")]
    [InlineData("????-??")]
    [InlineData("????")]
    [InlineData("2004-10-??")]
    [InlineData("????-1-21")]
    [InlineData("2???-10-21")]
    [InlineData("????-10-21suffix")]
    [InlineData("????-10-21\n")]
    public void ImpossibleOrNonCanonicalLifespanDatesStillReject(string date)
    {
        Assert.Throws<MusicBrainzException>(() => Map(date));
        Assert.Throws<MusicBrainzException>(() => Map(null, date));
    }

    [Fact]
    public void AbsentLifespanDatesRemainAbsent()
    {
        var result = Map(null);
        Assert.DoesNotContain(result.Facts, fact => fact.Label is "起始日期" or "结束日期");
    }

    [Theory]
    [InlineData("????-10-21")]
    [InlineData("2004-??-21")]
    public void UnknownLifespanComponentsDoNotBecomeReleaseOrRelationDateEvidence(string date)
    {
        Assert.False(MusicBrainzValidation.Date(date));
        Assert.Throws<MusicBrainzException>(() => Map(null, relationBegin: date));
    }
}
