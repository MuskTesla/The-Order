using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
using static TheOrder.Tests.RecognitionReviewTests;

namespace TheOrder.Tests;

public sealed class AutomaticRecognitionTests
{
    [Fact]
    public async Task ProgressIncludesWaitingImportsDeduplicatesAndSettlesCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Candidates = [] };
        engine.Hold = async token => { if (engine.Calls >= 2) entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        var additions = Enumerable.Range(1, 6).Select(n => Entry(folder: @"C:\synthetic\progress" + n) with { Id = "g" + n }).ToImmutableArray();
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode(additions, Clock));
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        var ids = additions.Select(e => e.Id).ToImmutableArray();
        await vm.StartImportedAsync(ids); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new RecognitionProgress(6, 4, 2, 0, 0, 0, 0, 0), vm.Progress);
        await vm.StartImportedAsync(ids.Add("a"));
        Assert.Equal(new RecognitionProgress(7, 5, 2, 0, 0, 0, 0, 0), vm.Progress);
        Assert.Contains("排队 5", vm.ProgressText); Assert.True(vm.HasRecognitionWork);
        vm.CancelAll(); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new RecognitionProgress(7, 0, 0, 0, 0, 0, 7, 0), vm.Progress); Assert.False(vm.HasRecognitionWork);
        engine.Hold = null;
        await vm.StartImportedAsync(["a"]); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new RecognitionProgress(1, 0, 0, 1, 0, 0, 0, 0), vm.Progress);
    }
    [Fact]
    public async Task CredentialRetryWaitsForAnEarlierImportAndThenRetriesIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Candidates = [] };
        engine.Hold = async token => {
            if (engine.Calls == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            else engine.Candidates = [Candidate("200", .99)];
        };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await vm.StartImportedAsync(["a"]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = vm.StartUnidentifiedAsync(retryPending: true);
        Assert.False(retry.IsCompleted);
        release.TrySetResult(); await retry.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, engine.Calls); Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id);
        Assert.Empty(vm.Reviews);
    }
    [Fact]
    public async Task SavingCredentialCanResumeEntriesBlockedByOldEmptyReviews()
    {
        var engine = new Engine { Candidates = [] };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await vm.StartImportedAsync(["a"]); await vm.WaitForAutomaticRecognitionAsync();
        Assert.Null(lab.Library.Snapshot.Entries[0].Metadata); Assert.Single(vm.Reviews);
        engine.Candidates = [Candidate("200", .99)];
        await vm.StartUnidentifiedAsync(); Assert.Equal(1, engine.Calls);
        await vm.StartUnidentifiedAsync(retryPending: true); await vm.WaitForAutomaticRecognitionAsync();
        Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id); Assert.Empty(vm.Reviews);
        await vm.StartUnidentifiedAsync(retryPending: true); Assert.Equal(2, engine.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ConfirmationAdvancesOnlyAfterCommitAndEndsAtLibrary(bool hasNext)
    {
        await using var lab = new Lab(engine: new Engine { Candidates = [Candidate("200", .9)] }, ranker: new Ranker()); await lab.Init();
        if (hasNext) await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([Entry(folder: @"C:\synthetic\b") with { Id = "b" }], Clock));
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await vm.StartUnidentifiedAsync(); await vm.WaitForAutomaticRecognitionAsync();
        var order = vm.Reviews.Select(r => r.GameId).ToArray(); Assert.Equal(hasNext ? 2 : 1, order.Length);
        var completed = new List<(string Id, string? Next)>();
        vm.ReviewCompleted = (id, next) => { completed.Add((id, next)); return Task.CompletedTask; };
        vm.SelectForGame(order[0]); vm.SelectCandidate("200"); await vm.ConfirmSelectionAsync();
        Assert.True(vm.LastDecision?.IsCommitted, vm.LastDecision?.Message);
        Assert.Equal((order[0], hasNext ? order[1] : null), Assert.Single(completed));
    }
    [Fact]
    public async Task BatchRemovalIsAtomicAndKeepsUnselectedPersonalData()
    {
        await using var lab = new Lab(); await lab.Init();
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([Entry(folder: @"C:\synthetic\b") with { Id = "b", Notes = "保留" }], Clock));
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.Library.DeleteBatchAsync(["a", "missing"]));
        Assert.Equal(2, lab.Library.Snapshot.Entries.Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lab.Library.DeleteBatchAsync(["a"], cancelled.Token));
        Assert.Equal(2, lab.Library.Snapshot.Entries.Length);
        await lab.Library.DeleteBatchAsync(["a", "a"]);
        var remaining = Assert.Single(lab.Library.Snapshot.Entries); Assert.Equal("b", remaining.Id); Assert.Equal("保留", remaining.Notes);
        Assert.Equal(@"C:\synthetic\b", Assert.Single(remaining.Installations).Folder);
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Covers : IRemoteCoverRegistry { public string? Register(string? url, string revision) => url; }
    private sealed class Ranker : IRecognitionRanker
    {
        public double Score = .99;
        public bool Veto = false;
        public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, IEnumerable<WorkMetadata> works,
            IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases = null, CancellationToken cancellation = default)
            // Like the production ranker, only the first (unique) candidate carries HighConfidence.
            => [.. works.Select((w, i) => Candidate(w.Id, Score, i == 0 && !Veto) with { Work = w, Vetoed = Veto })];
    }
    private sealed class Engine : IRecognitionEngine
    {
        public ImmutableArray<RankedRecognitionCandidate> Candidates = [Candidate("200", .99)];
        public Func<CancellationToken, Task>? Hold;
        public int Calls;
        public bool? LastIncludePe;
        public async Task<RecognitionResult> RecognizeAsync(RecognitionEntrySnapshot input, Guid generation, bool online, bool includePe,
            Action<RecognitionJobState> progress, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            LastIncludePe = includePe;
            if (Hold is { } hold) await hold(cancellation);
            cancellation.ThrowIfCancellationRequested();
            var evidence = Evidence(new LocalTitleEvidence(input.Entry.Name, "folder"));
            return new(generation, input, new(evidence, [], [], new string('a', 64)), new(evidence, [], [], []), Candidates,
                TitleMatching.AlgorithmVersion, RecognitionFingerprint.Identity(input.Entry), false, null, 0);
        }
    }
    private static RankedRecognitionCandidate Candidate(string id, double score, bool unique = true) => new(Work(id), score, "合成分数", unique, false, [], 2, [], []);
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ImportUsesElectronDefaultPeEvidenceAndHonorsOptOut(bool enabled)
    {
        var engine = new Engine(); await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        Assert.True(vm.IncludePe); vm.IncludePe = enabled;
        await vm.StartImportedAsync(["a"]); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(enabled, engine.LastIncludePe); Assert.Equal(1, engine.Calls);
    }
    private static WorkspaceViewModel Workspace(Lab lab) => new(lab.Offline, new Preferences(), new(lab.Profile.Root), new LegacyClientTextRules());
    private static RecognitionViewModel Vm(Lab lab, WorkspaceViewModel workspace) => new(lab.Offline, lab.Queue, lab.Reviews,
        lab.Catalog, new Covers(), lab.Credentials.AuthContext, workspace);
    private static async Task<Guid> Recognize(Lab lab)
    {
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.MissingMetadata, true);
        await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); return id;
    }

    [Theory]
    [InlineData(.899, false)][InlineData(.9, false)][InlineData(.900001, true)][InlineData(.99, true)]
    [InlineData(1, true)][InlineData(double.NaN, false)][InlineData(double.PositiveInfinity, false)]
    public void ThresholdIsStrictAndFinite(double score, bool expected) => Assert.Equal(expected, AutomaticRecognitionPolicy.Eligible(Candidate("200", score)));

    // Recognition v2: the ranker's uniqueness verdict decides. Several eligible candidates without
    // it are a tie for the user, not a choice by score or id (that rule applied wrong identities to
    // same-title works in replay evaluation).
    [Fact]
    public async Task UniqueTopIsAppliedAlongsideLowerEligibleCandidates()
    {
        var engine = new Engine { Candidates = [Candidate("201", .98), Candidate("202", .95, false), Candidate("200", .92, false)] };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init(); var id = await Recognize(lab);
        Assert.True((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted);
        Assert.Equal("201", Assert.Single(lab.Library.Snapshot.Entries).Metadata?.Id); Assert.Empty(lab.Reviews.Snapshot);
    }
    [Fact]
    public async Task SeveralEligibleCandidatesWithoutUniquenessStayForReview()
    {
        var engine = new Engine { Candidates = [Candidate("201", .98, false), Candidate("202", .95, false)] };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init(); var id = await Recognize(lab);
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted);
        Assert.Null(Assert.Single(lab.Library.Snapshot.Entries).Metadata); Assert.Single(lab.Reviews.Snapshot); Assert.Equal(0, lab.Catalog.DetailCalls);
    }
    [Fact]
    public async Task RefetchedContextThatLosesUniquenessPreventsAutomaticCommit()
    {
        var engine = new Engine { Candidates = [Candidate("200", .99), Candidate("201", .5, false)] };
        await using var lab = new Lab(engine: engine, ranker: new ReorderingRanker()); await lab.Init(); var id = await Recognize(lab);
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    // Fresh details make another candidate rank first: the original decision no longer holds.
    private sealed class ReorderingRanker : IRecognitionRanker
    {
        public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, IEnumerable<WorkMetadata> works,
            IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases = null, CancellationToken cancellation = default)
            => [.. works.Reverse().Select((w, i) => Candidate(w.Id, .99, i == 0) with { Work = w })];
    }
    [Theory][InlineData("score")][InlineData("veto")][InlineData("version")][InlineData("conflicts")]
    public async Task IneligibleResultsRemainForManualReviewWithoutWriting(string reason)
    {
        var candidate = Candidate("200", reason == "score" ? .9 : .99);
        // A vetoed candidate is never unique (the review codec rejects that combination).
        candidate = reason switch { "veto" => candidate with { Vetoed = true, HighConfidence = false }, "version" => candidate with { Vetoed = true, HighConfidence = false, VersionNeedsConfirmation = true, EvidenceLevel = 0 },
            "conflicts" => candidate with { Vetoed = true, HighConfidence = false, Conflicts = ["不同作品编号"] }, _ => candidate };
        await using var lab = new Lab(engine: new Engine { Candidates = [candidate] }, ranker: new Ranker()); await lab.Init(); var id = await Recognize(lab);
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
        Assert.Single(lab.Reviews.Snapshot); Assert.Equal(0, lab.Catalog.DetailCalls);
    }
    [Theory][InlineData(.9, false)][InlineData(.99, true)]
    public async Task RefetchedDetailsMustStillMeetAutomaticThreshold(double freshScore, bool expected)
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker { Score = freshScore }); await lab.Init(); var id = await Recognize(lab);
        Assert.Equal(expected, (await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted);
        Assert.Equal(expected, lab.Library.Snapshot.Entries[0].Metadata is not null);
    }
    [Fact]
    public async Task CanonicalIdentityChangeCannotAuthorizeAnotherAutomaticRelation()
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker()); await lab.Init(); var id = await Recognize(lab);
        lab.Catalog.Detail = (_, _) => Task.FromResult(Work("201"));
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Fact]
    public async Task RefetchedVersionConflictPreventsAutomaticCommit()
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker { Veto = true }); await lab.Init(); var id = await Recognize(lab);
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
        Assert.Single(lab.Reviews.Snapshot);
    }
    [Theory][InlineData("credential")][InlineData("evidence")][InlineData("reidentify")]
    public async Task ChangedAuthorizationEvidenceOrGenerationPreventsAutomaticCommit(string change)
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker()); await lab.Init(); var id = await Recognize(lab);
        if (change == "credential") await lab.Credentials.ClearAsync(default);
        else if (change == "evidence") lab.Reader.Hash = new string('b', 64);
        else { await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); }
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Fact]
    public async Task ExistingRelationAndExplicitReidentificationNeverGainAutomaticAuthority()
    {
        await using var lab = new Lab(entry: Entry(folder: @"C:\synthetic\game") with { Metadata = Work("100") }, engine: new Engine(), ranker: new Ranker());
        await lab.Init(); var id = await Recognize(lab);
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(id, default)).IsCommitted); Assert.Equal("100", lab.Library.Snapshot.Entries[0].Metadata?.Id);
        var second = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync();
        Assert.False((await lab.Reviews.ApplyAutomaticAsync(second, default)).IsCommitted);
    }
    [Fact]
    public async Task ImportWorkflowAppliesWithoutOpeningRecognitionPageAndPreservesPersonalFields()
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await lab.Library.UpdateAsync("a", new() { Notes = "个人笔记保留", Rating = 8, Favorite = true, Categories = ImmutableArray.Create("个人分类") });
        await vm.StartImportedAsync(["a", "a"]); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var entry = Assert.Single(lab.Library.Snapshot.Entries); Assert.Equal("200", entry.Metadata?.Id);
        Assert.Equal("个人笔记保留", entry.Notes); Assert.Equal(8, entry.PersonalRating); Assert.True(entry.Favorite);
        Assert.Contains("个人分类", entry.Categories); Assert.Equal(WorkspacePage.Library, workspace.Page);
        Assert.Equal(1, lab.Queue.Progress.Completed); Assert.Contains("已应用 1", vm.Message);
    }
    [Fact]
    public async Task LowScoreRemainsVisibleWhileHighScoreIsApplied()
    {
        await using var lab = new Lab(engine: new Engine { Candidates = [Candidate("200", .9)] }, ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await vm.StartImportedAsync(["a"]); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(lab.Library.Snapshot.Entries[0].Metadata); Assert.Single(vm.Reviews); Assert.Contains("1 项需核对", vm.Message);
    }
    [Fact]
    public async Task AutomaticApplyWaitsForUnsavedDraftAndPreservesSelectionAndPersonalEdits()
    {
        await using var lab = new Lab(engine: new Engine(), ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        Assert.True(await workspace.NavigateAsync(WorkspacePage.Details, "a", () => Task.FromResult(UnsavedChoice.Stay)));
        workspace.Draft!.Notes = "正在编辑的新笔记"; var draft = workspace.Draft; var selection = workspace.SelectionVersion;
        await vm.StartImportedAsync(["a"]); await lab.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(lab.Library.Snapshot.Entries[0].Metadata); Assert.Same(draft, workspace.Draft); Assert.True(workspace.IsDirty);
        Assert.True((await workspace.SaveAsync()).IsCommitted);
        // The save itself refreshes selection; the automatic commit must preserve the resulting state.
        selection = workspace.SelectionVersion;
        await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id); Assert.Equal("正在编辑的新笔记", lab.Library.Snapshot.Entries[0].Notes);
        Assert.Equal(selection, workspace.SelectionVersion); Assert.Equal(WorkspacePage.Details, workspace.Page); Assert.Equal("a", workspace.SelectedId);
        Assert.False(workspace.IsDirty); Assert.Equal("正在编辑的新笔记", workspace.Draft!.Notes);
    }
    [Fact]
    public async Task ClosingCancelsTwoActiveJobsAndDoesNotStartQueuedTargets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Hold = async cancellation => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); } };
        await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        var additions = Enumerable.Range(1, 8).Select(n => Entry(folder: @"C:\synthetic\g" + n) with { Id = "g" + n }).ToImmutableArray();
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode(additions, Clock));
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); var vm = Vm(lab, workspace);
        await vm.StartImportedAsync([.. additions.Select(e => e.Id)]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.InRange(engine.Calls, 1, RecognitionQueue.WorkerCount);
        Assert.All(lab.Library.Snapshot.Entries, e => Assert.Null(e.Metadata));
    }
    [Fact]
    public async Task StartupOnlyProcessesUnlinkedInstalledEntriesAndDoesNotReplaceExistingReview()
    {
        var engine = new Engine(); await using var lab = new Lab(engine: engine, ranker: new Ranker()); await lab.Init();
        await using var workspace = Workspace(lab); await workspace.InitializeAsync(); await using var vm = Vm(lab, workspace);
        await vm.StartUnidentifiedAsync(); await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id);
        await vm.StartUnidentifiedAsync(); Assert.Equal(1, engine.Calls);
    }
    [Fact]
    public async Task ImportCompletionWaitsForArtworkAfterCommitWithoutOpeningDetails()
    {
        await using var lab=new Lab(engine:new Engine(),ranker:new Ranker());await lab.Init();
        await using var workspace=Workspace(lab);await workspace.InitializeAsync();await using var vm=Vm(lab,workspace);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PrepareImportedArtwork=async(id,token)=> {
            Assert.Equal("200",id);Assert.Equal("200",lab.Library.Snapshot.Entries[0].Metadata?.Id);
            Assert.Equal(WorkspacePage.Library,workspace.Page);Assert.False(workspace.IsDirty);
            entered.SetResult();await release.Task.WaitAsync(token);return true;
        };
        await vm.StartImportedAsync(["a"]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.WaitForAutomaticRecognitionAsync().IsCompleted);
        Assert.Equal(new RecognitionProgress(1, 0, 1, 0, 0, 0, 0, 0), vm.Progress);
        Assert.True(vm.HasRecognitionWork);
        Assert.Contains("准备作品图片",vm.Message);release.SetResult();
        await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Contains("自动识别完成",vm.Message);
        Assert.Equal(new RecognitionProgress(1, 0, 0, 0, 1, 0, 0, 0), vm.Progress);
        Assert.False(vm.HasRecognitionWork);
    }
    [Fact]
    public async Task ArtworkFailureCannotUndoOrMislabelCommittedImport()
    {
        await using var lab=new Lab(engine:new Engine(),ranker:new Ranker());await lab.Init();
        await using var workspace=Workspace(lab);await workspace.InitializeAsync();await using var vm=Vm(lab,workspace);
        vm.PrepareImportedArtwork=(_,_)=>Task.FromResult(false);
        await vm.StartImportedAsync(["a"]);await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("200",lab.Library.Snapshot.Entries[0].Metadata?.Id);Assert.Contains("图片准备未完成",vm.Message);Assert.Contains("已应用 1",vm.Message);
    }
    [Fact]
    public async Task AmbiguityDoesNotPrepareAnUnappliedCandidate()
    {
        await using var lab=new Lab(engine:new Engine{Candidates=[Candidate("200",.9)]},ranker:new Ranker());await lab.Init();
        await using var workspace=Workspace(lab);await workspace.InitializeAsync();await using var vm=Vm(lab,workspace);
        int calls=0;vm.PrepareImportedArtwork=(_,_)=>{calls++;return Task.FromResult(true);};
        await vm.StartImportedAsync(["a"]);await vm.WaitForAutomaticRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,calls);Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
}
