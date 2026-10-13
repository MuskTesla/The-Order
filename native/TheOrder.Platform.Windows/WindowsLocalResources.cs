using System.Collections.Immutable;
using System.Diagnostics;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsLocalResources(IFilesystemIdentity identity)
{
    private static readonly HashSet<string> SaveNames = new(["save", "saves", "savedata", "save_data", "sav", "userdata", "存档"], StringComparer.OrdinalIgnoreCase);
    public Task<ImmutableArray<string>> FindSaveFoldersAsync(string folder, CancellationToken cancellation = default) => Task.Run(() =>
    {
        var root = Require(folder, true); var guard = new InstallationPreflightService(identity);
        var queue = new Queue<(PathObservation Folder, int Depth)>(); queue.Enqueue((root, 0));
        var result = ImmutableArray.CreateBuilder<string>(); int count = 0;
        while (queue.Count > 0 && count++ < 80)
        {
            cancellation.ThrowIfCancellationRequested(); var item = queue.Dequeue(); guard.Revalidate(root); guard.Revalidate(item.Folder);
            string[] children;
            try { children = new WindowsScanFilesystem().Enumerate(item.Folder, root).Take(400).Where(x => x.IsDirectory && !x.IsReparsePoint).Take(81).Select(x => x.Path).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in children)
            {
                var probe = identity.Inspect(path);
                if (!probe.IsValid || probe.Observation!.ViaReparsePoint || !InstallationPreflightService.Within(probe.Observation.FinalPath, root.FinalPath)) continue;
                if (SaveNames.Contains(Path.GetFileName(path))) result.Add(path);
                else if (item.Depth < 1) queue.Enqueue((probe.Observation, item.Depth + 1));
            }
        }
        guard.Revalidate(root); return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToImmutableArray();
    }, cancellation);
    public Task OpenFolderAsync(string path) => OpenAsync(path, true);
    public Task OpenGuideAsync(string path) => OpenAsync(path, false);
    public Task ValidateGuideAsync(string path, CancellationToken cancellation) => Task.Run(() =>
    { var value = Require(path, false); if (!LocalDocuments.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("请选择受支持的攻略文档。"); new InstallationPreflightService(identity).Revalidate(value); }, cancellation);
    private Task OpenAsync(string path, bool directory) => Task.Run(() =>
    {
        var value = Require(path, directory);
        if (!directory && !LocalDocuments.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("不支持此文档类型。");
        var held = IsolatedLibraryPersistence.PinDirectoryChain(directory ? path : Path.GetDirectoryName(path)!);
        try
        {
            using var file = directory ? null : IsolatedLibrarySource.OpenChecked(path);
            new InstallationPreflightService(identity).Revalidate(value);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    });
    private PathObservation Require(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("请选择完整的本地路径。");
        var value = identity.Inspect(path, directory);
        if (!value.IsValid || value.Observation!.ViaReparsePoint || value.Observation.IsDirectory != directory) throw new InvalidDataException("路径不存在、是链接或无法访问，请重新定位。");
        return value.Observation;
    }
}
