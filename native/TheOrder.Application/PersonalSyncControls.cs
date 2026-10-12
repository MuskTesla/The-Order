namespace TheOrder.Application;

public sealed partial class PersonalSyncCoordinator
{
    public async Task PauseAutomaticAsync(CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var fence = await auth.AuthContext.CaptureAsync(cancellation).ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, fence.Changed);
            var state = await CurrentAsync(stop.Token).ConfigureAwait(false);
            await SaveFenced(state with { Settings = state.Settings with { Automatic = false } }, fence, stop.Token).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    public async Task UnmapFolderAsync(string folderId, CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var fence = await auth.AuthContext.CaptureAsync(cancellation).ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, fence.Changed);
            var state = await CurrentAsync(stop.Token).ConfigureAwait(false);
            if (state.Pending.Any(x => x.FolderId == folderId)) throw new OnlineException(OnlineFailure.Conflict);
            await SaveFenced(state with { Folders = state.Folders.Where(x => x.FolderId != folderId).ToArray() }, fence, stop.Token).ConfigureAwait(false);
            active = null; activeState = null; activeRemote = null;
        }
        finally { serial.Release(); }
    }
}
