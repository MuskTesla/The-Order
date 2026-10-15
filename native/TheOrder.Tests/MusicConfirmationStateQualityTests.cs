using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicConfirmationStateQualityTests
{
    [Fact]
    public async Task FirstConfirmationImmediatelyRetainsSavedTargetForRevocation()
    {
        var reader = new Reader(); using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item());
        await model.PreviewAsync(MusicTestData.Release); await model.ConfirmAsync();
        Assert.Equal(MusicTestData.Release, reader.Saved);
        Assert.Equal(MusicTestData.Release, model.Match?.ConfirmedReleaseId);
        Assert.True(model.CanRevoke);
    }

    [Fact]
    public async Task ReplacingConfirmedEditionRevokesTheNewTargetWithoutRefresh()
    {
        var reader = new Reader { Saved = MusicTestData.Release }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item()); await model.ChooseEditionAsync();
        await model.PreviewAsync(MusicTestData.Other); await model.ConfirmAsync();
        Assert.Equal(MusicTestData.Other, model.Match?.ConfirmedReleaseId);
        await model.RevokeAsync();
        Assert.Equal(MusicTestData.Other, reader.Revoked);
        Assert.Null(reader.Saved); Assert.False(model.CanRevoke);
    }

    [Fact]
    public async Task ManualSearchAndUnavailablePreviewRetainSavedTargetForRevocation()
    {
        var reader = new Reader { Saved = MusicTestData.Release }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item());
        await model.SearchAsync("different title");
        reader.Unavailable = true; await model.PreviewAsync(MusicTestData.Other);
        Assert.Null(model.Displayed); Assert.True(model.CanRevoke);
        await model.RevokeAsync();
        Assert.Equal(MusicTestData.Release, reader.Revoked); Assert.Null(reader.Saved);
    }

    [Fact]
    public async Task PendingRejectionDisablesConfirmSearchAndSecondRejection()
    {
        var reader = new Reader { RejectGate = NewGate() }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item()); await model.PreviewAsync(MusicTestData.Release);
        var pending = model.RejectAsync();
        try
        {
            Assert.True(model.IsBusy); Assert.True(model.CanCancel); Assert.False(model.CanConfirm); Assert.False(model.CanLoad);
            await model.ConfirmAsync(); await model.SearchAsync("other"); await model.RejectAsync();
            Assert.Equal(0, reader.Confirmations); Assert.Equal(1, reader.Rejections); Assert.Equal(1, reader.Matches);
        }
        finally { reader.RejectGate.SetResult(); await pending; }
        Assert.False(model.IsBusy); Assert.Equal(MusicAlbumState.Choosing, model.State);
    }

    [Fact]
    public async Task PendingRevocationCanBeCancelledAndCannotStartAnotherDecision()
    {
        var reader = new Reader { Saved = MusicTestData.Release, RevokeGate = NewGate() }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item()); var pending = model.RevokeAsync();
        try
        {
            Assert.True(model.IsBusy); Assert.True(model.CanCancel); Assert.False(model.CanRevoke);
            await model.RevokeAsync(); Assert.Equal(1, reader.Revocations);
            model.Cancel(); Assert.Equal(MusicAlbumState.Cancelled, model.State); Assert.False(model.IsBusy);
        }
        finally { reader.RevokeGate.SetResult(); await pending; }
        Assert.Equal(MusicTestData.Release, reader.Saved);
        Assert.True(model.CanRevoke); Assert.Equal(MusicAlbumState.Cancelled, model.State);
    }

    [Fact]
    public async Task CancelledOldRejectionCannotClearTheNewSource()
    {
        var reader = new Reader { RejectGate = NewGate() }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item()); await model.PreviewAsync(MusicTestData.Release);
        var pending = model.RejectAsync(); await model.OpenAsync(MusicTestData.Item("617"));
        reader.RejectGate.SetResult(); await pending;
        Assert.Equal("617", model.Source?.BangumiId); Assert.Equal(MusicAlbumState.Choosing, model.State);
        Assert.NotNull(model.Match); Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task LatePostRejectionSearchCannotReplaceTheNewSourceMatch()
    {
        var gate = new TaskCompletionSource<MusicBrainzMatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { PendingSecondMatch = gate }; using var model = Model(reader);
        await model.OpenAsync(MusicTestData.Item()); await model.PreviewAsync(MusicTestData.Release);
        var pending = model.RejectAsync(); await model.OpenAsync(MusicTestData.Item("617"));
        var current = model.Match;
        gate.SetResult(new(new([], 0, false, null, null), null, MusicTestData.Other, false, false, null)); await pending;
        Assert.Same(current, model.Match); Assert.Equal("617", model.Source?.BangumiId);
        Assert.Null(model.SavedUserTargetId); Assert.Equal(MusicAlbumState.Choosing, model.State);
    }

    private static MusicAlbumViewModel Model(Reader reader) => new(new Subjects(), reader);
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Subjects : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string id, BangumiRelationKind kind, bool refresh, CancellationToken token)
            => Task.FromResult(new BangumiSubjectDetailsResult(MusicTestData.Details(id), false, false, null));
    }
    private sealed class Reader : IMusicBrainzReader
    {
        public string? Saved, Revoked;
        public bool Unavailable;
        public int Confirmations, Rejections, Revocations, Matches;
        public TaskCompletionSource? RejectGate, RevokeGate;
        public TaskCompletionSource<MusicBrainzMatchResult>? PendingSecondMatch;
        private static MusicBrainzSearchResult Search() => new([MusicTestData.Candidate(), MusicTestData.Candidate(MusicTestData.Other)], 2, false, null, null);
        public Task<MusicBrainzMatchResult> MatchAsync(MusicAlbumSource source, bool refresh, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Matches++; if (Matches == 2 && PendingSecondMatch is not null) return PendingSecondMatch.Task;
            return Task.FromResult(new MusicBrainzMatchResult(Search(), null, Saved, false, false, null) { ConfirmedReleaseGroupId = Saved is null ? null : MusicTestData.Group }); }
        public Task<MusicBrainzAlbumResult> LoadAlbumAsync(string id, bool refresh, CancellationToken token)
            => Unavailable ? Task.FromException<MusicBrainzAlbumResult>(new MusicBrainzException(MusicBrainzFailure.Unavailable, "Synthetic removed release"))
                : Task.FromResult(new MusicBrainzAlbumResult(MusicTestData.Album(id), false, false, null));
        public Task<MusicBrainzSearchResult> EditionsAsync(string id, CancellationToken token) => Task.FromResult(Search());
        public Task ConfirmAsync(MusicAlbumSource source, MusicBrainzAlbum album, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Confirmations++; Saved = album.Release.Id; return Task.CompletedTask; }
        public async Task RejectAsync(MusicAlbumSource source, MusicBrainzReleaseCandidate release, CancellationToken token)
        { Rejections++; if (RejectGate is not null) await RejectGate.Task; token.ThrowIfCancellationRequested(); if (Saved == release.Id) Saved = null; }
        public async Task RevokeAsync(MusicAlbumSource source, string expectedId, CancellationToken token)
        { Revocations++; if (RevokeGate is not null) await RevokeGate.Task; token.ThrowIfCancellationRequested(); if (Saved != expectedId) throw new IOException("Changed saved identity"); Revoked = expectedId; Saved = null; }
    }
}
