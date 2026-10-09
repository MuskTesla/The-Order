using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionReviewTests;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionPresentationTests
{
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken token) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Covers : IRemoteCoverRegistry { public string? Register(string? url, string revision) => url; }
    private static WorkspaceViewModel Workspace(Lab lab) => new(lab.Offline, new Preferences(), new(lab.Profile.Root), new LegacyClientTextRules());
    private static RecognitionViewModel Vm(Lab lab, WorkspaceViewModel workspace) => new(lab.Offline, lab.Queue, lab.Reviews, lab.Catalog, new Covers(), lab.Credentials.AuthContext, workspace);

    [Fact] public async Task StableRefreshRetainsCardsAndIgnoreClearsRemoteDetails()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize();
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace);
        vm.Refresh(); var cards = vm.Reviews; vm.Select(id); Assert.Same(cards, vm.Reviews);
        var candidates = vm.Candidates; await vm.OpenDetailsAsync("200"); vm.Refresh();
        Assert.Same(candidates, vm.Candidates); Assert.Contains("编号：200", vm.RemoteFacts);
        Assert.Contains("识别时关联", vm.LocalFacts);
        await vm.IgnoreAsync(); Assert.False(vm.HasSelection); Assert.Empty(vm.Candidates);
        Assert.Null(vm.RemoteCoverKey); Assert.DoesNotContain("编号：200", vm.RemoteFacts);
    }
    [Fact]
    public async Task AmbiguousOfficialTitlesDoNotClaimUniqueCorrespondenceInTheUi()
    {
        await using var lab = new Lab();
        lab.Catalog.Works = [Work("200"), Work("201")];
        await lab.Init(); var id = await lab.Recognize();
        var result = lab.Queue.RequireCurrentResult(id);
        Assert.Null(AutomaticRecognitionPolicy.Select(result));
        Assert.Contains(result.Candidates, c => c.Reason.Contains("完整官方标题唯一对应", StringComparison.Ordinal));
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace);
        vm.Select(id); Assert.Equal(2, vm.Candidates.Count);
        Assert.All(vm.Candidates, c => Assert.DoesNotContain("唯一对应", c.Summary));
        Assert.Null(Assert.Single(lab.Library.Snapshot.Entries).Metadata);
    }
    [Fact] public async Task LateDetailsAfterIgnoredSelectionCannotReappear()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize();
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace);
        vm.Select(id); var deferred = new TaskCompletionSource<TheOrder.Domain.WorkMetadata>();
        lab.Catalog.Detail = (_, _) => deferred.Task;
        var old = vm.OpenDetailsAsync("200"); await vm.IgnoreAsync(); deferred.SetResult(Work()); await old;
        Assert.False(vm.HasSelection); Assert.DoesNotContain("编号：200", vm.RemoteFacts);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task InvalidReviewUpdatesQueueCountsAndRevokesDecision(bool manual)
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize();
        if (manual) Assert.True((await lab.Reviews.ManualLinkAsync("a", "200", default)).IsCommitted);
        else { await lab.Library.UpdateAsync("a", new() { Name = "changed title" }); await lab.Reviews.RefreshValidityAsync(default); }
        Assert.Equal(0, lab.Queue.Progress.NeedsReview); Assert.Equal(1, lab.Queue.Progress.Stale);
        Assert.Equal(RecognitionReviewStatus.Stale, Assert.Single(lab.Reviews.Snapshot).Status);
        Assert.Throws<StaleLibraryRequestException>(() => lab.Queue.RequireCurrentResult(id));
    }
    [Fact]
    public async Task DetailLinksClearOnSelectionAndCredentialChangeAndIgnoreLateResponse()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize();
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace); vm.Select(id);
        lab.Catalog.Detail = (_, _) => Task.FromResult(Work() with { WebUrl = "https://vndb.org/v200" });
        await vm.OpenDetailsAsync("200"); Assert.Single(vm.RemoteLinks);
        await lab.Credentials.ClearAsync(default); vm.RefreshIfDirty(); Assert.Empty(vm.RemoteLinks);
        var deferred = new TaskCompletionSource<TheOrder.Domain.WorkMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Catalog.Detail = (_, _) => deferred.Task;
        var pending = vm.OpenDetailsAsync("200"); await vm.IgnoreAsync();
        deferred.SetResult(Work() with { WebUrl = "https://vndb.org/v200" }); await pending; Assert.Empty(vm.RemoteLinks);
    }
}
