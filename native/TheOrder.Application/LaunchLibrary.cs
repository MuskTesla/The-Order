namespace TheOrder.Application;

public sealed partial class TransactionalLibrary : ILaunchLibrary
{
    public Task<LaunchEntry> CaptureLaunchAsync(string id, CancellationToken cancellation = default) => Enqueue(() =>
    {
        var entry = Find(current.Entries, id);
        return Task.FromResult(new LaunchEntry(new(epoch, launchIncarnations[id], id), entry.Installations, entry.PreferredInstallationId));
    }, cancellation);

    public Task<T> WithLaunchAdmissionAsync<T>(GameLaunchIdentity identity, string installationId, Func<T> create, CancellationToken cancellation = default)
        => Enqueue(() =>
        {
            if (identity.Epoch != epoch || !launchIncarnations.TryGetValue(identity.GameId, out var incarnation) || incarnation != identity.Incarnation
                || !current.Entries.Any(g => g.Id == identity.GameId && g.Installations.Any(i => i.Id == installationId)))
                throw new LaunchValidationException(LaunchOutcome.StaleInstallation, "作品或安装关系已失效，请重新打开详情。");
            cancellation.ThrowIfCancellationRequested();
            // No library mutation or playtime side effect. Once creation succeeds cancellation cannot undo it.
            return Task.FromResult(create());
        }, cancellation);
}

public sealed class OfflineLaunchLibrary(OfflineLibraryClient client) : ILaunchLibrary
{
    public Task<LaunchEntry> CaptureLaunchAsync(string id, CancellationToken cancellation) => client.Required().CaptureLaunchAsync(id, cancellation);
    public Task<T> WithLaunchAdmissionAsync<T>(GameLaunchIdentity identity, string installationId, Func<T> create, CancellationToken cancellation)
        => client.Required().WithLaunchAdmissionAsync(identity, installationId, create, cancellation);
}
