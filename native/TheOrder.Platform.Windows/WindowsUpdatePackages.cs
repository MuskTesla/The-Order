using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsUpdatePackages : IAppUpdatePackages
{
    private readonly NativeConfiguration configuration;
    private readonly Func<Stream?> openBase;
    private readonly string currentVersion;
    public WindowsUpdatePackages(NativeConfiguration configuration) : this(configuration, OpenInstalledBase,
        typeof(WindowsUpdatePackages).Assembly.GetName().Version!.ToString(3)) { }
    // Explicit synthetic dependency, not an environment variable or a configurable update path.
    internal WindowsUpdatePackages(NativeConfiguration configuration, Func<Stream?> openBase, string currentVersion)
    { this.configuration = configuration; this.openBase = openBase; this.currentVersion = currentVersion; }
    private static Stream? OpenInstalledBase()
    {
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (!WindowsUpdateInstaller.ManagedDirectory(directory)) return null;
            var path = Path.Combine(directory, "update-base.bin"); EnsurePlainPath(path);
            var file = IsolatedLibrarySource.OpenChecked(path);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (file.Length is < 2 or > AppUpdateIdentity.MaximumInstallerBytes || file.ReadByte() != 'M' || file.ReadByte() != 'Z'
                    || info.ProductName?.Trim() != "The Order" || !Version.TryParse(info.FileVersion?.Trim(), out var version)
                    || version.ToString(3) != typeof(WindowsUpdatePackages).Assembly.GetName().Version!.ToString(3)) { file.Dispose(); return null; }
                file.Position = 0; return file;
            }
            catch { file.Dispose(); throw; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AppUpdateException) { return null; }
    }
    internal string Root
    {
        get
        {
            var profile = configuration.Profile ?? throw new AppUpdateException("当前资料不可写，未下载更新。");
            profile.ValidateAccess();
            var root = Path.Combine(profile.Root, "updates"); EnsurePlainPath(root); return root;
        }
    }
    internal static void EnsurePlainPath(string path)
    {
        var canonical = ProfilePathGuard.Canonicalize(path);
        for (var directory = new DirectoryInfo(canonical); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AppUpdateException("更新目录包含链接，未继续下载或安装。");
        if (File.Exists(canonical) && (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
            throw new AppUpdateException("更新文件包含链接，未继续下载或安装。");
    }
    public async Task<VerifiedAppUpdate> DownloadAsync(IAppUpdateFeed feed, AppUpdateRelease release, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation)
    {
        AppUpdateIdentity.Validate(release); cancellation.ThrowIfCancellationRequested();
        var root = Root; Directory.CreateDirectory(root); EnsurePlainPath(root);
        // Each attempt owns one new folder. No resumable bytes are ever mistaken for a complete installer.
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); EnsurePlainPath(folder);
        var partial = Path.Combine(folder, "download.part"); var completed = Path.Combine(folder, AppUpdateIdentity.InstallerName(release.Version));
        try
        {
            long downloaded = release.Bytes, reused = 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var delta = await TryDifferentialAsync(feed, release, output, progress, timeout.Token).ConfigureAwait(false);
                if (delta is not null) { downloaded = delta.DownloadBytes + release.BlockMap!.Bytes; reused = delta.ReusedBytes; }
                else
                {
                    output.Position = 0; output.SetLength(0);
                    await feed.CopyInstallerAsync(release, output, progress, timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false); output.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            await ValidateAsync(partial, release, cancellation).ConfigureAwait(false);
            EnsurePlainPath(folder); File.Move(partial, completed);
            // Preserve the downloaded-file security zone; never unblock or alter Defender/SmartScreen policy.
            await File.WriteAllTextAsync(completed + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + release.DownloadUri.AbsoluteUri + "\r\n", cancellation).ConfigureAwait(false);
            return new(release, completed, downloaded, reused);
        }
        catch (Exception error)
        {
            foreach (var path in new[] { partial, completed })
                try { EnsurePlainPath(path); if (File.Exists(path)) File.Delete(path); } catch { }
            try { EnsurePlainPath(folder); Directory.Delete(folder); } catch { }
            if (error is OperationCanceledException && !cancellation.IsCancellationRequested)
                throw new AppUpdateException("准备或下载更新超时，当前程序未改变。请稍后重试。");
            throw;
        }
    }
    private async Task<AppUpdateDeltaPlan?> TryDifferentialAsync(IAppUpdateFeed feed, AppUpdateRelease release, Stream output,
        IProgress<AppUpdateTransfer> progress, CancellationToken cancellation)
    {
        if (feed is not IAppUpdateDifferentialFeed deltaFeed || !AppUpdateIdentity.ValidBlockMap(release)) return null;
        try
        {
            await using var basis = openBase();
            if (basis is null) return null;
            var current = await AppUpdateBlocks.CreateAsync(basis, currentVersion, cancellation).ConfigureAwait(false);
            var next = await deltaFeed.ReadBlockMapAsync(release, cancellation).ConfigureAwait(false);
            var plan = AppUpdateBlocks.Plan(current, next, release.BlockMap!.Bytes);
            if (!plan.Worthwhile) return null;
            await AppUpdateBlocks.ReconstructAsync(basis, output, plan,
                (offset, bytes, destination, token) => deltaFeed.CopyRangeAsync(release, offset, bytes, destination, token), progress, cancellation).ConfigureAwait(false);
            // Neither the old cache nor individual chunks can authorize execution. Only the full release hash can.
            output.Position = 0;
            if (output.Length != release.Bytes || output.ReadByte() != 'M' || output.ReadByte() != 'Z') throw new AppUpdateException("差量合成不完整，将下载完整包。");
            output.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(output, cancellation).ConfigureAwait(false), Convert.FromHexString(release.Sha256)))
                throw new AppUpdateException("差量合成校验失败，将下载完整包。");
            return plan;
        }
        // One bounded fallback. Cancellation always stops both paths; no in-place program writes occur here.
        catch (Exception e) when (e is AppUpdateException or IOException or UnauthorizedAccessException) { return null; }
    }
    internal static async Task ValidateAsync(string path, AppUpdateRelease release, CancellationToken cancellation)
    {
        EnsurePlainPath(path);
        using var file = IsolatedLibrarySource.OpenChecked(path);
        if (file.Length != release.Bytes || file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new AppUpdateException("下载内容不是完整安装包，文件未安装。");
        file.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false), Convert.FromHexString(release.Sha256)))
            throw new AppUpdateException("安装包校验失败，文件未安装。请重新下载。");
    }
    public Task<AppUpdateReceipt?> ReadReceiptAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var path = Path.Combine(Root, "last-result.json"); EnsurePlainPath(path);
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length > 4096) return Task.FromResult<AppUpdateReceipt?>(null);
            using var json = JsonDocument.Parse(file); var value = json.RootElement;
            if (value.GetProperty("schema").GetInt32() != 1 || !AppUpdateVersion.TryParse(value.GetProperty("version").GetString(), out var version)) return Task.FromResult<AppUpdateReceipt?>(null);
            var success = value.GetProperty("success").GetBoolean();
            var message = success ? $"已完成更新至 {version.Text}。" : "上次更新没有完成，未自动改写书架资料。请重新检查更新；若程序无法启动，可重装所选版本修复。";
            if (success && value.TryGetProperty("code", out var code) && code.GetString() == "InstalledRestartUnavailable") message += "请从开始菜单重新打开程序。";
            return Task.FromResult<AppUpdateReceipt?>(new(success, version.Text, message));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or AppUpdateException)
        { return Task.FromResult<AppUpdateReceipt?>(null); }
    }
}
