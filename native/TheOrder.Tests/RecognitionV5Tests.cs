using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionV5Tests
{
    private static WorkMetadata W(string id, string title, string language = "en", string reference = "") => Work(id, title) with {
        TitleEvidence = [new(title, "official", false, language)],
        References = reference.Length == 0 ? [] : [new(reference.Split(':')[0], reference.Split(':')[1], null)] };
    private static RecognitionEvidence E(params (string Source, string Value)[] titles) => Evidence([.. titles.Select(t => new LocalTitleEvidence(t.Value, t.Source))]);
    private static TitleIndex Index(string json = "{\"version\":1,\"records\":[]}") => new(() => new MemoryStream(Gzip(json)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InferredIndexAndReleaseCannotSuppressLowerLevelFullTitleHomonym(bool reverse)
    {
        const string title = "The Voyage";
        var evidence = E(("product-name", title)) with {
            IndexProofs = [new(title, "product-name", [title], ["vndb:v9"])] };
        var indexed = W("200", title, reference: "vndb:v9");
        var other = W("201", title, reference: "vndb:v10");
        var releases = new Dictionary<string, ImmutableArray<ReleaseIdentity>> {
            ["200"] = [new("3", "200", title, title, "default", null)],
            ["201"] = [new("4", "201", title, title, "default", null)] };
        var ranked = new ReleaseAwareRanking().Rank(evidence, reverse ? [other, indexed] : [indexed, other], releases);
        Assert.Equal("200", ranked[0].Work.Id);
        Assert.Equal(2, ranked[0].EvidenceLevel);
        Assert.Equal(1, ranked[1].EvidenceLevel);
        Assert.False(ranked[0].HighConfidence);
        Assert.Contains("同时对应不同作品", ranked[0].Reason);
    }

    [Fact]
    public void IndependentSteamIdentityCanResolveSameFullTitleCompetition()
    {
        const string title = "The Voyage";
        var evidence = E(("folder", title)) with { References = [new("steam:100", "steam-config")] };
        var ranked = new ReleaseAwareRanking().Rank(evidence,
            [W("200", title, reference: "steam:100"), W("201", title, reference: "steam:101")]);
        Assert.Equal("200", ranked[0].Work.Id); Assert.True(ranked[0].HighConfidence);
        Assert.True(ranked[1].Vetoed);
    }

    [Fact]
    public void BinaryPublisherProofCanDistinguishFullTitleCompetitorOnlyWhenItDisagrees()
    {
        const string title = "The Voyage";
        var evidence = E(("folder", title), ("product-name", title)) with {
            Companies = ["North Star"], IndexProofs = [new(title, "product-name", [title], ["vndb:v9"])] };
        var mine = W("200", title, reference: "vndb:v9") with { CatalogIdentity = new(["North Star"], []) };
        var other = W("201", title, reference: "vndb:v10") with { CatalogIdentity = new(["Different Studio"], []) };
        var releases = new Dictionary<string, ImmutableArray<ReleaseIdentity>> {
            ["200"] = [new("3", "200", title, title, "default", null)],
            ["201"] = [new("4", "201", title, title, "default", null)] };
        Assert.True(new ReleaseAwareRanking().Rank(evidence, [mine, other], releases)[0].HighConfidence);
        // The same company is no longer a distinguishing fact when both records agree.
        other = other with { CatalogIdentity = mine.CatalogIdentity };
        evidence = evidence with { IndexProofs = evidence.IndexProofs.Add(new(title, "product-name", [title], ["vndb:v10"])) };
        Assert.False(new ReleaseAwareRanking().Rank(evidence, [mine, other], releases)[0].HighConfidence);
    }
    private sealed class Reader(RecognitionEvidence evidence) : IRecognitionEvidenceReader
    {
        public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation) =>
            Task.FromResult(new LocalEvidenceSnapshot(evidence, [], [], new string('a', 64)) { IncludePe = includePe });
    }
    private sealed class Catalog(Func<string, OnlinePage> search, Func<string, WorkMetadata> detail) : IRecognitionCatalog
    {
        public readonly List<string> Queries = [], Fetched = [];
        public ImmutableArray<ReleaseIdentity> ReleaseRows = [];
        public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Queries.Add(query); return Task.FromResult(search(query)); }
        public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Fetched.Add(id); return Task.FromResult(detail(id)); }
        public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation) => Task.FromResult(ImmutableArray<WorkMetadata>.Empty);
        public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation) => Task.FromResult(ReleaseRows.Where(r => r.WorkId == id).ToImmutableArray());
        public Task TestConnectionAsync(CancellationToken cancellation) => Task.CompletedTask;
        public void ClearCache() { }
    }

    [Theory]
    [InlineData("星空の航路 DVD-GAME", "星空の航路")]
    [InlineData("Star Voyage - CD GAME", "Star Voyage")]
    [InlineData("星空の航路 BD-GAME", "星空の航路")]
    [InlineData("Star Voyage RE:BOOT DVD-GAME", "Star Voyage RE:BOOT")]
    [InlineData("Star Voyage FD DVD-GAME", "Star Voyage FD")]
    [InlineData("Star Voyage: Book Two DVD-GAME", "Star Voyage: Book Two")]
    [InlineData("DVD Game Club", "DVD Game Club")]
    [InlineData("MyDVD-GAME", "MyDVD-GAME")]
    [InlineData("DVD-GAME", "DVD-GAME")]
    public void PhysicalMediaNoiseDoesNotEraseEditionOrChapter(string input, string expected) => Assert.Equal(expected, LocalTitleCleaner.Clean(input));

    [Fact]
    public async Task SubtitleQueriesRetrieveWithoutInventingIdentityFacts()
    {
        const string full = "Star Voyage: Book Two";
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry(full), E(("folder", full)), Index(), default);
        Assert.Contains(full, identity.Queries); Assert.Contains("Star Voyage", identity.Queries); Assert.InRange(identity.Queries.Length, 1, 6);
        Assert.Single(identity.Evidence.Titles); Assert.Equal(full, identity.Evidence.Titles[0].Value);
        Assert.Empty(identity.Evidence.IndexProofs); Assert.Empty(identity.References);
        var ranked = new ReleaseAwareRanking().Rank(identity.Evidence, [W("200", "Star Voyage: Book One")]);
        Assert.Null(AutomaticRecognitionPolicy.Select(new(Guid.NewGuid(), new(Entry(full), new(Guid.Empty, Guid.Empty, 0, 0)), new(identity.Evidence, [], [], ""), identity,
            ranked, TitleMatching.AlgorithmVersion, "", false, null, null)));
    }

    [Fact]
    public async Task ChineseSpellingKeepsEvidenceAndInferredReferenceProvenance()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string traditional = "春夢幻夜物語", simplified = "春梦幻夜物语";
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry(traditional), E(("folder", traditional)),
            Index("""{"version":1,"records":[{"names":["春梦幻夜物语"],"references":["vndb:v9"]}]}"""), default);
        Assert.Contains(traditional, identity.Queries); Assert.Contains(simplified, identity.Queries);
        Assert.Single(identity.Evidence.Titles); Assert.Equal(traditional, identity.Evidence.Titles[0].Value);
        Assert.Empty(identity.Evidence.IndexProofs); Assert.Contains(identity.Evidence.References, r => r.Value == "vndb:v9" && r.Source == "title-index");
        Assert.False(TitleMatching.Compare(traditional, simplified).Exact); // The compatibility oracle is unchanged.
    }

    [Fact]
    public void ScriptSpellingRequiresNonMachineChineseOfficialTitle()
    {
        if (!OperatingSystem.IsWindows()) return;
        var work = W("200", "春梦幻夜物语", "zh-Hans"); var evidence = E(("folder", "春夢幻夜物語"));
        Assert.True(new ReleaseAwareRanking().Rank(evidence, [work])[0].HighConfidence);
        Assert.False(new ReleaseAwareRanking().Rank(evidence, [work with { TitleEvidence = [new(work.Title, "official", true, "zh-Hans")] }])[0].HighConfidence);
        Assert.False(new ReleaseAwareRanking().Rank(evidence, [work with { TitleEvidence = [new(work.Title, "official", false, "ja")] }])[0].HighConfidence);
        Assert.False(new ReleaseAwareRanking().Rank(evidence, [work with { TitleEvidence = [new(work.Title, "alias", false, "zh-Hans")] }])[0].HighConfidence);
        Assert.Empty(LocalQueryVariants.Chinese("春夢の物語"));
        Assert.False(LocalQueryVariants.ChineseExact("春夢", "春梦", "zh-Hans"));
    }

    [Theory]
    [InlineData("時代的構想圖", "时代的构想图")]
    [InlineData("白晝夢的構想圖", "白昼梦的构想图")]
    [InlineData("春夢幻夜物語", "春梦幻夜物语")]
    public void ChineseRetrievalRetainsAllMeaningfulCharacters(string traditional, string simplified)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains(simplified, LocalQueryVariants.Chinese(traditional));
    }

    [Fact]
    public void ScriptSpellingCannotOverridePhysicalIdentityOrChapterConflict()
    {
        if (!OperatingSystem.IsWindows()) return;
        var evidence = E(("folder", "春夢幻夜物語")) with { References = [new("vndb:v10", "config")] };
        Assert.True(new ReleaseAwareRanking().Rank(evidence, [W("200", "春梦幻夜物语", "zh-Hans", "vndb:v9")])[0].Vetoed);
        evidence = E(("folder", "春夢幻夜物語 第1章"));
        Assert.False(new ReleaseAwareRanking().Rank(evidence, [W("200", "春梦幻夜物语 第2章", "zh-Hans")])[0].HighConfidence);
        Assert.False(new ReleaseAwareRanking().Rank(E(("folder", "春夢幻夜物語 FD")), [W("200", "春梦幻夜物语", "zh-Hans")])[0].HighConfidence);
    }

    [Fact]
    public async Task ChineseConversionCollisionRemainsKnownHomonymEvenIfOtherWorkIsNotReturned()
    {
        if (!OperatingSystem.IsWindows()) return;
        var index = Index("""{"version":1,"records":[{"names":["春夢幻夜物語"],"references":["vndb:v9"]},{"names":["春梦幻夜物语"],"references":["vndb:v10"]}]}""");
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry("春夢幻夜物語"), E(("folder", "春夢幻夜物語")), index, default);
        Assert.Equal(2, Assert.Single(identity.Evidence.Homonyms).Identities.Length);
        Assert.False(new ReleaseAwareRanking().Rank(identity.Evidence, [W("200", "春梦幻夜物语", "zh-Hans", "vndb:v10")])[0].HighConfidence);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SpecificSingletonDetailIsFairButNeverAutomaticAuthority(bool stillIncomplete, bool hasMorePages)
    {
        const string full = "Star Voyage ~Echoes of Dawn~";
        var card = W("200", "晨曦航路") with { TitleEvidence = [] };
        var broad = W("201", "Another Voyage") with { Aliases = ["Star Voyage"] };
        var second = W("202", "Star Voyage Extra") with { Aliases = ["Star Voyage"] };
        var catalog = new Catalog(q => q == full ? new([card], hasMorePages ? "cur_more" : null) : new([broad, second], null),
            id => id == "200" ? stillIncomplete ? card : W("200", full) : id == "201" ? broad : second);
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry("Star Voyage", @"C:\synthetic\voyage"), reader: new Reader(E(("folder", "Star Voyage"), ("product-name", full))), catalog: catalog);
        await lab.Init(); var job = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var selected = AutomaticRecognitionPolicy.Select(lab.Queue.RequireCurrentResult(job));
        Assert.Equal(2, catalog.Fetched.Count); Assert.InRange(catalog.Queries.Count, 1, 6);
        if (hasMorePages) Assert.DoesNotContain("200", catalog.Fetched); else Assert.Contains("200", catalog.Fetched);
        if (stillIncomplete || hasMorePages) Assert.Null(selected);
        else
        {
            Assert.Equal("200", selected?.Work.Id); Assert.True((await lab.Reviews.ApplyAutomaticAsync(job, default)).IsCommitted);
            Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id);
        }
    }

    [Fact]
    public async Task SpecificSearchSingletonCannotDisplaceAnIdentityConflict()
    {
        const string full = "Star Voyage ~Echoes of Dawn~";
        var veto = W("200", "晨曦航路", reference: "vndb:v9") with { TitleEvidence = [] };
        var other = W("201", "Star Voyage", reference: "vndb:v10");
        var extra = W("202", "Another Voyage", reference: "vndb:v10") with { Aliases = ["Star Voyage"] };
        var catalog = new Catalog(q => q == full ? new([veto], null) : new([other, extra], null), id => id == "200" ? veto : id == "201" ? other : extra);
        var evidence = E(("folder", "Star Voyage"), ("product-name", full)) with { References = [new("vndb:v10", "config")] };
        await using var lab = new RecognitionReviewTests.Lab(entry: Entry("Star Voyage", @"C:\synthetic\voyage"), reader: new Reader(evidence), catalog: catalog);
        await lab.Init(); var job = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("200", catalog.Fetched);
        Assert.True(lab.Queue.RequireCurrentResult(job).Candidates.Single(c => c.Work.Id == "200").Vetoed);
    }
}
