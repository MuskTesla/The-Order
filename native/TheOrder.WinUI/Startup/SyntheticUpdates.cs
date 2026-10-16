using TheOrder.Application;

namespace TheOrder.WinUI.Startup;

// Isolated visual preview only; offers no executable and cannot install anything.
internal sealed class SyntheticUpdates : IAppUpdateChannelFeed
{
    public Task<AppUpdateRelease?> LatestAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<AppUpdateRelease?>(new("1.1.1", "合成更新预览：显示新版本、更新说明和一键安装入口。这里没有真实更新文件。",
            new("https://github.com/Ph-Rank/The-Order/releases/download/v1.1.1/The-Order-1.1.1-Setup-x64.exe"), 72000000, new string('0',64)));
    }
    public Task<AppUpdateRelease?> LatestAsync(AppUpdateChannel channel, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var version = channel == AppUpdateChannel.Beta ? "1.2.0-beta.3" : "1.1.1";
        return Task.FromResult<AppUpdateRelease?>(new(version, "合成通道预览；测试资料不能执行安装。",
            new($"https://github.com/Ph-Rank/The-Order/releases/download/v{version}/The-Order-{version}-Setup-x64.exe"), 72000000, new string('0',64)) { ChannelProtocol = 1 });
    }
    public Task CopyInstallerAsync(AppUpdateRelease release, Stream destination, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation)
        => throw new AppUpdateException("合成预览不下载安装包。");
}
