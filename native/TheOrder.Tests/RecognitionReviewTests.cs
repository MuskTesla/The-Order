using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionReviewTests
{
    internal sealed class MemoryStore : IRecognitionReviewStorage
    {
        public byte[]? Bytes, Legacy;
        public bool Fail;
        public int Writes;
        public Task<RecognitionReviewDocuments> ReadAsync(CancellationToken cancellation) => Task.FromResult(new RecognitionReviewDocuments(Bytes, Legacy));
        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (Fail) throw new IOException("synthetic write failure"); Bytes = bytes.ToArray(); Writes++; return Task.CompletedTask; }
    }
    internal sealed class Reader : IRecognitionEvidenceReader
    {
        public string Hash = new('a', 64);
        public Func<CancellationToken, Task>? Pause { get; set; }
        public async Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation)
        { if (Pause is { } pause) await pause(cancellation); return new(Evidence(new LocalTitleEvidence(entry.Name.Length > 0 ? entry.Name : Path.GetFileName(entry.Installations[0].Folder), "folder")), [], [], Hash); }
    }
    internal sealed class Catalog : IRecognitionCatalog
    {
        public WorkMetadata[] Works = [Work()]; public int DetailCalls;
        public Func<string, CancellationToken, Task<WorkMetadata>>? Detail;
        public Task<OnlinePage> SearchAsync(string q, string? cursor, CancellationToken c) => Task.FromResult(new OnlinePage([.. Works], null));
        public Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken c) { DetailCalls++; return Detail?.Invoke(id, c) ?? Task.FromResult(Works.FirstOrDefault(w => w.Id == id) ?? Work(id)); }
        public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken c) => Task.FromResult(ImmutableArray<WorkMetadata>.Empty);
        public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken c) => Task.FromResult(ImmutableArray<ReleaseIdentity>.Empty);
        public Task TestConnectionAsync(CancellationToken c) => Task.CompletedTask;
        public void ClearCache() { }
    }
    private sealed class Factory(TransactionProfile profile, IPersistenceProbe? probe) : ITransactionalLibraryFactory
    {
        public TransactionalLibrary Library = null!;
        public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.FromResult(Library = profile.Open(probe));
    }
    internal sealed class Lab : IAsyncDisposable
    {
        public readonly TransactionProfile Profile;
        public readonly Catalog Catalog = new(); public readonly MemoryStore Store; public readonly Reader Reader = new();
        public readonly OfflineLibraryClient Offline; public readonly RecognitionQueue Queue; public readonly RecognitionReviewService Reviews;
        public readonly AuthenticatedCredentials Credentials;
        private readonly Factory factory;
        public TransactionalLibrary Library => factory.Library;
        public Lab(MemoryStore? store = null, LibraryEntry? entry = null, IRecognitionCatalog? catalog = null,
            IRecognitionEvidenceReader? reader = null, IRecognitionReviewCodec? codec = null, ITitleIndex? titleIndex = null, IPersistenceProbe? probe = null,
            IRecognitionRanker? ranker = null, IRecognitionEngine? engine = null)
        {
            Profile = new(new LegacyLibraryCodec().Encode([entry ?? Entry(folder: @"C:\synthetic\game")], Clock)); factory = new(Profile, probe);
            Store = store ?? new(); Offline = new(factory, new LegacyClientTextRules());
            var secret = new OnlineTransportTests.Credential(); Credentials = new(secret, secret);
            var index = new TitleIndex(() => new MemoryStream(Gzip("{\"version\":1,\"records\":[]}")));
            Queue = new(Offline, engine ?? new RecognitionEngine(titleIndex ?? index, reader ?? Reader, ranker ?? new ReviewSafeRanking(), catalog ?? Catalog, Credentials.AuthContext), Credentials.AuthContext);
            Reviews = new(Offline, Queue, Store, codec ?? new RecognitionReviewCodec(), reader ?? Reader, ranker ?? new ReviewSafeRanking(), catalog ?? Catalog, Credentials.AuthContext, RecognitionFingerprint.Identity);
        }
        public async Task Init() { Assert.Equal(ClientOutcome.Success, (await Offline.LoadAsync(default)).Outcome); await Reviews.InitializeAsync(default); }
        public async Task<Guid> Recognize()
        { var id = await Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); await Reviews.FlushAsync(); return id; }
        public async ValueTask DisposeAsync() { await Queue.DisposeAsync(); await Reviews.DisposeAsync(); await Offline.DisposeAsync(); Profile.Dispose(); }
    }
    [Fact] public async Task ApplyRequiresLiveReviewAndPreservesLaterPersonalFields()
    {
        await using var lab = new Lab(); await lab.Init(); var review = await lab.Recognize();
        await lab.Library.UpdateAsync("a", new() { Notes = "new personal note", Rating = 9, Favorite = true, Categories = ImmutableArray.Create("personal") });
        var result = await lab.Reviews.ApplyAsync(review, "200", default);
        Assert.True(result.IsCommitted); var entry = lab.Library.Snapshot.Entries[0]; Assert.Equal("200", entry.Metadata?.Id); Assert.Equal("new personal note", entry.Notes); Assert.Equal(9, entry.PersonalRating); Assert.True(entry.Favorite); Assert.Contains("personal", entry.Categories);
        Assert.Empty(lab.Reviews.Snapshot); Assert.False((await lab.Reviews.ApplyAsync(review, "200", default)).IsCommitted);
    }
    [Fact] public async Task IgnoreFailureKeepsReviewAndSuccessOnlyDismissesExactDecision()
    {
        await using var lab = new Lab(); await lab.Init(); var review = await lab.Recognize(); lab.Store.Fail = true;
        Assert.Equal(ClientOutcome.IoFailure, (await lab.Reviews.IgnoreAsync(review, default)).Outcome); Assert.Single(lab.Reviews.Snapshot);
        lab.Store.Fail = false; Assert.Equal(ClientOutcome.Success, (await lab.Reviews.IgnoreAsync(review, default)).Outcome); Assert.Empty(lab.Reviews.Snapshot);
        var later = await lab.Recognize(); Assert.NotEqual(review, later); Assert.Single(lab.Reviews.Snapshot);
        Assert.False((await lab.Reviews.IgnoreAsync(review, default)).IsCommitted); Assert.Equal(later, Assert.Single(lab.Reviews.Snapshot).Id);
    }
    [Fact] public async Task RestartRequiresRecheckAndCannotReusePersistedDecision()
    {
        var storage = new MemoryStore(); Guid id;
        await using (var before = new Lab(storage)) { await before.Init(); id = await before.Recognize(); }
        await using var after = new Lab(storage); await after.Init(); Assert.Equal(RecognitionReviewStatus.NeedsRecheck, Assert.Single(after.Reviews.Snapshot).Status);
        Assert.Equal(ClientOutcome.Conflict, (await after.Reviews.ApplyAsync(id, "200", default)).Outcome); Assert.Null(after.Library.Snapshot.Entries[0].Metadata);
    }
    [Theory][InlineData("title")][InlineData("delete-recreate")][InlineData("installation")][InlineData("local-file")][InlineData("credential")]
    public async Task StaleBeforeApplyNeverCommits(string change)
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize(); var original = lab.Library.Snapshot.Entries[0];
        switch (change)
        {
            case "title": await lab.Library.UpdateAsync("a", new() { Name = "other" }); break;
            case "delete-recreate": await lab.Library.DeleteAsync("a"); await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([original], Clock)); break;
            case "installation": await lab.Library.RemoveInstallationAsync("a", "i"); break;
            case "local-file": lab.Reader.Hash = new string('b', 64); break;
            case "credential": await lab.Credentials.ClearAsync(default); break;
        }
        Assert.False((await lab.Reviews.ApplyAsync(id, "200", default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Fact] public async Task ReidentifyWhileApplyWaitsInvalidatesOnlyOldDecision()
    {
        await using var lab = new Lab(); await lab.Init(); var old = await lab.Recognize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Catalog.Detail = async (id, _) => { entered.TrySetResult(); await release.Task; return Work(id); };
        var apply = lab.Reviews.ApplyAsync(old, "200", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var later = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, false); release.SetResult();
        Assert.False((await apply).IsCommitted); await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
        Assert.Contains(lab.Reviews.Snapshot, r => r.Id == later);
    }
    [Fact] public async Task CommittedLibraryIsNotReportedAsFailureWhenReviewCleanupFails()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize(); lab.Store.Fail = true;
        var result = await lab.Reviews.ApplyAsync(id, "200", default); Assert.Equal(ClientOutcome.CommittedWithWarning, result.Outcome); Assert.Contains("已经保存", result.Message);
        Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id); Assert.False((await lab.Reviews.ApplyAsync(id, "200", default)).IsCommitted);
    }
    [Fact] public async Task VetoCannotBeBypassedBySelectingCandidateButManualIntentIsSeparate()
    {
        await using var lab = new Lab(entry: Entry("星空の航路 第2章", @"C:\synthetic\game")); lab.Catalog.Works = [Work(title: "星空の航路 第1章")]; await lab.Init();
        var id = await lab.Recognize(); Assert.True(lab.Queue.RequireCurrentResult(id).Candidates[0].Vetoed);
        Assert.Equal(ClientOutcome.ValidationFailure, (await lab.Reviews.ApplyAsync(id, "200", default)).Outcome);
        Assert.True((await lab.Reviews.ManualLinkAsync("a", "200", default)).IsCommitted); Assert.Equal("星空の航路 第2章", lab.Library.Snapshot.Entries[0].Name);
    }
    [Fact] public async Task ManualInvalidOrMissingTargetCannotCommit()
    {
        await using var lab = new Lab(); await lab.Init(); lab.Catalog.Detail = (_, _) => throw new OnlineException(OnlineFailure.NotFound);
        Assert.False((await lab.Reviews.ManualLinkAsync("a", "no-id", default)).IsCommitted);
        Assert.Equal(ClientOutcome.ValidationFailure, (await lab.Reviews.ManualLinkAsync("a", "404", default)).Outcome); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Fact] public async Task DifferentWorkRecognitionNeverAutomaticallyReplacesExistingRelation()
    {
        // Re-identification needs independent local evidence, not the old provider display name.
        await using var lab = new Lab(entry: Entry(folder: @"C:\synthetic\星空の航路") with { Metadata = Work("100", "原作品") }); await lab.Init();
        var id = await lab.Recognize(); Assert.Equal("100", lab.Library.Snapshot.Entries[0].Metadata?.Id); Assert.Contains(lab.Queue.RequireCurrentResult(id).Candidates, c => c.Work.Id == "200");
        Assert.True((await lab.Reviews.ApplyAsync(id, "200", default)).IsCommitted);
    }
    [Fact] public async Task CorruptReviewFileIsPreservedAndNeverSilentlyReplaced()
    {
        var bytes = Encoding.UTF8.GetBytes("{broken"); var storage = new MemoryStore { Bytes = bytes };
        await using var lab = new Lab(storage); await lab.Init(); Assert.NotNull(lab.Reviews.PersistenceError);
        await Assert.ThrowsAsync<IOException>(() => lab.Reviews.FlushAsync()); Assert.Equal(bytes, storage.Bytes); Assert.Equal(0, storage.Writes);
    }
    [Fact] public async Task LegacyQueueImportsAsHistoricalSuggestionsAndIgnoreDoesNotBlacklistGame()
    {
        var storage = new MemoryStore { Legacy = Encoding.UTF8.GetBytes("{\"status\":\"running\",\"reviews\":[{\"gameId\":\"a\",\"name\":\"星空の航路\",\"folderPath\":\"C:\\\\synthetic\\\\game\",\"reason\":\"old suggestion\",\"options\":[{\"work\":{\"id\":\"200\",\"title\":\"星空の航路\",\"originalTitle\":\"星空の航路\"},\"confidence\":0.99,\"reason\":\"title\"}]}]}") };
        await using var lab = new Lab(storage); await lab.Init(); var review = Assert.Single(lab.Reviews.Snapshot); Assert.Equal(RecognitionReviewStatus.NeedsRecheck, review.Status); Assert.Single(review.HistoricalCandidates);
        Assert.Equal(ClientOutcome.Success, (await lab.Reviews.IgnoreAsync(review.Id, default)).Outcome); await lab.Recognize(); Assert.Single(lab.Reviews.Snapshot);
    }
}
