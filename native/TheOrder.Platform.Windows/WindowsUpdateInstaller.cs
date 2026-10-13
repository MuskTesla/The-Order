using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Starts only after the user chooses Install. The helper cannot install before
// both the application commits its clean shutdown and the exact owning PID exits.
public sealed class WindowsUpdateInstaller(NativeConfiguration configuration, WindowsUpdatePackages packages) : IAppUpdateInstaller
{
    internal const string InstallerIdentity = "TheOrder.Setup.F7940B13-6814-42F7-AB71-69A9901D7DB3";
    private string? target;
    public AppUpdateAvailability Availability
    {
        get
        {
            if (configuration.Profile is not NativeProfileStore { Manifest.ProfileKind: NativeProfileKind.Production })
                return new(false, "独立试用版只检查更新；请在正式使用的版本中安装更新。");
            try
            {
                var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
                target = ManagedDirectory(current) ? current : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "The Order");
                WindowsUpdatePackages.EnsurePlainPath(target);
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !ManagedDirectory(target))
                    return new(false, "安装目录已有其他文件，无法直接升级；当前程序和数据不受影响。");
                return new(true, "更新会保留使用数据，安装完成后重新打开程序。");
            }
            catch { return new(false, "暂时无法确认安装目录，未启用自动升级。"); }
        }
    }
    internal static bool ManagedDirectory(string path)
    {
        WindowsUpdatePackages.EnsurePlainPath(path);
        var marker = Path.Combine(path, "installer-identity.ini"); WindowsUpdatePackages.EnsurePlainPath(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096 || !File.Exists(Path.Combine(path, "unins000.exe"))) return false;
        using var file = IsolatedLibrarySource.OpenChecked(marker); using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n').Any(line => line.Trim() == "Identity=" + InstallerIdentity);
    }
    public async Task<IAppUpdateHandoff> PrepareAsync(VerifiedAppUpdate package, CancellationToken cancellation)
    {
        var available = Availability;
        if (!available.CanInstall || target is null) throw new AppUpdateException(available.Message);
        AppUpdateIdentity.Validate(package.Release);
        var currentVersion = typeof(WindowsUpdateInstaller).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (!AppUpdateIdentity.IsNewer(package.Release.Version, currentVersion)) throw new AppUpdateException("这个安装包不是更新版本，未开始安装。");
        var root = packages.Root; var folder = Path.GetDirectoryName(Path.GetFullPath(package.InstallerPath))!;
        if (Path.GetDirectoryName(folder) != root || !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)
            || Path.GetFileName(package.InstallerPath) != AppUpdateIdentity.InstallerName(package.Release.Version)) throw new AppUpdateException("更新文件不在本次下载目录，未开始安装。");
        await WindowsUpdatePackages.ValidateAsync(package.InstallerPath, package.Release, cancellation).ConfigureAwait(false);
        var version = FileVersionInfo.GetVersionInfo(package.InstallerPath);
        // Inno Setup pads version-resource strings with spaces.
        if (version.ProductName?.Trim() != "The Order" || !Version.TryParse(version.FileVersion?.Trim(), out var installerVersion)
            || installerVersion.ToString(3) != package.Release.Version) throw new AppUpdateException("安装包的程序身份或版本不正确，未开始安装。");
        var worker = Path.Combine(AppContext.BaseDirectory, "update-worker.ps1"); WindowsUpdatePackages.EnsurePlainPath(worker);
        if (!File.Exists(worker)) throw new AppUpdateException("更新组件缺失，未开始安装。");
        using var workerFile = IsolatedLibrarySource.OpenChecked(worker);
        var workerHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(workerFile, cancellation).ConfigureAwait(false));
        // A retry gets a fresh ticket, ready receipt and kernel event pair.
        // Reusing a canceled ticket could consume a stale ready receipt/signal.
        folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); WindowsUpdatePackages.EnsurePlainPath(folder);
        var id = Path.GetFileName(folder); var planPath = Path.Combine(folder, "plan.json");
        using var owner = Process.GetCurrentProcess();
        var handoff = new Handoff(id);
        try
        {
        var profile = (NativeProfileStore)configuration.Profile!; profile.ValidateAccess();
        var plan = new { schema = 1, id, version = package.Release.Version, bytes = package.Release.Bytes, sha256 = package.Release.Sha256,
            installer = Path.GetFullPath(package.InstallerPath), target, profile = configuration.Profile!.Root,
            profileId = profile.Manifest.ProfileId, storeId = profile.Manifest.StoreId, physicalRootHash = profile.Manifest.PhysicalRoot,
            parentPid = owner.Id, parentStartTicks = owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            workerSha256 = workerHash, result = Path.Combine(root, "last-result.json"), currentVersion,
            commitEvent = "Local\\TheOrder.Update.Commit." + id, cancelEvent = "Local\\TheOrder.Update.Cancel." + id };
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), cancellation).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", worker, "-Plan", planPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new AppUpdateException("更新安装程序未能启动，当前窗口已保留。");
            var until = Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellation.ThrowIfCancellationRequested();
                var ready = Path.Combine(folder, "ready.json");
                if (File.Exists(ready))
                {
                    WindowsUpdatePackages.EnsurePlainPath(ready);
                    using var file = IsolatedLibrarySource.OpenChecked(ready);
                    if (file.Length > 4096) throw new AppUpdateException("更新组件返回信息无效。");
                    using var response = JsonDocument.Parse(file);
                    if (response.RootElement.GetProperty("id").GetString() == id && response.RootElement.GetProperty("ready").GetBoolean()) return handoff;
                    throw new AppUpdateException("更新组件准备失败，当前窗口已保留。");
                }
                if (process.HasExited) throw new AppUpdateException("更新组件未能准备，当前窗口已保留。");
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            throw new AppUpdateException("更新组件准备超时，当前窗口已保留。");
        }
        catch { handoff.Dispose(); throw; }
    }
    private sealed class Handoff : IAppUpdateHandoff
    {
        private bool committed;
        private readonly EventWaitHandle commit, cancel;
        internal Handoff(string id)
        {
            commit = new(false, EventResetMode.ManualReset, "Local\\TheOrder.Update.Commit." + id);
            cancel = new(false, EventResetMode.ManualReset, "Local\\TheOrder.Update.Cancel." + id);
        }
        public void Commit()
        {
            // No disk writes after profile disposal. The helper already owns its
            // named handles, and still must observe the exact process exit.
            commit.Set(); committed = true;
        }
        public void Dispose()
        {
            if (!committed) cancel.Set();
            commit.Dispose(); cancel.Dispose();
        }
    }
}
