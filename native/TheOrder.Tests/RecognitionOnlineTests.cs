using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Recognition;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionOnlineTests
{
    private const string WorkJson = "{\"object\":\"work\",\"id\":\"200\",\"display_name\":\"星空の航路\",\"refs\":[{\"source\":\"vndb\",\"external_id\":\"v123\"}]}";
    [Fact] public async Task ResolveUsesVerbatimReferencesAndKeepsAmbiguity()
    {
        string other = WorkJson.Replace("\"200\"", "\"201\""), unrelated = WorkJson.Replace("v123", "v777").Replace("\"200\"", "\"202\"");
        using var http = new HttpClient(new Handler((request, _) => { Assert.Contains("refs=vndb%3Av123", request.RequestUri!.Query); return Task.FromResult(Json("{\"object\":\"list\",\"items\":[" + WorkJson + "," + other + "," + unrelated + "]}")); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(new[] { "200", "201" }, (await catalog.ResolveReferencesAsync(["vndb:v123"], default)).Select(w => w.Id));
    }
    [Theory][InlineData("vndb:r123")][InlineData(" vndb:v123")][InlineData("steam:0")][InlineData("bangumi:01")]
    public async Task InvalidReferencesNeverSend(string reference)
    {
        int sent = 0; using var http = new HttpClient(new Handler((_, _) => { sent++; return Task.FromResult(Json("{}")); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidRequest, (await Assert.ThrowsAsync<OnlineException>(() => catalog.ResolveReferencesAsync([reference], default))).Failure); Assert.Equal(0, sent);
    }
    [Theory][InlineData("null", true)][InlineData("\"200\"", true)][InlineData("\"201\"", false)][InlineData("200", false)]
    public async Task ReleasesValidateOwnershipBeforeBecomingEvidence(string parent, bool valid)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"object\":\"list\",\"items\":[{\"object\":\"release\",\"id\":\"3\",\"work_id\":" + parent + ",\"title\":\"星空の航路 FD\",\"release_kind\":\"digital\",\"date\":\"2020-01-01\"}]}"))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        if (!valid) Assert.Equal(OnlineFailure.InvalidResponse, (await Assert.ThrowsAsync<OnlineException>(() => catalog.ReleasesAsync("200", default))).Failure);
        else { var row = Assert.Single(await catalog.ReleasesAsync("200", default)); Assert.Equal("200", row.WorkId); Assert.Equal("digital", row.Kind); Assert.Equal("2020-01-01", row.Date); }
    }
    [Fact] public async Task RepeatedReleaseCursorIsRejectedAndPaginationIsBounded()
    {
        int sent = 0; using var http = new HttpClient(new Handler((_, _) => { sent++; return Task.FromResult(Json("{\"object\":\"list\",\"items\":[],\"next_cursor\":\"cur_same\"}")); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidResponse, (await Assert.ThrowsAsync<OnlineException>(() => catalog.ReleasesAsync("200", default))).Failure); Assert.Equal(2, sent);
    }
    private sealed class EvidenceReader : IRecognitionEvidenceReader
    {
        public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation)
            => Task.FromResult(new LocalEvidenceSnapshot(Evidence(new LocalTitleEvidence(entry.Name, "folder")), [], [], "local"));
    }
    private sealed class Catalog : IRecognitionCatalog
    {
        public Func<CancellationToken, Task<OnlinePage>> Search = _ => Task.FromResult(new OnlinePage([Work()], null));
        public Func<string, CancellationToken, Task<WorkMetadata>> Details = (id, _) => Task.FromResult(Work(id));
        public Task<OnlinePage> SearchAsync(string query, string? cursor, CancellationToken cancellation) => Search(cancellation);
        public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken cancellation) => Details(id, cancellation);
        public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation) => Task.FromResult(ImmutableArray<WorkMetadata>.Empty);
        public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation) => Task.FromResult(ImmutableArray<ReleaseIdentity>.Empty);
        public Task TestConnectionAsync(CancellationToken cancellation) => Task.CompletedTask;
        public void ClearCache() { }
    }
    private static TitleIndex Index() => new(() => new MemoryStream(Gzip("{\"version\":1,\"records\":[{\"names\":[\"星空の航路\"],\"references\":[\"vndb:v123\"]}]}")));
    private static RecognitionEntrySnapshot Input() => new(Entry(folder: @"C:\synthetic\game"), new(Guid.NewGuid(), Guid.NewGuid(), 1, 0));
    [Theory][InlineData(OnlineFailure.NoCredential)][InlineData(OnlineFailure.Offline)][InlineData(OnlineFailure.RateLimited)][InlineData(OnlineFailure.InvalidResponse)]
    public async Task NetworkFailureRetainsIndependentLocalCandidates(OnlineFailure failure)
    {
        var catalog = new Catalog { Search = _ => throw new OnlineException(failure) };
        var engine = new RecognitionEngine(Index(), new EvidenceReader(), new EvidenceRanking(), catalog);
        var result = await engine.RecognizeAsync(Input(), Guid.NewGuid(), true, false, _ => { }, default);
        Assert.True(result.EnrichmentUnavailable); Assert.Single(result.Identity.LocalCandidates); Assert.Empty(result.Candidates);
    }
    [Fact] public async Task LocalOnlyNeverRequiresOnlineOrEagerIndexAtStartup()
    {
        var catalog = new Catalog { Search = _ => throw new Xunit.Sdk.XunitException("Offline recognition must not send.") };
        var index = Index(); var engine = new RecognitionEngine(index, new EvidenceReader(), new EvidenceRanking(), catalog);
        Assert.Equal(0, index.RecordCount); var result = await engine.RecognizeAsync(Input(), Guid.NewGuid(), false, false, _ => { }, default);
        Assert.Single(result.Identity.LocalCandidates); Assert.True(result.EnrichmentUnavailable); Assert.Empty(result.Candidates);
    }
    [Fact] public async Task ChangedCredentialDiscardsAllOldEnrichmentButRetainsLocalEvidence()
    {
        var credentials = new Credential(); var protectedCredentials = new AuthenticatedCredentials(credentials, credentials);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new Catalog { Details = async (id, _) => { entered.SetResult(); await release.Task; return Work(id); } };
        var engine = new RecognitionEngine(Index(), new EvidenceReader(), new EvidenceRanking(), catalog, protectedCredentials.AuthContext);
        var task = engine.RecognizeAsync(Input(), Guid.NewGuid(), true, false, _ => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await protectedCredentials.ClearAsync(default); release.SetResult();
        var result = await task; Assert.Single(result.Identity.LocalCandidates); Assert.Empty(result.Candidates); Assert.Null(result.AuthGeneration); Assert.True(result.EnrichmentUnavailable);
    }
    [Fact] public async Task ActualHttpWrongDetailsIdCannotBecomeVerifiedCandidate()
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal)
            ? "{\"object\":\"list\",\"items\":[{\"object\":\"search_result\",\"target_object\":\"work\",\"id\":\"200\"}]}"
            : request.RequestUri.AbsolutePath.EndsWith("/200", StringComparison.Ordinal) ? WorkJson.Replace("\"200\"", "\"999\"") : "{\"object\":\"list\",\"items\":[" + WorkJson + "]}"))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var engine = new RecognitionEngine(Index(), new EvidenceReader(), new EvidenceRanking(), catalog);
        var result = await engine.RecognizeAsync(Input(), Guid.NewGuid(), true, false, _ => { }, default);
        Assert.True(result.EnrichmentUnavailable); Assert.DoesNotContain(result.Candidates, c => c.Work.Id == "999");
    }
}
