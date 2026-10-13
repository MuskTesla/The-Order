using System.Security.Cryptography;

namespace TheOrder.Application;

public sealed class LibraryBackupService(OfflineLibraryClient library, ILibraryBackupStore store, ILibraryCodec codec,
    TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(1);
    public static string ContentDigest(ILibraryCodec codec, ReadOnlyMemory<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(codec.Encode(codec.Decode(bytes), "2000-01-01T00:00:00.000Z")));
    public Task<LibraryBackupState> ReadStateAsync(CancellationToken cancellation) => store.ReadStateAsync(cancellation);
    public Task SaveSettingsAsync(bool automatic, CancellationToken cancellation) => store.SaveSettingsAsync(new(automatic), cancellation);
    public async Task<string?> ExportAsync(string path, CancellationToken cancellation)
    {
        var bytes = await library.ExportBackupAsync(cancellation).ConfigureAwait(false);
        return await store.WriteExternalAsync(path, bytes, cancellation).ConfigureAwait(false);
    }
    public async Task<LibraryRestorePreview> PreviewAsync(string pathOrId, bool savedSnapshot, LibraryRestoreMode mode, CancellationToken cancellation)
    {
        var bytes = savedSnapshot ? await store.ReadSnapshotAsync(pathOrId, cancellation).ConfigureAwait(false)
            : await store.ReadExternalAsync(pathOrId, cancellation).ConfigureAwait(false);
        return await library.PreviewRestoreAsync(bytes, mode, cancellation).ConfigureAwait(false);
    }
    public Task<ClientResult> RestoreAsync(LibraryRestorePreview preview, CancellationToken cancellation)
        => library.RestoreBackupAsync(preview, async (bytes, count, token) =>
        {
            var rollback = await store.SaveSnapshotAsync(bytes, count, "恢复前", token).ConfigureAwait(false);
            // A checkpoint must be verified, including a warning after its durable write, before replacement.
            _ = await store.ReadSnapshotAsync(rollback.Record.Id, token).ConfigureAwait(false);
        }, cancellation);
    public async Task<LibraryBackupWrite?> AutomaticAsync(bool onExit, CancellationToken cancellation)
    {
        if (!library.Capability.CanEdit || library.CurrentSnapshot is not { Entries.IsEmpty: false }) return null;
        var state = await store.ReadStateAsync(cancellation).ConfigureAwait(false);
        if (!state.Settings.Automatic) return null;
        var newest = state.Records.FirstOrDefault();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!onExit && newest is not null && now - newest.CreatedAt < AutomaticInterval) return null;
        var bytes = await library.ExportBackupAsync(cancellation).ConfigureAwait(false);
        if (newest?.Digest == ContentDigest(codec, bytes)) return null;
        return await store.SaveSnapshotAsync(bytes, codec.Decode(bytes).Length, "自动", cancellation).ConfigureAwait(false);
    }
}
