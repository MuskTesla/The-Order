using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace TheOrder.Application;

public sealed record ScanLimits(int MaxDepth = 6, int MaxDirectories = 5000, int MaxEntries = 50000, int MaxCandidates = 2000);
public enum ScanStatus { Completed, NoCandidates, Cancelled, Failed }
public sealed record DirectoryItem(string Path, string Name, bool IsDirectory, bool IsReparsePoint);
public interface IScanFilesystem
{
    IEnumerable<DirectoryItem> Enumerate(string directory);
    bool IsForbiddenRoot(PathObservation root);
}
public sealed record ScanProgress(int Directories, int Entries, int Candidates, string CurrentDirectory);
public sealed record ScanCandidate(string Id, string Name, PathObservation Folder, ImmutableArray<string> Executables,
    string Evidence, InstallationPreflight Preflight)
{
    public override string ToString() => $"{Name} · {Preflight.Path.Message}";
}
public sealed record ScanResult(ScanStatus Status, ImmutableArray<ScanCandidate> Candidates, ScanProgress Progress, ImmutableArray<string> Warnings, string Message);

public sealed partial class BoundedScanner(IFilesystemIdentity identity, IScanFilesystem filesystem)
{
    public static bool IsGameExecutable(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !ExcludedExecutable().IsMatch(name);
    public static readonly ImmutableHashSet<string> SkippedDirectories = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "node_modules", ".git", ".svn", ".potatovn", "$recycle.bin", "system volume information", "windows", "redist", "_commonredist", "汉化补丁", "补丁", "patches", "_patch", "backup", "beifen");
    public Task<ScanResult> ScanAsync(string root, ImmutableArray<ExistingInstallation> existing, ScanLimits? limits = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default)
        => Task.Run(() => Scan(root, existing, limits ?? new(), progress, cancellation));
    private ScanResult Scan(string root, ImmutableArray<ExistingInstallation> existing, ScanLimits limits, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        var candidates = ImmutableArray.CreateBuilder<ScanCandidate>(); var warnings = ImmutableArray.CreateBuilder<string>();
        var current = new ScanProgress(0, 0, 0, root);
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (limits.MaxDepth is < 0 or > 6 || limits.MaxDirectories is < 1 or > 5000 || limits.MaxEntries is < 1 or > 50000 || limits.MaxCandidates is < 1 or > 2000)
                throw new InvalidDataException("扫描上限不在允许范围内。");
            var probe = identity.Inspect(root); if (!probe.IsValid) throw new InvalidDataException(probe.Message);
            if (filesystem.IsForbiddenRoot(probe.Observation!) || PatchDirectory().IsMatch(probe.Observation!.FinalPath))
                throw new InvalidDataException("请选择具体游戏目录；不能扫描磁盘根目录、系统目录或补丁备份目录。");
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
            var visited = new HashSet<FilesystemIdentity>(); var preflight = new InstallationPreflightService(identity);
            var observed = preflight.Observe(existing, cancellation);
            while (queue.TryDequeue(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                current = current with { Directories = current.Directories + 1, CurrentDirectory = item.Path };
                if (current.Directories > limits.MaxDirectories || candidates.Count >= limits.MaxCandidates) throw new InvalidDataException("扫描范围达到上限，请选择更具体的目录。");
                var directory = identity.Inspect(item.Path);
                if (!directory.IsValid) { Warn(directory.Message); continue; }
                if (!visited.Add(directory.Observation!.Identity)) continue;
                var executables = new List<string>(); var subdirectories = new List<string>();
                try
                {
                    foreach (var entry in filesystem.Enumerate(item.Path))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        current = current with { Entries = current.Entries + 1 };
                        if (current.Entries > limits.MaxEntries) throw new ScanLimitException();
                        if (entry.IsReparsePoint) continue; // Old scanner excludes links. Never follow loops or read external trees.
                        if (entry.IsDirectory) { if (!SkippedDirectories.Contains(entry.Name)) subdirectories.Add(entry.Path); }
                        else if (IsGameExecutable(entry.Name)) executables.Add(entry.Path);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn("有目录无法读取或已消失，已跳过。"); continue; }
                if (executables.Count > 0)
                {
                    var name = Path.GetFileName(directory.Observation.DisplayPath);
                    var sorted = executables.OrderByDescending(p => Compact(Path.GetFileNameWithoutExtension(p)) == Compact(name))
                        .ThenBy(p => p, StringComparer.CurrentCulture).ToImmutableArray();
                    candidates.Add(new(Guid.NewGuid().ToString(), name, directory.Observation, sorted,
                        $"根目录发现 {sorted.Length} 个候选 exe；未执行、未识别作品资料。", preflight.Check(item.Path, observed, cancellation)));
                }
                else if (item.Depth < limits.MaxDepth)
                    foreach (var child in subdirectories)
                    {
                        if (queue.Count + current.Directories >= limits.MaxDirectories) throw new ScanLimitException();
                        queue.Enqueue((child, item.Depth + 1));
                    }
                current = current with { Candidates = candidates.Count };
                if (current.Directories == 1 || lastReport.ElapsedMilliseconds >= 50) { progress?.Report(current); lastReport.Restart(); }
            }
            return Result(candidates.Count == 0 ? ScanStatus.NoCandidates : ScanStatus.Completed, candidates.Count == 0 ? "未发现候选游戏。" : "扫描完成，请核对并选择要导入的候选。");
        }
        catch (OperationCanceledException) { return Result(ScanStatus.Cancelled, "扫描已取消，未导入任何候选。"); }
        catch (Exception e) when (e is InvalidDataException or ScanLimitException or IOException or UnauthorizedAccessException)
        { return Result(ScanStatus.Failed, e is ScanLimitException ? "扫描范围达到上限，请选择更具体的目录。" : e.Message); }
        void Warn(string message) { if (warnings.Count < 100) warnings.Add(message); }
        ScanResult Result(ScanStatus status, string message) => new(status, candidates.ToImmutable(), current, warnings.ToImmutable(), message);
    }
    private sealed class ScanLimitException : Exception;
    private static string Compact(string name) => NonAlphanumeric().Replace(name.ToLowerInvariant(), "");
    [GeneratedRegex(@"^(?:unins\d*|uninstall(?:er)?|setup|install(?:er)?|vcredist.*|vc_redist.*|dxsetup|directx.*|redist.*|unitycrashhandler.*|crashreport.*|updater?|configure|configuration)(?:[._ -].*)?\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ExcludedExecutable();
    [GeneratedRegex(@"[^\p{L}\p{N}]")] private static partial Regex NonAlphanumeric();
    [GeneratedRegex(@"(?:^|[\\/])(?:汉化补丁|补丁|patches|_patch|backup|beifen)(?:[\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PatchDirectory();
}
