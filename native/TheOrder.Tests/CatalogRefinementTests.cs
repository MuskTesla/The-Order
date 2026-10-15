using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

public sealed class CatalogRefinementTests
{
    private static WorkMetadata Map(string fields)
    {
        using var doc = JsonDocument.Parse("{\"object\":\"work\",\"id\":\"7001\",\"display_name\":\"Synthetic remake\"," + fields + "}");
        return new RemoteWorkDto(doc.RootElement).Map(true, DateTimeOffset.UnixEpoch);
    }
    private static CatalogPresentation Display(params CatalogEntityRef[] relations) => new("curated", [], [], [], [], [], [], [], [..relations]);
    private static CatalogEntityRef Relation(string id, string type) => new(CatalogEntityKind.Work, id, "Unrelated synthetic name", Relation: type);

    [Fact]
    public void UnclaimedCatalogWorkStillHasKunGalRouteWithoutInventedIdentityEvidence()
    {
        var work = Map("\"claim\":null,\"refs\":[{\"source\":\"bangumi\",\"external_id\":\"42\"}]");
        Assert.Equal("https://www.kungal.com/galgame/7001", work.WebUrl);
        Assert.Equal("KunGal", CandidateDetailLinks.From(work)[0].Title);
        Assert.Single(work.References);
        Assert.DoesNotContain(work.References, r => r.Source is "curated" or "kungal");
    }

    [Fact]
    public void CanonicalVndbWorkLinkReplacesReleaseClutterWithoutDeletingUnderlyingAnchors()
    {
        var refs = Enumerable.Range(1, 12).Select(n => new WorkReference("vndb", "r" + n, "https://vndb.org/r" + n)).ToImmutableArray();
        var work = Work() with { References = refs.Add(new("vndb", "v501", "https://vndb.org/v501")).Add(new("vndb", "v501", "https://vndb.org/v501")) };
        var links = CandidateDetailLinks.From(work);
        Assert.Single(links, l => l.Title == "VNDB");
        Assert.Equal("https://vndb.org/v501", links.Single(l => l.Title == "VNDB").Url);
        Assert.Equal(14, work.References.Length);
        Assert.DoesNotContain(links, l => l.Url.Contains("/r"));
        Assert.DoesNotContain(CandidateDetailLinks.From(work with { References = refs }), l => l.Title == "VNDB");
    }

    [Theory]
    [InlineData("remake_of")][InlineData("remaster_of")][InlineData("port_of")][InlineData("alternative_version")]
    public void ExplicitUniqueVersionReferenceWorksForArbitraryNamesAndIds(string type)
    {
        foreach (var id in new[] { "1", "907", "12345678901234567890" })
        {
            var current = Work("333", "Invented Edition Z") with { Presentation = Display(Relation(id, type)) };
            Assert.Equal(id, CatalogReferences.Preferred(current)?.Id);
            Assert.Equal("333", current.Id);
        }
    }

    [Theory]
    [InlineData("side_story_of")][InlineData("fandisc_of")][InlineData("sequel_of")][InlineData("same_series")]
    [InlineData("alternative_setting")][InlineData("shares_character")][InlineData("unknown")]
    public void SharedSeriesOrCharactersNeverAuthorizeAutomaticVersionReference(string type)
    {
        var current = Work("333", "Title Remake HD") with { Presentation = Display(Relation("90", type)) };
        Assert.Null(CatalogReferences.Preferred(current));
    }

    [Fact]
    public void MultipleExplicitParentsDoNotUsePopularityIdOrTitleToGuessOne()
    {
        var current = Work("333") with { Presentation = Display(Relation("90", "remake_of"), Relation("1", "remake_of"), Relation("88", "alternative_version")) };
        Assert.Null(CatalogReferences.Preferred(current));
        Assert.Null(CatalogReferences.Preferred(current with { Presentation = Display(Relation("90", "alternative_version"), Relation("1", "alternative_version")) }));
        Assert.Null(CatalogReferences.Preferred(current with { Presentation = Display(Relation("333", "remake_of")) }));
        Assert.Null(CatalogReferences.Preferred(current with { Presentation = Display(new CatalogEntityRef(CatalogEntityKind.Company,"42","Not a work",Relation:"remake_of")) }));
    }

    [Fact]
    public void DirectedParentOutranksAlternativeButDoesNotRewriteOrPersistReferenceFacts()
    {
        var current = Work("333", "Current") with { ReleaseDate = "2026-01-01", Ratings = [new("bangumi", 6.5, 10, 4)], Presentation = Display(Relation("90", "remake_of"), Relation("1", "alternative_version")) };
        var entry = Entry("Personal label") with { Metadata = current, Notes = "**personal**", PersonalRating = 9, CustomTags = ["own"], TotalPlaySeconds = 123 };
        var codec = new NativeLibraryCodec(); var before = codec.Encode([entry], Clock);
        Assert.Equal("90", CatalogReferences.Preferred(current)?.Id);
        Assert.Equal(before, codec.Encode([entry], Clock));
        var restored = Assert.Single(codec.Decode(before));
        Assert.Null(restored.Metadata!.Presentation);
        Assert.Equal("333", restored.Metadata.Id);
        Assert.Equal(current.Ratings.ToArray(), restored.Metadata.Ratings.ToArray());
        Assert.Equal("2026-01-01", restored.Metadata.ReleaseDate);
        Assert.Equal(entry.Notes, restored.Notes);
        Assert.Equal(entry.PersonalRating, restored.PersonalRating);
        Assert.Equal(entry.TotalPlaySeconds, restored.TotalPlaySeconds);
    }

    [Fact]
    public void RicherRelatedVersionHasSeparateScoresAndOwnCommunityRatingScale()
    {
        var work = Map("\"intros\":[{\"source\":\"bangumi\",\"lang\":\"zh-Hans\",\"value\":\"Other intro\"},{\"source\":\"curated\",\"lang\":\"zh-Hans\",\"value\":\"Kun intro\",\"is_machine\":false}],\"ratings\":[{\"source\":\"bangumi\",\"score\":7,\"vote_count\":100},{\"source\":\"nextmoe\",\"score\":8,\"vote_count\":18},{\"source\":\"howlongtobeat\",\"score\":83,\"vote_count\":4},{\"source\":\"unknown\",\"score\":8}]");
        Assert.Equal("Kun intro", work.Description);
        Assert.Equal("curated", work.Presentation!.IntroSource);
        Assert.Equal(new SourceRating("nextmoe", 8, 10, 18), work.Ratings[0]);
        Assert.Equal(100, work.Ratings.Single(r=>r.Source=="howlongtobeat").Scale);
        Assert.DoesNotContain(work.Ratings, r=>r.Source=="unknown");
        Assert.DoesNotContain(work.References, r=>r.Source=="curated");
    }

    private static JsonObject Character(string id, string name, string? person, string? credit, bool image, string spoiler = "none") =>
        new() { ["id"]=id, ["display_name"]=name, ["roster_role"]=image?"secondary":"unknown", ["spoiler"]=spoiler,
            ["image"]=image?new JsonObject {["url"]="https://example.org/"+id+".png",["width"]=250,["height"]=300}:null,
            ["voices"]=new JsonArray(new JsonObject { ["id"]=credit, ["person_id"]=person, ["display_name"]="Synthetic voice" }) };

    private static WorkMetadata Characters(params JsonObject[] rows) => Map("\"characters\":" + new JsonArray(rows.Cast<JsonNode>().ToArray()).ToJsonString());

    [Fact]
    public void DuplicateRosterUsesIllustratedRecordAndCvWithoutLosingSpoilerProtection()
    {
        var work=Characters(Character("81","Person A","77","99",true,"major"),Character("82","Person A","77","98",false));
        var character=Assert.Single(work.Presentation!.Characters);
        Assert.Equal("81",character.Entity.Id); Assert.NotNull(character.Entity.ImageUrl);
        Assert.Equal("major",character.Spoiler); Assert.Single(character.Voices);
        Assert.Empty(work.Characters);
    }
    [Fact]
    public void CreditIdCanDeduplicateButActorOrNameAloneCannotMergeDifferentCharacters()
    {
        var work=Characters(Character("81","Person A",null,"99",true),Character("82","Person A",null,"99",false),Character("83","Person B",null,"99",true),Character("84","Person A",null,"100",true));
        Assert.Equal(3,work.Presentation!.Characters.Length);
        Assert.Equal(3,work.Characters.Length);
    }
    [Fact]
    public void MissingVoiceIdsDoNotBecomeFakeSharedIdentity()
    {
        var work=Characters(Character("81","Person A",null,null,true),Character("82","Person A",null,null,false));
        Assert.Equal(2,work.Presentation!.Characters.Length);
    }

    [Fact]
    public void SameCharacterIdCanBridgeDuplicateGroupsWhileUnlinkedVoiceNamesRemainReadable()
    {
        var work=Characters(Character("81","Person A","77","99",true),Character("82","Person A","78","98",false),Character("81","Person A","78","97",false));
        Assert.Single(work.Presentation!.Characters);
        var unlinked=Characters(Character("91","Person B",null,null,true));
        Assert.Equal("Synthetic voice",Assert.Single(Assert.Single(unlinked.Characters).VoiceActors));
        Assert.Equal("Synthetic voice",Assert.Single(Assert.Single(unlinked.Presentation!.Characters).Voices).Name);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DifferentCreditIdsCannotMergeIncompleteRosterByCharacterAndActorSpellings(bool reverse)
    {
        var full=Character("81","虚构 秀樹",null,"99",true,"major");
        full["localized"]=new JsonObject {["zh-Hans"]=new JsonObject {["value"]="虚构秀树"}};
        full["voices"]![0]!["display_name"]="测试 声优";
        var supplement=Character("82","虚构秀树",null,"100",false);supplement["voices"]![0]!["display_name"]="测试声优";
        var work=reverse?Characters(supplement,full):Characters(full,supplement);
        Assert.Equal(new[]{"81","82"},work.Presentation!.Characters.Select(c=>c.Entity.Id).Order());
        var illustrated=work.Presentation.Characters.Single(c=>c.Entity.Id=="81");
        Assert.NotNull(illustrated.Entity.ImageUrl);Assert.Equal("major",illustrated.Spoiler);
        Assert.Equal("99",Assert.Single(illustrated.Voices).Id);
        Assert.Equal("100",Assert.Single(work.Presentation.Characters.Single(c=>c.Entity.Id=="82").Voices).Id);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ExplicitSharedPersonCanMergeIncompleteRosterAcrossDifferentCreditIds(bool reverse)
    {
        var full=Character("81","虚构 秀樹","77","99",true,"major");
        full["localized"]=new JsonObject {["zh-Hans"]=new JsonObject {["value"]="虚构秀树"}};
        full["voices"]![0]!["display_name"]="第一个署名";
        var supplement=Character("82","虚构秀树","77","100",false);
        supplement["voices"]![0]!["display_name"]="不同署名";
        var work=reverse?Characters(supplement,full):Characters(full,supplement);
        var character=Assert.Single(work.Presentation!.Characters);
        Assert.Equal("81",character.Entity.Id);Assert.Equal("major",character.Spoiler);
        Assert.Equal("99",Assert.Single(character.Voices).Id);
    }
    [Fact]
    public void AmbiguousCompleteRostersAndConflictingActorPersonsCannotMergeByName()
    {
        Assert.Equal(3,Characters(Character("81","Person A",null,"99",true),Character("82","Person A",null,"100",true),Character("83","Person A",null,"101",false)).Presentation!.Characters.Length);
        Assert.Equal(2,Characters(Character("81","Person A","71","99",true),Character("82","Person A","72","100",false)).Presentation!.Characters.Length);
        var unnamed=Character("83","????",null,"101",false);Assert.Equal(2,Characters(Character("81","????",null,"99",true),unnamed).Presentation!.Characters.Length);
    }
    [Theory]
    [InlineData("/galgame/42")][InlineData("/galgame/character/42")][InlineData("/galgame/staff/42")]
    public void CatalogMarkdownRelativeRoutesRemainInNativeNavigation(string value)
    {
        Assert.Equal("https://www.kungal.com"+value,MarkdownContent.ResolveLink(value));
    }
    [Theory]
    [InlineData("//attacker.invalid/a")][InlineData("/private/file")][InlineData("/galgame/unknown/4")][InlineData("javascript:alert(1)")]
    public void UnknownRelativeMarkdownLinksAreNotActivated(string value)=>Assert.Null(MarkdownContent.ResolveLink(value));
    [Theory]
    [InlineData("character",CatalogEntityKind.Character)][InlineData("official",CatalogEntityKind.Company)]
    [InlineData("staff",CatalogEntityKind.CreditName)][InlineData("tag",CatalogEntityKind.Tag)]
    [InlineData("engine",CatalogEntityKind.Engine)][InlineData("series",CatalogEntityKind.Series)][InlineData("trait",CatalogEntityKind.Trait)]
    public void VerifiedKunGalCatalogLinksResolveToNativeEntity(string route,CatalogEntityKind expected)
    {
        Assert.True(KunGalNavigation.TryEntity("https://www.kungal.com/galgame/"+route+"/99",out var entity));
        Assert.Equal(expected,entity.Kind); Assert.Equal("99",entity.Id);
    }
    [Theory]
    [InlineData("https://kungal.com.attacker.invalid/galgame/1")]
    [InlineData("https://secret@www.kungal.com/galgame/1")]
    [InlineData("https://www.kungal.com:444/galgame/1")]
    [InlineData("https://www.kungal.com/galgame/character/../../1")]
    [InlineData("file:///galgame/1")]
    [InlineData("https://www.kungal.com/galgame/unknown/1")]
    public void UntrustedOrUnknownRoutesCannotBecomeNativeCatalogOperations(string url) => Assert.False(KunGalNavigation.TryEntity(url,out _));

    [Fact]
    public void MarkdownParsesFormattingTablesTasksAndUnicodeWithoutExecutingHtml()
    {
        var doc=MarkdownContent.Parse("# 中文日文\n\n**bold** *emphasis* ~~strike~~ [link](https://www.kungal.com/galgame/42)\n\n- [x] done\n- [ ] todo\n\n| A | B |\n|---|---|\n| 一 | 二 |\n\n<script>alert(1)</script>");
        Assert.Contains(doc,b=>b is HeadingBlock);
        Assert.Contains(doc,b=>b is Table);
        Assert.Contains(doc,b=>b is ListBlock);
        Assert.DoesNotContain(doc,b=>b is HtmlBlock);
        var paragraph=doc.OfType<ParagraphBlock>().First(); Assert.Contains(paragraph.Inline!,i=>i is EmphasisInline);
        Assert.Equal("中文日文",doc.OfType<HeadingBlock>().Single().Inline!.OfType<LiteralInline>().Single().Content.ToString());
    }
    [Theory]
    [InlineData("javascript:alert(1)")][InlineData("file:///C:/private")]
    [InlineData("https://secret@example.org/")][InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")][InlineData("https://example.org:444/")]
    public void MarkdownCannotLaunchLocalFilesOrCredentialBearingLinks(string value)=>Assert.False(MarkdownContent.AllowedLink(value));

    [Fact]
    public void TagMembershipUsesFullLibraryIdentityIncludingWishlistsAndMultipleInstallations()
    {
        var installed = Entry("Personal renamed title", @"C:\synthetic\one") with { Metadata = Work("91"), Status = "completed", Categories = ["Hidden by current filter"] };
        var wishlist = Entry("Wishlist") with { Id = "b", Metadata = Work("92"), Status = "wishlist" };
        var secondInstall = installed with { Id = "c", Installations = [new("other", @"C:\synthetic\two", null, [])] };
        var membership = new CatalogShelfMembership(new([installed, wishlist, secondInstall, Entry("Unknown local game") with { Id = "d" }], LibraryRecovery.Main));
        Assert.True(membership.Contains(Work("91", "Different display title")));
        Assert.True(membership.Contains(Work("92")));
        Assert.False(membership.Contains(Work("93", "Personal renamed title")));
    }

    [Fact]
    public void TagMembershipDoesNotMergeRemakesParentAliasesOrSharedSourceReferences()
    {
        var original = Work("101", "Same display name") with { References = [new("vndb", "v100", null)] };
        var remake = original with { Id = "102", Presentation = Display(Relation("101", "remake_of")) };
        var membership = new CatalogShelfMembership(new([Entry() with { Metadata = original }], LibraryRecovery.Main));
        Assert.True(membership.Contains(original));
        Assert.False(membership.Contains(remake));
    }

    [Fact]
    public void TagMembershipSnapshotStaysConsistentAcrossPagesAndNewNavigationRechecksLibrary()
    {
        var first = new LibrarySnapshot([Entry() with { Metadata = Work("91") }], LibraryRecovery.Main);
        var before = new CatalogShelfMembership(first);
        var after = new CatalogShelfMembership(first with { Entries = first.Entries.Add(Entry() with { Id = "b", Metadata = Work("92") }) });
        Assert.True(before.Contains(Work("91"))); Assert.False(before.Contains(Work("92")));
        Assert.True(after.Contains(Work("91"))); Assert.True(after.Contains(Work("92")));
    }

    [Fact]
    public void TagMembershipAllowsValidReadOnlyLibraryButDoesNotTreatLoadFailureAsEmptyShelf()
    {
        var snapshot = new LibrarySnapshot([Entry() with { Metadata = Work("91") }], LibraryRecovery.Backup, LibraryAccess.ValidReadOnly);
        Assert.True(new CatalogShelfMembership(snapshot).Contains(Work("91")));
        Assert.Throws<InvalidOperationException>(() => new CatalogShelfMembership(snapshot with { Access = LibraryAccess.InvalidInput }));
        Assert.False(new CatalogShelfMembership(new([], LibraryRecovery.New)).Contains(Work("91")));
    }
}
