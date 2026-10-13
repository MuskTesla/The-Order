using TheOrder.Application;
using System.Collections.Immutable;

namespace TheOrder.Application;

public enum PreflightStatus
{
    Valid, NotFound, AccessDenied, FileInsteadOfDirectory, UnsupportedReparseCase,
    DuplicateExistingInstall, SameTargetViaAlias, BrokenLink, IdentityUnavailable, OtherIOError, DuplicateIndeterminate
}
// An observation of this machine/volume, never a portable identifier persisted in legacy JSON.
public sealed record FilesystemIdentity(string Volume, string FileId);
public sealed record PathObservation(string StoredPath, string DisplayPath, string FinalPath,
    FilesystemIdentity Identity, bool IsDirectory, bool ViaReparsePoint, uint ReparseTag)
{
    public ImmutableArray<string> ReparsePaths { get; init; } = [];
}
public sealed record PathPreflight(PreflightStatus Status, PathObservation? Observation, string Message, int NativeError = 0)
{
    public bool IsValid => Status == PreflightStatus.Valid && Observation is not null;
}
public interface IFilesystemIdentity
{
    // Blocking OS calls: Application invokes this only on background workers, including the transaction queue.
    PathPreflight Inspect(string storedPath, bool requireDirectory = true);
}
public sealed record ExistingInstallation(string GameId, string GameName, string InstallationId, string Folder);
public sealed record ObservedInstallation(ExistingInstallation Installation, PathPreflight Path);
public enum DuplicateStatus { NotChecked, ConfirmedUnique, ConfirmedDuplicate, Indeterminate }
public sealed class CandidateStaleException() : Exception(UiText.Get("候选身份在核对后已变化，请重新扫描或重新核对；未提交任何安装。"));
public sealed record InstallationPreflight(PathPreflight Path, ExistingInstallation? Duplicate = null, bool Overlap = false)
{
    public ImmutableArray<ObservedInstallation> Unresolved { get; init; } = [];
    public DuplicateStatus DuplicateStatus => Duplicate is not null ? DuplicateStatus.ConfirmedDuplicate
        : Path.Status == PreflightStatus.DuplicateIndeterminate ? DuplicateStatus.Indeterminate
        : Path.IsValid ? DuplicateStatus.ConfirmedUnique : DuplicateStatus.NotChecked;
    public bool CanAttach => Path.IsValid && DuplicateStatus == DuplicateStatus.ConfirmedUnique;
}

public sealed class InstallationPreflightService(IFilesystemIdentity filesystem)
{
    public Task<InstallationPreflight> CheckAsync(string path, ImmutableArray<ExistingInstallation> existing,
        CancellationToken cancellation = default) => Task.Run(() => Check(path, existing, cancellation), cancellation);

    public InstallationPreflight Check(string path, ImmutableArray<ExistingInstallation> existing, CancellationToken cancellation)
        => Check(path, Observe(existing, cancellation), cancellation);
    public ImmutableArray<ObservedInstallation> Observe(IEnumerable<ExistingInstallation> existing, CancellationToken cancellation)
        => [.. existing.Select(i => { cancellation.ThrowIfCancellationRequested(); return new ObservedInstallation(i, filesystem.Inspect(i.Folder)); })];
    public InstallationPreflight Check(string path, IEnumerable<ObservedInstallation> existing, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var candidate = filesystem.Inspect(path);
        if (!candidate.IsValid) return new(candidate);
        var unresolved = ImmutableArray.CreateBuilder<ObservedInstallation>();
        foreach (var record in existing)
        {
            cancellation.ThrowIfCancellationRequested();
            var known = record.Installation; var other = record.Path;
            // An unavailable historical folder remains in the library. Identical stored strings are still duplicates.
            var sameString = string.Equals(candidate.Observation!.DisplayPath, Normalize(known.Folder), StringComparison.OrdinalIgnoreCase);
            if (sameString && !other.IsValid || other.IsValid && other.Observation!.Identity == candidate.Observation.Identity)
                return new(candidate with { Status = sameString ? PreflightStatus.DuplicateExistingInstall : PreflightStatus.SameTargetViaAlias,
                    Message = UiText.Format($"此目录已属于「{known.GameName}」，请核对已有安装。") }, known);
            if (other.IsValid && (Within(candidate.Observation.FinalPath, other.Observation!.FinalPath) || Within(other.Observation.FinalPath, candidate.Observation.FinalPath)))
                return new(candidate with { Status = PreflightStatus.DuplicateExistingInstall, Message = UiText.Format($"目录与「{known.GameName}」的安装范围重叠。") }, known, true);
            if (!other.IsValid) unresolved.Add(record);
        }
        if (unresolved.Count > 0)
            return new(candidate with { Status = PreflightStatus.DuplicateIndeterminate,
                Message = UiText.Get("无法确认是否重复，未允许自动导入。请恢复以下历史安装的访问后重新检查：\n") +
                    string.Join("\n", unresolved.Take(3).Select(r => $"{r.Installation.Folder} · {r.Path.Message}")) +
                    (unresolved.Count > 3 ? UiText.Format($"\n另有 {unresolved.Count - 3} 个无法确认的安装。") : "") }) { Unresolved = unresolved.ToImmutable() };
        return new(candidate);
    }
    private static string Normalize(string path) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; } }
    public static bool Within(string child, string parent) => child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static bool SameObservation(PathObservation original, PathObservation current) =>
        original.Identity == current.Identity && string.Equals(original.FinalPath, current.FinalPath, StringComparison.OrdinalIgnoreCase)
        && original.IsDirectory == current.IsDirectory && original.ViaReparsePoint == current.ViaReparsePoint
        && original.ReparseTag == current.ReparseTag && original.ReparsePaths.SequenceEqual(current.ReparsePaths, StringComparer.OrdinalIgnoreCase);
    public void Revalidate(PathObservation snapshot, bool reviewedCandidate = false)
    {
        var actual = filesystem.Inspect(snapshot.StoredPath, snapshot.IsDirectory);
        if (!actual.IsValid || !SameObservation(snapshot, actual.Observation!))
        {
            if (reviewedCandidate) throw new CandidateStaleException();
            throw new InvalidDataException(UiText.Get("目录在检查后发生变化，未提交安装关系。请重新扫描或选择。"));
        }
    }
}
