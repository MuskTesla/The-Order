using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Only a user-selected library is read. No login/config/account files and no automatic traversal
// of paths listed by libraryfolders.vdf. Multiple libraries are explicit source selections.
public sealed class WindowsSteamLibraryScanner(IFilesystemIdentity identity, BoundedScanner scanner, ITitleIndex? titles = null) : ISteamLibraryScanner
{
    public Task<ScanResult> ScanAsync(PathObservation root, ImmutableArray<ExistingInstallation> existing, CancellationToken cancellation)
        => Task.Run(async () =>
        {
            var candidates = ImmutableArray.CreateBuilder<ScanCandidate>(); var warnings = ImmutableArray.CreateBuilder<string>(); int inspected = 0;
            try
            {
                var guard = new InstallationPreflightService(identity); guard.Revalidate(root);
                var appsPath = Path.GetFileName(root.StoredPath.TrimEnd('\\')).Equals("steamapps", StringComparison.OrdinalIgnoreCase) ? root.StoredPath : Path.Combine(root.StoredPath, "steamapps");
                var apps = Require(appsPath, root); var common = Require(Path.Combine(apps.StoredPath, "common"), apps);
                var manifests = Directory.EnumerateFiles(apps.StoredPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).Take(2001).ToArray();
                if (manifests.Length > 2000) throw new InvalidDataException("Steam 清单超过 2000 项，请缩小库范围。");
                foreach (var file in manifests)
                {
                    cancellation.ThrowIfCancellationRequested(); guard.Revalidate(root); guard.Revalidate(apps); guard.Revalidate(common); inspected++;
                    try
                    {
                        var observed = identity.Inspect(file, false);
                        if (!observed.IsValid || observed.Observation!.ViaReparsePoint || !InstallationPreflightService.Within(observed.Observation.FinalPath, apps.FinalPath)) throw new InvalidDataException("清单路径不可读取。");
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (stream.Length is < 1 or > 1_048_576) throw new InvalidDataException("清单过大或为空。");
                        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                        var state = ValveKeyValues.Parse(await reader.ReadToEndAsync(cancellation).ConfigureAwait(false)).GetValueOrDefault("AppState") ?? throw new InvalidDataException("清单缺少 AppState。");
                        var id = state.String("appid"); var folder = state.String("installdir"); var name = state.String("name");
                        if (id is null || !Regex.IsMatch(id, "^[1-9][0-9]{0,9}$") || Path.GetFileName(file) != "appmanifest_" + id + ".acf" || string.IsNullOrWhiteSpace(name) || name.Length > 2000
                            || string.IsNullOrWhiteSpace(folder) || folder is "." or ".." || folder.IndexOfAny(['\\', '/', ':']) >= 0 || !uint.TryParse(state.String("StateFlags"), out var flags) || (flags & 4) == 0)
                            throw new InvalidDataException("尚未安装完成或清单无效。");
                        var game = Require(Path.Combine(common.StoredPath, folder), common);
                        var catalogMatch = titles is not null && await titles.ContainsReferenceAsync("steam:" + id, cancellation).ConfigureAwait(false);
                        var result = await scanner.ScanAsync(game.StoredPath, existing, new(MaxDepth: 3, MaxDirectories: 200, MaxEntries: 4000, MaxCandidates: 20), cancellation: cancellation).ConfigureAwait(false);
                        if (result.Status == ScanStatus.Failed || result.IsPartial) warnings.Add(name + "：" + result.Message);
                        // One installed app may contain several launchable subfolders. Let the user
                        // select them independently; the app ID remains a clue, never a metadata match.
                        guard.Revalidate(game); guard.Revalidate(observed.Observation);
                        if (candidates.Count + result.Candidates.Length > 2000) return new ScanResult(ScanStatus.Failed, [], new(inspected, inspected, 0, root.StoredPath), warnings.ToImmutable(), "Steam 候选过多，请缩小库范围。");
                        foreach (var c in result.Candidates) candidates.Add(c with { Name = name, SteamAppId = id, SteamCatalogMatch = catalogMatch,
                            Evidence = "Steam 已安装清单 · " + id + (catalogMatch ? " · 视觉小说目录已收录" : " · 作品类型未确认") });
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException) { warnings.Add(Path.GetFileName(file) + "：" + e.Message); }
                }
                guard.Revalidate(root); guard.Revalidate(apps); guard.Revalidate(common);
                return new ScanResult(candidates.Count == 0 ? ScanStatus.NoCandidates : ScanStatus.Completed, [.. candidates.DistinctBy(c => c.Folder.Identity)], new(inspected, inspected, candidates.Count, root.StoredPath), warnings.ToImmutable(), $"Steam 扫描完成，发现 {candidates.Count} 个候选。");
            }
            catch (OperationCanceledException) { return new ScanResult(ScanStatus.Cancelled, [], new(inspected, inspected, 0, root.StoredPath), [], "Steam 扫描已取消。"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return new ScanResult(ScanStatus.Failed, [], new(inspected, inspected, 0, root.StoredPath), warnings.ToImmutable(), e.Message); }
        }, cancellation);
    private PathObservation Require(string path, PathObservation parent)
    {
        var value = identity.Inspect(path);
        if (!value.IsValid || value.Observation!.ViaReparsePoint || value.Observation.Identity != parent.Identity && !InstallationPreflightService.Within(value.Observation.FinalPath, parent.FinalPath)) throw new InvalidDataException("Steam 目录越界、是链接或不可读取。");
        return value.Observation;
    }
}
