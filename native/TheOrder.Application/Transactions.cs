using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public interface ILibraryCodec
{
    ImmutableArray<LibraryEntry> Decode(ReadOnlyMemory<byte> document);
    byte[] Encode(ImmutableArray<LibraryEntry> entries, string exportedAt);
    string EvidenceIdentity(LibraryEntry entry);
    bool ProtectsName(LibraryEntry entry);
    string InstallationKey(string path);
    WorkMetadata NormalizeWork(WorkMetadata work);
}

// A persistence implementation owns an exclusive profile lease for its entire lifetime.
public interface ILibraryPersistence : IDisposable
{
    byte[]? ReadMain();
    byte[]? ReadBackup();
    Task<PersistenceReceipt> SaveAsync(ReadOnlyMemory<byte> previous, ReadOnlyMemory<byte> candidate);
}
public sealed record PersistenceReceipt(string? Warning = null);
public enum LibraryRecovery { Main, Backup, New }
public sealed record LibrarySnapshot(ImmutableArray<LibraryEntry> Entries, LibraryRecovery Recovery);
public sealed record CommitResult<T>(T Value, bool CancellationAfterCommit, string? Warning);
public sealed record ImportCount(int Added, int Skipped);
public readonly record struct Change<T>(bool IsSpecified, T Value)
{
    public static implicit operator Change<T>(T value) => new(true, value);
}
public sealed record PersonalPatch
{
    public Change<string> Name { get; init; }
    public Change<string> Status { get; init; }
    public Change<double?> Rating { get; init; }
    public Change<bool> Favorite { get; init; }
    public Change<ImmutableArray<string>> Categories { get; init; }
    public Change<ImmutableArray<string>> Tags { get; init; }
    public Change<string> Notes { get; init; }
    public Change<string?> PreferredInstallationId { get; init; }
}
public sealed record RequestToken(Guid Epoch, string GameId, Guid Generation, long Request, long Revision);
public sealed class StaleLibraryRequestException() : InvalidOperationException("条目或请求已变化，请重新开始操作。");

// UI-independent transaction service. All state is immutable; only this queue publishes snapshots.
// Metadata fetching, directory authorization and game launch deliberately do not live here.
public sealed class TransactionalLibrary : IDisposable
{
    private readonly ILibraryCodec codec;
    private readonly ILibraryPersistence persistence;
    private readonly Func<string> now;
    private readonly Func<string> newId;
    private readonly SemaphoreSlim queue = new(1, 1);
    private readonly Guid epoch = Guid.NewGuid();
    private Dictionary<string, Lifecycle> lifecycles = [];
    private long revision, request;
    private LibrarySnapshot current;
    private byte[] lastGood;
    private bool disposed;
    private sealed record Lifecycle(Guid Generation, long Revision, long Request = 0, bool Pending = false);

    public LibrarySnapshot Snapshot => Volatile.Read(ref current);
    private TransactionalLibrary(ILibraryCodec codec, ILibraryPersistence persistence, Func<string> now, Func<string> newId,
        ImmutableArray<LibraryEntry> entries, LibraryRecovery recovery)
    {
        this.codec = codec; this.persistence = persistence; this.now = now; this.newId = newId;
        current = new(entries, recovery); lastGood = codec.Encode(entries, now());
        foreach (var e in entries) lifecycles[e.Id] = new(Guid.NewGuid(), ++revision);
    }
    public static TransactionalLibrary Open(ILibraryCodec codec, ILibraryPersistence persistence, Func<string>? clock = null, Func<string>? idFactory = null)
    {
        // Opening never writes, including recovery and the genuinely-new profile case.
        try
        {
            byte[]? main = null, backup = null;
            ImmutableArray<LibraryEntry> entries;
            Exception? mainError = null;
            try { main = persistence.ReadMain(); if (main is not null) return Create(codec.Decode(main), LibraryRecovery.Main); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { mainError = e; }
            try { backup = persistence.ReadBackup(); if (backup is not null) return Create(codec.Decode(backup), LibraryRecovery.Backup); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { throw new InvalidDataException("主文件及备份不可用；原文件已保留。", e); }
            if (main is not null || mainError is not null) throw new InvalidDataException("主文件不可用且没有有效备份；原文件已保留。", mainError);
            entries = []; return Create(entries, LibraryRecovery.New);
        }
        catch { persistence.Dispose(); throw; }
        TransactionalLibrary Create(ImmutableArray<LibraryEntry> e, LibraryRecovery r) => new(codec, persistence,
            clock ?? (() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")), idFactory ?? (() => Guid.NewGuid().ToString()), e, r);
    }

    private async Task<T> Enqueue<T>(Func<Task<T>> action, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); cancellation.ThrowIfCancellationRequested(); return await action().ConfigureAwait(false); }
        finally { queue.Release(); }
    }
    private Task<CommitResult<T>> Mutate<T>(Func<ImmutableArray<LibraryEntry>, (ImmutableArray<LibraryEntry> Entries, T Result)> operation, CancellationToken cancellation, string? forceNewGeneration = null)
        => Enqueue(async () =>
        {
            var previous = current.Entries;
            var (candidate, result) = operation(previous);
            // Round-trip validates and canonicalizes the complete candidate before any I/O.
            var serialized = codec.Encode(candidate, now());
            var validated = codec.Decode(serialized);
            serialized = codec.Encode(validated, now());
            // Prepare every allocating/validating lifecycle calculation before durable I/O too.
            // Once the primary switches, publishing the prepared state requires only assignments.
            var old = previous.ToDictionary(e => e.Id);
            var nextLifecycles = new Dictionary<string, Lifecycle>(lifecycles);
            var nextRevision = revision;
            foreach (var id in nextLifecycles.Keys.Except(validated.Select(e => e.Id)).ToArray()) nextLifecycles.Remove(id);
            foreach (var e in validated)
                if (e.Id == forceNewGeneration || !old.TryGetValue(e.Id, out var before) || codec.EvidenceIdentity(before) != codec.EvidenceIdentity(e))
                    nextLifecycles[e.Id] = new(Guid.NewGuid(), ++nextRevision);
            var nextSnapshot = new LibrarySnapshot(validated, LibraryRecovery.Main);
            cancellation.ThrowIfCancellationRequested();
            // No cancellation inside the durable phase. A post-replace warning is a committed result.
            var receipt = await persistence.SaveAsync(lastGood, serialized).ConfigureAwait(false);
            lifecycles = nextLifecycles; revision = nextRevision;
            lastGood = serialized;
            Volatile.Write(ref current, nextSnapshot);
            return new CommitResult<T>(result, cancellation.IsCancellationRequested, receipt.Warning);
        }, cancellation);

    public Task<CommitResult<ImportCount>> ImportAsync(ReadOnlyMemory<byte> backup, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var incoming = codec.Decode(backup); // Freeze/validate the whole input before it queues.
        return Mutate(games =>
        {
            var ids = games.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var workIds = games.Where(e => e.Metadata is not null).Select(e => e.Metadata!.Id).ToHashSet(StringComparer.Ordinal);
            var folders = games.SelectMany(e => e.Installations).Select(i => codec.InstallationKey(i.Folder)).ToHashSet(StringComparer.Ordinal);
            var result = games.ToBuilder(); int added = 0, skipped = 0;
            foreach (var e in incoming)
            {
                var keys = e.Installations.Select(i => codec.InstallationKey(i.Folder)).ToArray();
                if (ids.Contains(e.Id) || e.Metadata is { } m && workIds.Contains(m.Id) || keys.Any(folders.Contains)) { skipped++; continue; }
                result.Add(e); ids.Add(e.Id); if (e.Metadata is not null) workIds.Add(e.Metadata.Id);
                folders.UnionWith(keys); added++;
            }
            return (result.ToImmutable(), new ImportCount(added, skipped));
        }, cancellation);
    }
    public Task<CommitResult<LibraryEntry>> AddAsync(WorkMetadata work, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        work = codec.NormalizeWork(work);
        return Mutate(games =>
        {
            var existing = games.FirstOrDefault(e => e.Metadata?.Id == work.Id);
            if (existing is not null) return (games, existing);
            var e = new LibraryEntry(newId(), work.Title, false, work, [], null, "planned", null, false, [], [], "", 0, 0, [], now(), null, null, null, new(false, null));
            return (games.Add(e), e);
        }, cancellation);
    }
    public Task<CommitResult<LibraryEntry>> UpdateAsync(string id, PersonalPatch p, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            var e = Find(games, id);
            var changed = e with
            {
                Name = p.Name.IsSpecified ? p.Name.Value : e.Name, NameIsCustom = p.Name.IsSpecified ? true : e.NameIsCustom,
                Status = p.Status.IsSpecified ? p.Status.Value : e.Status, PersonalRating = p.Rating.IsSpecified ? p.Rating.Value : e.PersonalRating,
                Favorite = p.Favorite.IsSpecified ? p.Favorite.Value : e.Favorite, Categories = p.Categories.IsSpecified ? p.Categories.Value : e.Categories,
                CustomTags = p.Tags.IsSpecified ? p.Tags.Value : e.CustomTags, Notes = p.Notes.IsSpecified ? p.Notes.Value : e.Notes,
                PreferredInstallationId = p.PreferredInstallationId.IsSpecified ? p.PreferredInstallationId.Value : e.PreferredInstallationId
            };
            // Legacy patch normalizes these fields before returning the operation result.
            changed = codec.Decode(codec.Encode([changed], now()))[0];
            return (games.Replace(e, changed), changed);
        }, cancellation);
    public Task<CommitResult<bool>> DeleteAsync(string id, CancellationToken cancellation = default)
        => Mutate(games => (games.Remove(Find(games, id)), true), cancellation);
    // Installation is an already-authorized value, never an instruction to scan/open/execute a path.
    public Task<CommitResult<LibraryEntry>> AddInstallationAsync(string id, Installation installation, RequestToken token, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            AssertToken(id, token); var e = Find(games, id);
            var key = codec.InstallationKey(installation.Folder);
            foreach (var known in games.SelectMany(g => g.Installations).Select(i => codec.InstallationKey(i.Folder)))
                if (key == known || key.StartsWith(known.TrimEnd('\\') + "\\", StringComparison.Ordinal) || known.StartsWith(key.TrimEnd('\\') + "\\", StringComparison.Ordinal))
                    throw new InvalidDataException("安装目录重复或范围重叠。");
            var preferred = e.Installations.Any(i => i.Id == e.PreferredInstallationId && i.Executable is not null) ? e.PreferredInstallationId : installation.Id;
            var changed = e with { Installations = e.Installations.Add(installation), PreferredInstallationId = preferred };
            return (games.Replace(e, changed), changed);
        }, cancellation);
    // Explicit Phase 2 addition: removing a record never deletes game files.
    public Task<CommitResult<LibraryEntry>> RemoveInstallationAsync(string id, string installationId, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            var e = Find(games, id); var location = e.Installations.FirstOrDefault(i => i.Id == installationId) ?? throw new InvalidDataException("安装不存在。");
            var changed = e with { Installations = e.Installations.Remove(location), PreferredInstallationId = e.PreferredInstallationId == installationId ? null : e.PreferredInstallationId };
            return (games.Replace(e, changed), changed);
        }, cancellation);

    public Task<RequestToken> BeginRequestAsync(string id, CancellationToken cancellation = default)
        => Enqueue(() =>
        {
            Find(current.Entries, id); var state = lifecycles[id];
            state = state with { Request = ++request, Revision = ++revision, Pending = true }; lifecycles[id] = state;
            return Task.FromResult(new RequestToken(epoch, id, state.Generation, state.Request, state.Revision));
        }, cancellation);
    public Task<bool> FinishRequestAsync(RequestToken token, CancellationToken cancellation = default)
        => Enqueue(() =>
        {
            if (token.Epoch == epoch && lifecycles.TryGetValue(token.GameId, out var s) && s.Generation == token.Generation && s.Request == token.Request)
                lifecycles[token.GameId] = s with { Pending = false };
            return Task.FromResult(true);
        }, cancellation);
    private void AssertToken(string id, RequestToken token)
    {
        if (token.Epoch != epoch || token.GameId != id || !lifecycles.TryGetValue(id, out var s) || s.Generation != token.Generation || s.Request != token.Request || !s.Pending)
            throw new StaleLibraryRequestException();
    }
    public Task<CommitResult<LibraryEntry>> LinkAsync(string id, WorkMetadata work, bool preserveName = false, bool preserveMergedName = false, RequestToken? token = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        work = codec.NormalizeWork(work);
        return Mutate(games =>
        {
            if (token is not null) AssertToken(id, token);
            var source = Find(games, id); var target = games.FirstOrDefault(e => e.Id != id && e.Metadata?.Id == work.Id);
            if (target is not null && lifecycles.TryGetValue(target.Id, out var life) && (life.Pending || token is not null && life.Revision > token.Revision)) throw new StaleLibraryRequestException();
            var keep = preserveName || codec.ProtectsName(source);
            var mergedName = preserveMergedName && !keep && target?.Metadata is not null && codec.ProtectsName(target) ? target.Name : null;
            var e = source with { CoverChoice = source.CoverChoice?.WorkId == work.Id ? source.CoverChoice : null };
            if (target is not null)
            {
                var sessions = e.Sessions.ToBuilder(); long duplicateSeconds = 0, duplicateCount = 0;
                foreach (var play in target.Sessions)
                {
                    var previous = sessions.FirstOrDefault(s => s.Id == play.Id);
                    if (previous is null) { sessions.Add(play); continue; }
                    if (previous.StartedAt != play.StartedAt) throw new InvalidDataException("游玩记录编号冲突，未合并。");
                    duplicateSeconds += Math.Min(previous.Seconds, play.Seconds);
                    if (previous.Seconds > 0 && play.Seconds > 0) duplicateCount++;
                    if (play.Seconds > previous.Seconds) sessions[sessions.IndexOf(previous)] = play;
                }
                var inheritCover = e.LocalCover is null && e.CoverChoice is null;
                e = e with
                {
                    Installations = e.Installations.AddRange(target.Installations), PreferredInstallationId = e.PreferredInstallationId ?? target.PreferredInstallationId,
                    Categories = [.. e.Categories.Concat(target.Categories).Distinct(StringComparer.Ordinal)], CustomTags = [.. e.CustomTags.Concat(target.CustomTags).Distinct(StringComparer.Ordinal)],
                    Favorite = e.Favorite || target.Favorite, PersonalRating = e.PersonalRating ?? target.PersonalRating, Status = e.Status == "planned" ? target.Status : e.Status,
                    Notes = target.Notes.Length > 0 && target.Notes != e.Notes ? string.Join("\n\n", new[] { e.Notes, target.Notes }.Where(s => s.Length > 0)) : e.Notes,
                    Sessions = sessions.ToImmutable(), TotalPlaySeconds = e.TotalPlaySeconds + target.TotalPlaySeconds - duplicateSeconds, PlayCount = e.PlayCount + target.PlayCount - duplicateCount,
                    LastPlayedAt = target.LastPlayedAt is not null && (e.LastPlayedAt is null || string.CompareOrdinal(target.LastPlayedAt, e.LastPlayedAt) > 0) ? target.LastPlayedAt : e.LastPlayedAt,
                    LocalCover = inheritCover ? target.LocalCover : e.LocalCover,
                    CoverChoice = inheritCover && target.LocalCover is null && target.CoverChoice?.WorkId == work.Id ? target.CoverChoice : e.CoverChoice,
                    CoverDisplay = e.CoverDisplay.IsSpecified ? e.CoverDisplay : target.CoverDisplay
                };
                games = games.Remove(target);
            }
            e = e with { Metadata = work, Name = mergedName ?? (keep ? e.Name : work.Title), NameIsCustom = mergedName is not null || keep };
            return (games.Replace(source, e), e);
        }, cancellation, id);
    }
    private static LibraryEntry Find(ImmutableArray<LibraryEntry> entries, string id) => entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidDataException("游戏不存在。");
    public void Dispose()
    {
        // The owner must stop scheduling before disposal. Wait for an in-flight durable commit.
        queue.Wait(); try { if (disposed) return; disposed = true; persistence.Dispose(); } finally { queue.Release(); }
    }
}
