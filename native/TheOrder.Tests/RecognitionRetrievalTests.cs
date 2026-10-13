using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionRetrievalTests
{
    private static TitleIndex Index(string title = "The Stellar Voyage") => new(() => new MemoryStream(Gzip(System.Text.Json.JsonSerializer.Serialize(new { version = 1, records = new[] { new { names = new[] { title }, references = new[] { "vndb:v9" } } } }))));

    [Theory]
    [InlineData("The Stelar Voyage", "The Stellar Voyage")]
    [InlineData("星空の航路物语", "星空の航路物語")]
    [InlineData("The Stellar Voyag", "The Stellar Voyage")]
    public async Task NearbyRowsRetrieveWithoutInventingIdentityProof(string observed, string official)
    {
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry(observed), Evidence(new LocalTitleEvidence(observed, "folder")), Index(official), default);
        var candidate = Assert.Single(identity.LocalCandidates);
        Assert.False(candidate.Exact); Assert.Equal(observed, candidate.ObservedTitle);
        Assert.Empty(identity.Evidence.IndexProofs); Assert.Empty(identity.Evidence.References); Assert.Empty(identity.Evidence.Homonyms); Assert.Empty(identity.References);
        Assert.Equal(observed, Assert.Single(identity.Evidence.Titles).Value);
    }

    [Theory]
    [InlineData("SOL", "Sole")]
    [InlineData("Stelar", "Stellar")]
    [InlineData("The Stellar Voyage 1", "The Stellar Voyage 2")]
    [InlineData("The Stellar Voyage FD", "The Stellar Voyage")]
    [InlineData("The Stellar Voyage Remake", "The Stellar Voyage")]
    public async Task ShortTitlesAndVersionConflictsAreNotFuzzyRetrieval(string observed, string official) =>
        Assert.Empty(await Index(official).FindSimilarAsync([observed], default));

    [Fact]
    public async Task ExactLookupAndProofArePreserved()
    {
        var index = Index(); Assert.Empty(await index.FindSimilarAsync(["The Stellar Voyage"], default));
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry("The Stellar Voyage"), Evidence(new LocalTitleEvidence("The Stellar Voyage", "folder")), index, default);
        Assert.True(Assert.Single(identity.LocalCandidates).Exact); Assert.Single(identity.Evidence.IndexProofs); Assert.Contains("vndb:v9", identity.References);
    }

    [Fact]
    public async Task FuzzyLookupHasFanoutInputAndCancellationBounds()
    {
        var bytes = Gzip(System.Text.Json.JsonSerializer.Serialize(new { version = 1, records = Enumerable.Range(0, 100).Select(i => new { names = new[] { "The Stellar Voyage" }, references = new[] { "vndb:v" + (i + 1) } }) }));
        var index = new TitleIndex(() => new MemoryStream(bytes));
        Assert.Equal(24, (await index.FindSimilarAsync(["The Stelar Voyage"], default)).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => index.FindSimilarAsync([.. Enumerable.Repeat("The Stelar Voyage", 7)], default));
        await Assert.ThrowsAsync<InvalidDataException>(() => index.FindSimilarAsync([new string('a', 501)], default));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.FindSimilarAsync(["The Stelar Voyage"], stop.Token));
    }

    private sealed class Reader(string title) : IRecognitionEvidenceReader
    {
        public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation) =>
            Task.FromResult(new LocalEvidenceSnapshot(Evidence(new LocalTitleEvidence(title, "folder")), [], [], "synthetic") { IncludePe = includePe });
    }
    private sealed class Catalog(bool officialAlias) : IRecognitionCatalog
    {
        public readonly List<string> Queries = [], Details = [];
        public ImmutableArray<string> Resolved;
        private WorkMetadata Current => Work("200", "The Stellar Voyage") with { Aliases = officialAlias ? ["The Stelar Voyage"] : [], References = [new("vndb", "v9", null)] };
        public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation) { Queries.Add(query); return Task.FromResult(new OnlinePage([], null)); }
        public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation) { Resolved = references; return Task.FromResult(references.Contains("vndb:v9") ? ImmutableArray.Create(Current) : []); }
        public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation) { Details.Add(id); return Task.FromResult(Current); }
        public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation) => Task.FromResult(ImmutableArray<ReleaseIdentity>.Empty);
        public Task TestConnectionAsync(CancellationToken cancellation) => Task.CompletedTask;
        public void ClearCache() { }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievedReferenceCannotGrantAuthorityButOfficialAliasMayConfirm(bool officialAlias)
    {
        var catalog = new Catalog(officialAlias);
        var engine = new RecognitionEngine(Index(), new Reader("The Stelar Voyage"), new ReleaseAwareRanking(), catalog);
        var result = await engine.RecognizeAsync(new(Entry("The Stelar Voyage", @"C:\synthetic\stellar"), new(Guid.Empty, Guid.Empty, 0, 0)) { Mode = RecognitionMode.MissingMetadata }, Guid.NewGuid(), true, true, _ => { }, default);
        Assert.Contains("vndb:v9", catalog.Resolved); Assert.Empty(result.Identity.References); Assert.Empty(result.Identity.Evidence.IndexProofs);
        Assert.Empty(result.Candidates[0].MatchedReferences); Assert.InRange(catalog.Queries.Count, 1, 6); Assert.InRange(catalog.Details.Count, 1, 2);
        var selected = AutomaticRecognitionPolicy.Select(result);
        if (officialAlias) Assert.Equal("200", selected?.Work.Id); else Assert.Null(selected);
    }
}
