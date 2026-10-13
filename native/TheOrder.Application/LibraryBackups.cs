using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public enum LibraryRestoreMode { AddMissing, ReplaceLibrary }
public sealed class LibraryRestorePreview
{
    internal LibraryRestorePreview(Guid owner, LibrarySnapshot baseline, ImmutableArray<LibraryEntry> incoming,
        LibraryRestoreMode mode, int added, int skipped)
    { Owner = owner; Baseline = baseline; Incoming = incoming; Mode = mode; Added = added; Skipped = skipped; }
    internal Guid Owner { get; }
    internal LibrarySnapshot Baseline { get; }
    internal ImmutableArray<LibraryEntry> Incoming { get; }
    public LibraryRestoreMode Mode { get; }
    public int CurrentCount => Baseline.Entries.Length;
    public int BackupCount => Incoming.Length;
    public int Added { get; }
    public int Skipped { get; }
    public int ResultCount => Mode == LibraryRestoreMode.ReplaceLibrary ? BackupCount : CurrentCount + Added;
    public ImmutableArray<string> Names => [.. Incoming.Take(12).Select(e => e.Name)];
}
public sealed record LibraryBackupSettings(bool Automatic = true);
public sealed record LibraryBackupRecord(string Id, DateTimeOffset CreatedAt, string Reason, int Entries, string Digest);
public sealed record LibraryBackupState(LibraryBackupSettings Settings, ImmutableArray<LibraryBackupRecord> Records);
public sealed record LibraryBackupWrite(LibraryBackupRecord Record, string? Warning = null);
public interface ILibraryBackupStore
{
    Task<LibraryBackupState> ReadStateAsync(CancellationToken cancellation);
    Task SaveSettingsAsync(LibraryBackupSettings settings, CancellationToken cancellation);
    Task<LibraryBackupWrite> SaveSnapshotAsync(ReadOnlyMemory<byte> document, int entries, string reason, CancellationToken cancellation);
    Task<byte[]> ReadSnapshotAsync(string id, CancellationToken cancellation);
    Task<byte[]> ReadExternalAsync(string path, CancellationToken cancellation);
    Task<string?> WriteExternalAsync(string path, ReadOnlyMemory<byte> document, CancellationToken cancellation);
}

public sealed partial class TransactionalLibrary
{
    public Task<LibraryRestorePreview> PreviewRestoreAsync(ReadOnlyMemory<byte> document, LibraryRestoreMode mode,
        CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(mode)) throw new InvalidDataException("恢复方式无效。");
        var bytes = document.ToArray();
        return Enqueue(() =>
        {
            if (current.Access != LibraryAccess.ValidWritable) throw new LibraryReadOnlyException(current.ReadOnlyReason!);
            var incoming = codec.Decode(bytes).Select(PortableSessionHistory).ToImmutableArray();
            var plan = AddMissing(current.Entries, incoming);
            return Task.FromResult(new LibraryRestorePreview(epoch, current, incoming, mode, plan.Count.Added, plan.Count.Skipped));
        }, cancellation);
    }
    public Task<CommitResult<int>> RestoreAsync(LibraryRestorePreview preview,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, Task> saveRollback, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(saveRollback);
        byte[] rollback = []; int previousCount = 0;
        return Mutate(games =>
        {
            // Preview consent is tied to the exact snapshot and writer, including personal edits and sessions.
            if (preview.Owner != epoch || !ReferenceEquals(preview.Baseline, current)) throw new StaleLibraryRequestException();
            if (preview.Mode == LibraryRestoreMode.ReplaceLibrary
                && (pendingOwnedSessions.Count != 0 || games.Any(e => e.Sessions.Any(s => s.Native is { IsTerminal: false }))))
                throw new SessionConflictException("请先结束计时并处理待恢复的游玩会话，再恢复整份书架。");
            var next = preview.Mode == LibraryRestoreMode.ReplaceLibrary ? preview.Incoming : AddMissing(games, preview.Incoming).Entries;
            rollback = codec.Encode([.. games.Select(PortableSessionHistory)], now()); previousCount = games.Length;
            return (next, next.Length);
        }, cancellation, verifyBeforeCommit: () => saveRollback(rollback, previousCount, cancellation),
            renewGenerations: preview.Mode == LibraryRestoreMode.ReplaceLibrary);
    }
    private (ImmutableArray<LibraryEntry> Entries, ImportCount Count) AddMissing(ImmutableArray<LibraryEntry> games,
        ImmutableArray<LibraryEntry> incoming)
    {
        var ids = games.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var works = games.Where(e => e.Metadata is not null).Select(e => e.Metadata!.Id).ToHashSet(StringComparer.Ordinal);
        var folders = games.SelectMany(e => e.Installations).Select(i => codec.InstallationKey(i.Folder)).ToHashSet(StringComparer.Ordinal);
        var result = games.ToBuilder(); int added = 0, skipped = 0;
        foreach (var e in incoming)
        {
            var keys = e.Installations.Select(i => codec.InstallationKey(i.Folder)).ToArray();
            if (ids.Contains(e.Id) || e.Metadata is { } m && works.Contains(m.Id) || keys.Any(folders.Contains)) { skipped++; continue; }
            result.Add(e); ids.Add(e.Id); if (e.Metadata is not null) works.Add(e.Metadata.Id); folders.UnionWith(keys); added++;
        }
        return (result.ToImmutable(), new(added, skipped));
    }
}
