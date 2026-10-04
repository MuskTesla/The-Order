using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Platform.Windows;

// Only bounded metadata from installations explicitly present in an immutable entry snapshot.
// The shared identity adapter supplies physical identity; no executable is launched or loaded.
public sealed class WindowsRecognitionEvidence(ILocalEvidenceParser parser, Func<IEnumerable<string>, string> fingerprint,
    IExecutableEvidenceParser? executableParser = null) : IRecognitionEvidenceReader
{
    private readonly WindowsFilesystemIdentity filesystem = new();
    private readonly WindowsScanFilesystem directories = new();
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public Task<LocalEvidenceSnapshot> ReadAsync(LibraryEntry entry, bool includePe, CancellationToken cancellation)
        => Task.Run(() => Read(entry, includePe, cancellation), cancellation);
    private LocalEvidenceSnapshot Read(LibraryEntry entry, bool includePe, CancellationToken cancellation)
    {
        var titles = new List<LocalTitleEvidence>(); var refs = new List<ReferenceEvidence>(); var companies = new List<string>();
        var warnings = new List<string>(); var observations = new List<string>(); var roots = new List<PathObservation>(); var peHashes = new List<string>();
        long remaining = 8 * 1024 * 1024;
        void Add(ParsedLocalEvidence parsed) { titles.AddRange(parsed.Titles); refs.AddRange(parsed.References); companies.AddRange(parsed.Companies); warnings.AddRange(parsed.Warnings); }
        void Observe(PathObservation path) => observations.Add(JsonSerializer.Serialize(new { path.StoredPath, path.FinalPath, path.Identity, path.IsDirectory, path.ReparsePaths }));
        void ReadFile(PathObservation root, string path, string kind, int maximum, int prefix)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var result = filesystem.Inspect(path, false);
                if (!result.IsValid) { observations.Add(path + "|unavailable|" + result.Status); return; }
                var file = result.Observation!;
                if (!InstallationPreflightService.Within(file.FinalPath, root.FinalPath)
                    || file.ReparsePaths.Except(root.ReparsePaths, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException();
                using var lease = new PathLease(root, file);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                Revalidate(root); Revalidate(file); Observe(file);
                if (stream.Length > maximum || remaining < Math.Min(stream.Length, prefix)) { observations.Add(path + "|oversize"); warnings.Add("已跳过超过大小或本次读取总量限制的线索文件。"); return; }
                int length = (int)Math.Min(stream.Length, prefix); long size = stream.Length;
                var bytes = new byte[length]; int read = 0;
                while (read < length) { cancellation.ThrowIfCancellationRequested(); int n = stream.Read(bytes, read, length - read); if (n == 0) throw new IOException(); read += n; }
                remaining -= read; cancellation.ThrowIfCancellationRequested(); Revalidate(root); Revalidate(file);
                observations.Add(path + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Add(parser.Parse(kind, Path.GetFileName(path), bytes, size > length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { observations.Add(path + "|unsafe"); warnings.Add("部分本地线索无法安全读取，已保留其他可用证据。"); }
        }
        DirectoryItem[] Enumerate(PathObservation root, string path, int limit)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var result = filesystem.Inspect(path); if (!result.IsValid) { observations.Add(path + "|unavailable|" + result.Status); return []; }
                var directory = result.Observation!;
                if (directory.Identity != root.Identity && (!InstallationPreflightService.Within(directory.FinalPath, root.FinalPath) || directory.ReparsePaths.Except(root.ReparsePaths, StringComparer.OrdinalIgnoreCase).Any())) return [];
                var items = new List<DirectoryItem>();
                foreach (var item in directories.Enumerate(directory, root)) { cancellation.ThrowIfCancellationRequested(); if (items.Count == limit) { warnings.Add("目录项目较多，只检查了有界范围内的文件名。"); break; } items.Add(item); }
                Observe(directory);
                observations.Add(path + "|entries|" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items.OrderBy(i => i.Name, StringComparer.Ordinal)))));
                return items.OrderBy(i => i.Name, StringComparer.Ordinal).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { observations.Add(path + "|unsafe"); warnings.Add("部分线索目录无法安全读取。"); return []; }
        }
        var installations = entry.Installations.OrderByDescending(i => i.Id == entry.PreferredInstallationId).Take(100).ToArray();
        foreach (var installation in installations)
        {
            cancellation.ThrowIfCancellationRequested();
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(installation.Folder));
            if (folder.Length > 0) titles.Add(new(folder, "folder"));
            var result = filesystem.Inspect(installation.Folder);
            if (!result.IsValid) { observations.Add(installation.Folder + "|unavailable|" + result.Status); continue; }
            var root = result.Observation!;
            if (directories.IsForbiddenRoot(root)) { warnings.Add("已拒绝从系统根目录收集线索。"); continue; }
            if (roots.Any(r => r.Identity == root.Identity)) continue;
            roots.Add(root); Observe(root);
            ReadFile(root, Path.Combine(installation.Folder, ".PotatoVN", "meta.json"), "sidecar", 1024 * 1024, 1024 * 1024);
            ReadFile(root, Path.Combine(installation.Folder, "steam_appid.txt"), "steam", 128, 128);
            foreach (var readme in Enumerate(root, root.StoredPath, 512).Where(e => !e.IsDirectory && !e.IsReparsePoint && Match(e.Name, @"^(?:readme|read me|说明|必读|开始前请读|汉化说明|游戏说明|遊戲說明)[^\\/]*\.txt$")).Take(3))
                ReadFile(root, readme.Path, "readme-heading", 64 * 1024, 64 * 1024);
        }
        if (!titles.Any(t => t.Source == "folder" && t.Value == entry.Name)) titles.Add(new(entry.Name, "custom-name"));
        var preferred = installations.FirstOrDefault();
        var selectedRoot = roots.FirstOrDefault(r => r.StoredPath == preferred?.Folder);
        if (preferred is not null && selectedRoot is { } selected)
        {
            var entries = Enumerate(selected, selected.StoredPath, 512);
            var executables = new List<string>();
            if (preferred.Executable is { } chosen) executables.Add(chosen);
            executables.AddRange(entries.Where(e => !e.IsDirectory && !e.IsReparsePoint && Match(e.Name, @"\.exe$")).Select(e => e.Path));
            foreach (string path in executables.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => executableParser?.IsUsefulExecutable(Path.GetFileName(p)) ?? false).Take(5))
            {
                var filenameCheck = filesystem.Inspect(path, false);
                if (!filenameCheck.IsValid || !InstallationPreflightService.Within(filenameCheck.Observation!.FinalPath, selected.FinalPath)
                    || filenameCheck.Observation.ReparsePaths.Except(selected.ReparsePaths, StringComparer.OrdinalIgnoreCase).Any()) continue;
                int titlesBefore = titles.Count;
                var name = Path.GetFileNameWithoutExtension(path);
                if (executableParser!.IsUsefulTitle(name)) titles.Add(new(name, "executable-name"));
                if (!includePe) continue;
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var check = filesystem.Inspect(path, false); if (!check.IsValid) continue;
                    var file = check.Observation!;
                    if (!InstallationPreflightService.Within(file.FinalPath, selected.FinalPath) || file.ReparsePaths.Except(selected.ReparsePaths, StringComparer.OrdinalIgnoreCase).Any()) continue;
                    using var lease = new PathLease(selected, file);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Revalidate(selected); Revalidate(file);
                    var parsed = executableParser.Read(stream, Path.GetFileName(path), cancellation);
                    Revalidate(selected); Revalidate(file); Observe(file);
                    if (parsed.RejectExecutable) { titles.RemoveRange(titlesBefore, titles.Count - titlesBefore); observations.Add(path + "|helper"); continue; }
                    observations.Add(path + "|pe|" + parsed.Fingerprint); Add(parsed.Evidence);
                    if (parsed.Fingerprint.Length > 0) peHashes.Add(parsed.Fingerprint);
                    if (string.Equals(path, preferred.Executable, StringComparison.OrdinalIgnoreCase) && parsed.Evidence.Titles.Any(t => t.Source is "product-name" or "file-description")) includePe = false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { observations.Add(path + "|pe-unavailable"); warnings.Add("部分程序版本资料无法安全读取。"); }
            }
            foreach (var file in entries.Where(e => !e.IsDirectory && !e.IsReparsePoint && Match(e.Name, @"\.(ain|pfs|ypf)$") && !Match(e.Name, @"backup|beifen|\.bak|\.old")).Take(5))
            { string name = Path.GetFileNameWithoutExtension(file.Name); if (executableParser?.IsUsefulTitle(name) == true) titles.Add(new(name, "archive-name")); }
            foreach (var file in entries.Where(e => !e.IsDirectory && !e.IsReparsePoint && Match(e.Name, @"^(steam_emu\.ini|steam_api(?:64)?\.ini|steamconfig\.ini|steam\.ini|smartsteamemu\.ini|coldclientloader\.ini|steam_appid\.txt)$"))) ReadFile(selected, file.Path, "steam", 128 * 1024, 128 * 1024);
            foreach (string name in new[] { "configs.app.ini", "steam_appid.txt" }) ReadFile(selected, Path.Combine(selected.StoredPath, "steam_settings", name), "steam", 128 * 1024, 128 * 1024);
            int documents = 0;
            foreach (string directory in new[] { "", "manual", "Manual", "help", "StartData" }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Enumerate(selected, Path.Combine(selected.StoredPath, directory), 128))
            {
                cancellation.ThrowIfCancellationRequested(); if (file.IsDirectory || file.IsReparsePoint) continue;
                bool config = (directory.Length == 0 || directory == "StartData") && Match(file.Name, @"^(start|game|gameconfig|gameexe)\.ini$");
                bool document = Match(file.Name, @"^(read[ _-]?me(?:[-_ ](?:ja|jp|zh|en))?|manual|お読みください|お読み下さい|はじめにお読みください|説明書|游戏说明|遊戲說明)\.(html?|txt)$")
                    || (directory.Equals("manual", StringComparison.OrdinalIgnoreCase) || directory == "help") && Match(file.Name, @"^index\.html?$");
                if ((!config && !document) || documents >= 8) continue;
                documents++; ReadFile(selected, file.Path, config ? "config" : "manual", 128 * 1024, 32 * 1024);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var evidence = new RecognitionEvidence([.. titles.Take(40)], [.. refs.Distinct().Take(40)], [.. companies.Distinct(StringComparer.Ordinal).Take(16)],
            [.. warnings.Distinct(StringComparer.Ordinal)], [.. peHashes], entry.Installations.Any(i => Match(i.Folder, @"(?:^|[\\/])(?:汉化补丁|补丁|patches|_patch|backup|beifen)(?:[\\/]|$)")), []);
        return new(evidence, [.. roots], [.. observations], fingerprint(observations));
    }
    private void Revalidate(PathObservation value) => new InstallationPreflightService(filesystem).Revalidate(value);
    private sealed class PathLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        public PathLease(PathObservation root, PathObservation file)
        {
            try
            {
                foreach (string start in new[] { root.DisplayPath, root.FinalPath, Path.GetDirectoryName(file.DisplayPath)!, Path.GetDirectoryName(file.FinalPath)! }.Distinct(StringComparer.OrdinalIgnoreCase))
                for (string? path = start; path is not null; path = Path.GetDirectoryName(path))
                {
                    var handle = CreateFile(path, 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("本地线索目录正在变化。"); }
                    handles.Add(handle);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
