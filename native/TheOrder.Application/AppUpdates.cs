using System.Globalization;
using System.Text.RegularExpressions;

namespace TheOrder.Application;

public sealed record AppUpdateBlockMapAsset(Uri DownloadUri, long Bytes, string Sha256);
public sealed record AppUpdateRelease(string Version, string Notes, Uri DownloadUri, long Bytes, string Sha256, AppUpdateBlockMapAsset? BlockMap = null)
{
    // Only a verified unified-install manifest grants a cross-channel transition.
    public int ChannelProtocol { get; init; }
}
public sealed record AppUpdateTransfer(long Received, long Total, bool Differential = false);
public sealed record VerifiedAppUpdate(AppUpdateRelease Release, string InstallerPath, long DownloadedBytes = 0, long ReusedBytes = 0);
public sealed record AppUpdateAvailability(bool CanInstall, string Message);
public sealed record AppUpdateReceipt(bool Success, string Version, string Message);

public interface IAppUpdateFeed
{
    Task<AppUpdateRelease?> LatestAsync(CancellationToken cancellation);
    Task CopyInstallerAsync(AppUpdateRelease release, Stream destination, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation);
}
public interface IAppUpdatePackages
{
    Task<VerifiedAppUpdate> DownloadAsync(IAppUpdateFeed feed, AppUpdateRelease release, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation);
    Task<AppUpdateReceipt?> ReadReceiptAsync(CancellationToken cancellation);
}
// Optional capability: old releases/feeds still use the complete verified installer.
public interface IAppUpdateDifferentialFeed
{
    Task<AppUpdateBlockMap> ReadBlockMapAsync(AppUpdateRelease release, CancellationToken cancellation);
    Task CopyRangeAsync(AppUpdateRelease release, long offset, int bytes, Stream destination, CancellationToken cancellation);
}
public interface IAppUpdateHandoff : IDisposable
{
    // The worker also waits for the exact owning process to exit. Preparation
    // alone never authorizes installation; failed/canceled shutdown calls Dispose.
    void Commit();
}
public interface IAppUpdateInstaller
{
    AppUpdateAvailability Availability { get; }
    Task<IAppUpdateHandoff> PrepareAsync(VerifiedAppUpdate package, CancellationToken cancellation);
}
public sealed class AppUpdateException(string message) : Exception(message);

public static partial class AppUpdateIdentity
{
    public const string Repository = "Ph-Rank/The-Order";
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    public const int MaximumBlockMapBytes = 4 * 1024 * 1024;
    public static string InstallerName(string version) => $"The-Order-{version}-Setup-x64.exe";
    public static string BlockMapName(string version) => InstallerName(version) + ".blocks.json";
    public static bool TryVersion(string? input, out Version version)
    {
        version = new();
        if (input is null || input.Length > 24 || !VersionPattern().IsMatch(input)) return false;
        var value = input.StartsWith('v') ? input[1..] : input;
        if (!System.Version.TryParse(value, out var parsed) || parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build > 65535) return false;
        version = parsed; return true;
    }
    public static bool IsNewer(string offered, string current) => TryVersion(offered, out var a) && TryVersion(current, out var b) && a > b;
    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    public static bool ValidDownload(Uri uri, string version)
    {
        if (!AppUpdateVersion.TryParse(version, out var parsed) || parsed.Text != version || !SafeHttps(uri) || uri.Query.Length != 0) return false;
        return ValidAsset(uri, version, InstallerName(version));
    }
    public static bool ValidAsset(Uri uri, string version, string name)
    {
        if (!AppUpdateVersion.TryParse(version, out var parsed) || parsed.Text != version || !SafeHttps(uri) || uri.Query.Length != 0) return false;
        if (name != InstallerName(version) && name != BlockMapName(version) && name != "update-channel.json") return false;
        var expected = $"/{Repository}/releases/download/v{version}/{name}";
        var withoutV = $"/{Repository}/releases/download/{version}/{name}";
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && (uri.AbsolutePath == expected || uri.AbsolutePath == withoutV);
    }
    public static bool SafeHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 16384;
    public static void Validate(AppUpdateRelease release)
    {
        if (!ValidDownload(release.DownloadUri, release.Version) || !ValidHash(release.Sha256)
            || release.Bytes < 2 || release.Bytes > MaximumInstallerBytes) throw new AppUpdateException("更新文件信息不完整或来源不正确，未开始安装。请稍后重新检查。");
    }
    public static bool ValidBlockMap(AppUpdateRelease release) => release.BlockMap is { } map
        && ValidAsset(map.DownloadUri, release.Version, BlockMapName(release.Version))
        && ValidHash(map.Sha256) && map.Bytes is > 0 and <= MaximumBlockMapBytes;
    [GeneratedRegex(@"^v?(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
