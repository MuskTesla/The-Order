using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicAlbumViewModelTests
{
    private sealed class Subjects : IBangumiSubjectDetailsReader
    {
        public Func<string, CancellationToken, Task<BangumiSubjectDetailsResult>> Read = (id, _) => Task.FromResult(new BangumiSubjectDetailsResult(MusicTestData.Details(id), false, false, null));
        public Task<BangumiSubjectDetailsResult> LoadAsync(string id, BangumiRelationKind kind, bool refresh, CancellationToken token) => Read(id, token);
    }
    private sealed class Reader : IMusicBrainzReader
    {
        public int Confirmations, Reads;
        public string? LastGroup;
        public Func<MusicAlbumSource, Task<MusicBrainzMatchResult>> Match = _ => Task.FromResult(new MusicBrainzMatchResult(MusicTestData.Search(), MusicTestData.Release, null, false, false, null));
        public Func<string, Task<MusicBrainzAlbumResult>> Load = id => Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(id), false, false, null));
        public Task<MusicBrainzMatchResult> MatchAsync(MusicAlbumSource source, bool refresh, CancellationToken token) => Match(source);
        public Task<MusicBrainzAlbumResult> LoadAlbumAsync(string id, bool refresh, CancellationToken token) { Reads++; return Load(id); }
        public Task<MusicBrainzSearchResult> EditionsAsync(string id, CancellationToken token)
        { LastGroup = id; return Task.FromResult(MusicTestData.Search() with { Candidates = [MusicTestData.Candidate() with { ReleaseGroupId = id }] }); }
        public Task ConfirmAsync(MusicAlbumSource source, MusicBrainzAlbum album, CancellationToken token) { token.ThrowIfCancellationRequested(); Confirmations++; return Task.CompletedTask; }
    }
    [Fact] public async Task StrongMatchOpensMultiDiscAlbumWithoutWritingUserChoice()
    {
        var reader = new Reader(); using var model = new MusicAlbumViewModel(new Subjects(), reader);
        await model.OpenAsync(MusicTestData.Item()); Assert.Equal(MusicAlbumState.Ready, model.State);
        Assert.Equal(3, model.Album!.Album.Discs.Length); Assert.Equal(0, reader.Confirmations); Assert.False(model.CanConfirm);
    }
    [Fact] public async Task UncertainCandidateRequiresPreviewAndExplicitConfirmation()
    {
        var reader = new Reader { Match = _ => Task.FromResult(new MusicBrainzMatchResult(MusicTestData.Search(), null, null, false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        Assert.Equal(MusicAlbumState.Choosing, model.State); Assert.Null(model.Album); Assert.Equal(0, reader.Reads);
        await model.ConfirmAsync(); Assert.Equal(0, reader.Confirmations);
        await model.PreviewAsync(MusicTestData.Release); Assert.True(model.CanConfirm); Assert.Null(model.Album); Assert.Equal(0, reader.Confirmations);
        await model.ConfirmAsync(); Assert.Equal(1, reader.Confirmations); Assert.Equal(MusicAlbumState.Ready, model.State); Assert.Null(model.Preview);
    }
    [Fact] public async Task UnknownCandidateIdIsNotFetchedOrConfirmed()
    {
        var reader = new Reader(); using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        await model.PreviewAsync(MusicTestData.Other); await model.ConfirmAsync(); Assert.Equal(1, reader.Reads); Assert.Equal(0, reader.Confirmations);
    }
    [Fact] public async Task SearchFactsCannotOverrideConflictingFinalRelease()
    {
        var reader = new Reader { Load = id => Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(id) with { Release = MusicTestData.Candidate(id) with { Date = "2025-01-01" } }, false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        Assert.Equal(MusicAlbumState.Choosing, model.State); Assert.Null(model.Album); Assert.Equal(0, reader.Confirmations);
    }
    [Fact] public async Task NoMatchAndFailureKeepBangumiSummaryAndRelation()
    {
        var reader = new Reader { Match = _ => Task.FromResult(new MusicBrainzMatchResult(new([], 0, false, null, null), null, null, false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        Assert.Contains("暂未找到", model.Message); Assert.NotNull(model.SourceDetails); Assert.Equal("原声音乐", model.Item!.Relation);
        reader.Match = _ => throw new MusicBrainzException(MusicBrainzFailure.Timeout, "合成超时"); await model.RefreshAsync();
        Assert.Equal(MusicAlbumState.Failed, model.State); Assert.NotNull(model.SourceDetails); Assert.Null(model.Album);
    }
    [Fact] public async Task RapidAtoBtoARejectsLateOldSubjectAndAlbumResults()
    {
        var old = new TaskCompletionSource<MusicBrainzAlbumResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0; var reader = new Reader { Load = id => ++reads == 1 ? old.Task : Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(id), false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader);
        var first = model.OpenAsync(MusicTestData.Item("616")); await model.OpenAsync(MusicTestData.Item("617")); await model.OpenAsync(MusicTestData.Item("616"));
        old.SetResult(new(MusicTestData.Album() with { GroupTitle = "late" }, false, false, null)); await first;
        Assert.Equal("616", model.Item!.Id); Assert.NotEqual("late", model.Album!.Album.GroupTitle); Assert.Equal(MusicAlbumState.Ready, model.State);
    }
    [Fact] public async Task CancelAndDeactivateDoNotDisplayLatePreviewOrSaveSelection()
    {
        var reader = new Reader(); using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        var late = new TaskCompletionSource<MusicBrainzAlbumResult>(TaskCreationOptions.RunContinuationsAsynchronously); reader.Load = _ => late.Task;
        var preview = model.PreviewAsync(MusicTestData.Release); model.Cancel(); model.Deactivate();
        late.SetResult(new(MusicTestData.Album(), false, false, null)); await preview; Assert.Null(model.Preview);
        await model.ConfirmAsync(); Assert.Equal(0, reader.Confirmations); Assert.False(model.CanLoad);
    }
    [Fact] public async Task WrongReturnedReleaseCannotBeDisplayedOrConfirmed()
    {
        var reader = new Reader { Load = _ => Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(MusicTestData.Other), false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        Assert.Equal(MusicAlbumState.Failed, model.State); Assert.Null(model.Displayed);
    }
    [Fact] public async Task ReadonlyViewRejectsNonMusicEntityWithoutProviderWork()
    {
        var reader = new Reader(); using var model = new MusicAlbumViewModel(new Subjects(), reader);
        await model.OpenAsync(MusicTestData.Item() with { Kind = BangumiRelationKind.Anime }); Assert.Equal(MusicAlbumState.Failed, model.State); Assert.Equal(0, reader.Reads);
    }
    [Fact] public async Task ManualSearchNeverAutoBindsOrReplacesBridgeFacts()
    {
        var reader = new Reader(); using var model = new MusicAlbumViewModel(new Subjects(), reader);
        await model.OpenAsync(MusicTestData.Item()); var source = model.Source;
        await model.SearchAsync("Another album title"); Assert.Equal(MusicAlbumState.Choosing, model.State);
        Assert.Equal(source, model.Source); Assert.Null(model.Match!.AutomaticReleaseId); Assert.Equal(0, reader.Confirmations);
        await model.SearchAsync("https://untrusted.example/release/" + MusicTestData.Release); Assert.Contains("MusicBrainz", model.Message);
    }
    [Fact] public async Task ExistingAlbumCanBrowseGroupEditionsWithoutRereadingBangumi()
    {
        int subjects = 0; var source = new Subjects { Read = (id, _) => { subjects++; return Task.FromResult(new BangumiSubjectDetailsResult(MusicTestData.Details(id), false, false, null)); } };
        using var model = new MusicAlbumViewModel(source, new Reader()); await model.OpenAsync(MusicTestData.Item());
        await model.ChooseEditionAsync(); Assert.Equal(1, subjects); Assert.Equal(MusicAlbumState.Choosing, model.State); Assert.NotNull(model.Album);
    }
    [Fact] public async Task EditionPickerUsesPreviewedAlbumGroupWhenPreviewDiffersFromCurrentChoice()
    {
        var reader = new Reader { Match = _ => Task.FromResult(new MusicBrainzMatchResult(new([MusicTestData.Candidate(), MusicTestData.Candidate(MusicTestData.Other)], 2, false, null, null), MusicTestData.Release, null, false, false, null)),
            Load = id => Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(id) with { Release = MusicTestData.Candidate(id) with { ReleaseGroupId = id == MusicTestData.Other ? MusicTestData.Other : MusicTestData.Group } }, false, false, null)) };
        using var model = new MusicAlbumViewModel(new Subjects(), reader); await model.OpenAsync(MusicTestData.Item());
        await model.PreviewAsync(MusicTestData.Other); await model.ChooseEditionAsync(); Assert.Equal(MusicTestData.Other, reader.LastGroup);
    }
}
