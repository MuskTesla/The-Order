using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Recognition;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionV4Tests
{
    private static WorkMetadata W(string id, string title, params string[] refs) => Work(id, title) with {
        TitleEvidence = [new(title, "official", false, "ja")],
        References = [.. refs.Select(r => new WorkReference(r.Split(':')[0], r.Split(':')[1], null))] };
    private static RecognitionEvidence E(string title) => Evidence(new LocalTitleEvidence(title, "folder"));
    private static ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, params WorkMetadata[] works) => new ReleaseAwareRanking().Rank(evidence, works);
    private static CatalogWorkIdentity Facts(string company = "", params string[] sites) => new(company.Length == 0 ? [] : [company], [.. sites]);
    private static RecognitionEvidence ProgramEvidence() => E("SOL") with {
        Titles = [new("SOL", "folder"), new("SOL", "product-name")], Companies = ["North Star, Inc."],
        IndexProofs = [new("SOL", "folder", ["SOL"], ["vndb:v9"])] };
    private static Dictionary<string, ImmutableArray<ReleaseIdentity>> Release() => new() { ["200"] = [new("3", "200", "SOL", "SOL", "digital", null)] };
    private static WorkMetadata ProgramWork() => W("200", "SOL", "vndb:v9") with { CatalogIdentity = Facts("North Star") };

    [Fact]
    public void ShortBinaryProductWithPublisherIndexAndReleaseCanBeUnique()
    {
        var ranked = new ReleaseAwareRanking().Rank(ProgramEvidence(), [ProgramWork()], Release());
        Assert.True(ranked[0].HighConfidence); Assert.Equal(2, ranked[0].EvidenceLevel);
        Assert.Contains("发行公司", ranked[0].Reason);
    }

    [Theory]
    [InlineData("company")][InlineData("program")][InlineData("index")][InlineData("release")][InlineData("folder")]
    public void ShortTitlePromotionRequiresEveryIndependentPart(string missing)
    {
        var e = ProgramEvidence(); var rows = Release();
        if (missing == "company") e = e with { Companies = ["Other Studio"] };
        if (missing == "program") e = e with { Titles = [new("SOL", "folder")] };
        if (missing == "index") e = e with { IndexProofs = [] };
        if (missing == "release") rows.Clear();
        if (missing == "folder") e = e with { Titles = [new("SOL", "product-name")] };
        Assert.False(new ReleaseAwareRanking().Rank(e, [ProgramWork()], rows)[0].HighConfidence);
    }

    [Fact]
    public void ProgramPublisherDoesNotOverridePhysicalConflictOrKnownHomonym()
    {
        var e = ProgramEvidence() with { References = [new("vndb:v10", "config")] };
        Assert.True(new ReleaseAwareRanking().Rank(e, [ProgramWork()], Release())[0].Vetoed);
        e = ProgramEvidence() with { Homonyms = [new("SOL", "folder", ["vndb:v9", "vndb:v10"])] };
        Assert.False(new ReleaseAwareRanking().Rank(e, [ProgramWork()], Release())[0].HighConfidence);
    }

    [Fact]
    public void SpecificPrimaryEditionWinsOverInconsistentParentOriginalTitle()
    {
        var parent = W("200", "星空の航路", "bangumi:10") with {
            OriginalTitle = "Hoshi no Kouro REFLECTION BLUE", TitleEvidence = [new("星空の航路", "official", false, "zh-Hans"), new("Hoshi no Kouro REFLECTION BLUE", "official", false, "en")] };
        var edition = W("201", "星空の航路 REFLECTION BLUE", "bangumi:11") with {
            OriginalTitle = "Hoshi no Kouro REFLECTION BLUE", TitleEvidence = [new("星空の航路 REFLECTION BLUE", "official", false, "zh-Hans"), new("Hoshi no Kouro REFLECTION BLUE", "official", false, "en")] };
        var ranked = Rank(E("Hoshi no Kouro REFLECTION BLUE"), parent, edition);
        Assert.Equal("201", ranked[0].Work.Id); Assert.True(ranked[0].HighConfidence);
        Assert.True(ranked.Single(c => c.Work.Id == "200").VersionNeedsConfirmation);
    }

    [Fact]
    public void SameOfficialProductSiteCanCorroborateCrossSourceRecords()
    {
        var a = W("200", "星空の航路", "vndb:v9") with { CatalogIdentity = Facts(sites: ["https://studio.example/games/hoshi"] ) };
        var b = W("201", "星空の航路", "bangumi:10") with { CatalogIdentity = Facts(sites: ["https://studio.example/games/hoshi/index.html"] ) };
        var e = E("星空の航路") with { IndexProofs = [new("星空の航路", "folder", ["星空の航路"], ["vndb:v9"])] };
        Assert.Equal("200", Rank(e, a, b)[0].Work.Id); Assert.True(Rank(e, a, b)[0].HighConfidence);
        Assert.False(Rank(e, a with { References = a.References.Add(new("bangumi", "11", null)) }, b)[0].HighConfidence);
        Assert.False(Rank(e, a with { CatalogIdentity = Facts(sites: ["https://studio.example/"]) }, b with { CatalogIdentity = Facts(sites: ["https://studio.example/index.html"]) })[0].HighConfidence);
    }

    [Fact]
    public void LongExplicitEditionWithDisjointSourceRecordsUsesCoverageNotScoreOrId()
    {
        const string title = "Hoshi no Kouro RE:BOOT";
        var full = W("800", title, "bangumi:10") with { CatalogIdentity = Facts(sites: ["https://studio.example/hoshi/reboot"]) };
        var sparse = W("100", title, "erogamescape:11");
        foreach (var works in new[] { new[] { full, sparse }, new[] { sparse, full } })
        {
            var ranked = Rank(E(title), works); Assert.Equal("800", ranked[0].Work.Id); Assert.True(ranked[0].HighConfidence);
        }
        // The inference cannot absorb an actual identifier conflict or a different chapter.
        Assert.False(Rank(E(title), full, sparse with { References = [new("bangumi", "11", null)] })[0].HighConfidence);
        var chapter = W("101", "Hoshi no Kouro RE:BOOT 第2章", "erogamescape:12");
        Assert.True(Rank(E(title + " 第1章"), full with { Title = title + " 第1章", OriginalTitle = title + " 第1章" }, chapter).Single(c => c.Work.Id == "101").Vetoed);
        Assert.False(Rank(E(title) with { Homonyms = [new(title, "folder", ["bangumi:10", "vndb:v12"])] }, full, sparse)[0].HighConfidence);
    }

    [Fact]
    public void PlainTitleAndTwoSourcesStillDoNotProveDuplicateIdentity()
    {
        Assert.False(Rank(E("Hoshi no Kouro"), W("200", "Hoshi no Kouro", "bangumi:10"), W("201", "Hoshi no Kouro", "erogamescape:11"))[0].HighConfidence);
    }

    [Fact]
    public void PublisherAndOfficialSiteAreMappedWithoutChangingDeveloperFieldsOrLibrarySchema()
    {
        using var document = JsonDocument.Parse("""{"object":"work","id":"200","display_name":"SOL","companies":[{"display_name":"Maker","attribution_role":"developer"},{"display_name":"North Star","attribution_role":"publisher"},{"display_name":"Unrelated Voice","attribution_role":"voice"}],"links":[{"source":"official_site","url":"https://studio.example/sol"},{"source":"official_site","url":"https://user@studio.example/sol"},{"source":"official_site","url":"http://studio.example/sol"},{"source":"other","url":"https://studio.example/other"}]}""");
        var work = new RemoteWorkDto(document.RootElement).Map(true, DateTimeOffset.UnixEpoch);
        Assert.Equal(new[] { "Maker" }, work.Developers); Assert.Equal(new[] { "Maker", "North Star" }, work.CatalogIdentity!.Companies);
        Assert.Equal(new[] { "https://studio.example/sol" }, work.CatalogIdentity.OfficialSites);
        var codec = new LegacyLibraryCodec(); Assert.Null(codec.NormalizeWork(work).CatalogIdentity);
        Assert.DoesNotContain("catalogIdentity", Encoding.UTF8.GetString(codec.Encode([Entry() with { Metadata = work }], Clock)), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedReader(RecognitionEvidence evidence) : IRecognitionEvidenceReader
    {
        public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation) =>
            Task.FromResult(new LocalEvidenceSnapshot(evidence, [], [], new string('a', 64)) { IncludePe = includePe });
    }

    [Fact]
    public async Task RefreshedPublisherMismatchStopsAutomaticCommitAndFactsAreNotDurableAuthority()
    {
        var index = new TitleIndex(() => new MemoryStream(Gzip("""{"version":1,"records":[{"names":["SOL"],"references":["vndb:v9"]}]}""")));
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry("SOL", @"C:\synthetic\SOL"), reader: new FixedReader(ProgramEvidence()), titleIndex: index);
        lab.Catalog.Works = [ProgramWork()]; lab.Catalog.ReleaseRows = Release()["200"];
        await lab.Init(); var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); await lab.Reviews.FlushAsync();
        Assert.Equal("200", AutomaticRecognitionPolicy.Select(lab.Queue.RequireCurrentResult(id))?.Work.Id);
        Assert.DoesNotContain("catalogIdentity", Encoding.UTF8.GetString(lab.Store.Bytes!), StringComparison.OrdinalIgnoreCase);
        lab.Catalog.Detail = (work, _) => Task.FromResult(ProgramWork() with { CatalogIdentity = Facts("Other Studio") });
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }

    [Fact]
    public async Task RefreshedProductSiteCannotEraseCompetitionAtCommit()
    {
        var e = E("星空の航路") with { IndexProofs = [new("星空の航路", "folder", ["星空の航路"], ["vndb:v9"])] };
        var index = new TitleIndex(() => new MemoryStream(Gzip("""{"version":1,"records":[{"names":["星空の航路"],"references":["vndb:v9"]}]}""")));
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry(folder: @"C:\synthetic\hoshi"), reader: new FixedReader(e), titleIndex: index);
        lab.Catalog.Works = [W("200", "星空の航路", "vndb:v9") with { CatalogIdentity = Facts(sites: ["https://studio.example/hoshi"]) },
            W("201", "星空の航路", "bangumi:10") with { CatalogIdentity = Facts(sites: ["https://studio.example/hoshi/index.html"]) }];
        await lab.Init(); var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("200", AutomaticRecognitionPolicy.Select(lab.Queue.RequireCurrentResult(id))?.Work.Id);
        lab.Catalog.Detail = (work, _) => Task.FromResult(lab.Catalog.Works[0] with { CatalogIdentity = Facts(sites: ["https://studio.example/other"]) });
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }

    [Fact]
    public async Task IncompleteReferencedCardReceivesDetailWithoutIncreasingRequestBudget()
    {
        const string title = "Hoshi no Kouro";
        var index = new TitleIndex(() => new MemoryStream(Gzip("""{"version":1,"records":[{"names":["Hoshi no Kouro"],"references":["vndb:v9"]}]}""")));
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry(title, @"C:\synthetic\hoshi"), reader: new FixedReader(E(title)), titleIndex: index);
        // A localized card with no aliases/official title rows would rank behind unrelated
        // partial-title cards. Its fetched original title proves the actual match.
        lab.Catalog.Works = [W("200", "星空の航路", "vndb:v9") with { TitleEvidence = [] }, W("201", "Hoshi no Kouro Interlude"), W("202", "Hoshi no Kouro Prelude")];
        lab.Catalog.Detail = (id, _) => Task.FromResult(id == "200" ? W("200", title, "vndb:v9") : lab.Catalog.Works.Single(w => w.Id == id));
        await lab.Init(); var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, lab.Catalog.DetailCalls);
        Assert.Equal("200", AutomaticRecognitionPolicy.Select(lab.Queue.RequireCurrentResult(id))?.Work.Id);
        Assert.True((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted);
        Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id);
    }

    [Fact]
    public async Task WeakReferencedParentDoesNotDisplaceStrongEditionDetailCandidates()
    {
        const string title = "Hoshi no Kouro RE:BOOT";
        var index = new TitleIndex(() => new MemoryStream(Gzip("""{"version":1,"records":[{"names":["Hoshi no Kouro RE:BOOT"],"references":["vndb:v9"]}]}""")));
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry(title, @"C:\synthetic\hoshi"), reader: new FixedReader(E(title)), titleIndex: index);
        lab.Catalog.Works = [W("100", "星空の航路", "vndb:v9") with { TitleEvidence = [] },
            W("200", title, "bangumi:10"), W("201", title, "erogamescape:11")];
        var fetched = new List<string>();
        lab.Catalog.Detail = (workId, _) => { fetched.Add(workId); return Task.FromResult(lab.Catalog.Works.Single(w => w.Id == workId)); };
        await lab.Init(); var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, lab.Catalog.DetailCalls);
        Assert.Equal(new[] { "200", "201" }, fetched.Order());
        Assert.DoesNotContain("100", fetched);
    }
}
