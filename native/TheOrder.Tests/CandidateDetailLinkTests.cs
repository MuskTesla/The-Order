using System.Collections.Immutable;
using TheOrder.Domain;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class CandidateDetailLinkTests
{
    [Fact]
    public void LinksUseActualWorkUrlsAndDeduplicateSources()
    {
        var work = Work("123") with { WebUrl = "https://www.kungal.com/galgame/123", References = [new("vndb", "v1", "https://vndb.org/v1"),
            new("bangumi", "123", "https://bgm.tv/subject/123"), new("vndb", "v1", "https://vndb.org/v1")] };
        var links = CandidateDetailLinks.From(work);
        Assert.Equal(3, links.Count); Assert.Equal("KunGal", links[0].Title); Assert.Equal(work.WebUrl, links[0].Url);
        Assert.Contains(links, l => l.Title == "VNDB"); Assert.Contains(links, l => l.Title == "Bangumi");
    }
    [Theory]
    [InlineData("http://vndb.org/v1")][InlineData("file:///C:/private")][InlineData("javascript:alert(1)")]
    [InlineData("https://secret@vndb.org/v1")][InlineData("https://vndb.org:444/v1")]
    [InlineData("https://vndb.org.attacker.invalid/v1")][InlineData("https://127.0.0.1/")]
    public void UnsafeOrUnrelatedTargetsAreNotLaunchable(string value) => Assert.False(CandidateDetailLinks.Allowed(value));
    [Fact]
    public void MissingForeignLinksKeepOnlyCatalogKunGalRoute() { var link = Assert.Single(CandidateDetailLinks.From(Work())); Assert.Equal("KunGal", link.Title); Assert.Equal("https://www.kungal.com/galgame/" + Work().Id, link.Url); }
}
