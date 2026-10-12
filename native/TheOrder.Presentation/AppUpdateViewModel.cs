using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed class AppUpdateViewModel(IAppUpdateFeed feed, IAppUpdatePackages packages, IAppUpdateInstaller installer, string currentVersion) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private bool disposed, busy, applying, automatic;
    private int generation;
    private VerifiedAppUpdate? downloaded;
    public string CurrentVersion { get; } = currentVersion;
    public AppUpdateRelease? Latest { get; private set; }
    public string Message { get; private set; } = "点击检查更新，获取最新正式版。";
    public string VersionText => Latest is { } release ? $"发现新版本 {release.Version}" : $"当前版本 {CurrentVersion}";
    public string Notes => Latest?.Notes ?? "";
    public string Highlights => Latest is { } release ? AppUpdateNotes.Highlights(release.Notes) : "";
    public bool HasUpdate => Latest is not null;
    public bool IsBusy => busy && !automatic;
    public bool CanCheck => !disposed && (!busy || automatic);
    public bool CanInstall => !disposed && !busy && HasUpdate && installer.Availability.CanInstall;
    public bool CanCancel => busy && !automatic && !applying && !disposed;
    public string InstallAvailability => installer.Availability.Message;
    public double Progress { get; private set; }
    public bool Downloading { get; private set; }
    public bool Indeterminate => IsBusy && !Downloading;
    public async Task ReadReceiptAsync()
    {
        try { var receipt = await packages.ReadReceiptAsync(CancellationToken.None); if (!disposed && !busy && receipt is not null) { Message = receipt.Message; Notify(); } }
        catch { /* Optional update receipt never prevents ordinary startup. */ }
    }
    public Task CheckAsync() => CheckCoreAsync(false);
    public Task CheckAutomaticallyAsync() => CheckCoreAsync(true);
    private async Task CheckCoreAsync(bool background)
    {
        if (disposed || busy && (!automatic || background)) return;
        if (busy) operation?.Cancel(); // A manual request replaces a quiet in-flight check.
        using var token = Begin(); var epoch = generation;
        automatic = background;
        if (!background) Message = "正在检查更新…";
        Notify();
        try
        {
            var release = await feed.LatestAsync(token.Token);
            token.Token.ThrowIfCancellationRequested();
            if (disposed || epoch != generation) return;
            if (release is not null) AppUpdateIdentity.Validate(release);
            if (release is not null && AppUpdateIdentity.IsNewer(release.Version, CurrentVersion))
            { Latest = release; Message = "可下载安装新版本，书架、设置和游玩记录会保留。"; }
            else { Latest = null; Message = release is null ? "暂时没有已发布的更新。" : "当前没有更新，已是最新可用版本。"; }
        }
        catch (OperationCanceledException) { if (!disposed && epoch == generation && !background) Message = "检查更新已取消。"; }
        catch (Exception e) { if (!disposed && epoch == generation && !background) Message = e is AppUpdateException ? e.Message : "检查更新未完成，请稍后重试。"; }
        finally { End(epoch); }
    }
    public async Task InstallAsync(Func<VerifiedAppUpdate, Task<bool>> shutdown)
    {
        if (!CanInstall || Latest is not { } release) return;
        using var token = Begin(); var epoch = generation;
        Downloading = true; Progress = 0; Message = "正在比较并准备所需更新…"; Notify();
        var progress = new Progress<AppUpdateTransfer>(value =>
        {
            if (disposed || generation != epoch || !Downloading) return;
            Progress = Math.Clamp(value.Received * 100d / Math.Max(1, value.Total), 0, 100);
            Message = $"{(value.Differential ? "正在下载变化部分" : "正在下载完整更新")} {Progress:F0}% · {value.Received / 1048576d:F1} / {value.Total / 1048576d:F1} MB"; Notify();
        });
        try
        {
            var package = downloaded?.Release == release ? downloaded : await packages.DownloadAsync(feed, release, progress, token.Token);
            downloaded = package;
            token.Token.ThrowIfCancellationRequested();
            if (disposed || epoch != generation) return;
            applying = true; Downloading = false; Message = "下载与校验完成，正在保存并准备升级…"; Notify();
            if (!await shutdown(package) && !disposed) Message = "本次升级未开始，程序已保留。请完成保存后重试。";
        }
        catch (OperationCanceledException) { if (!disposed) Message = "下载已取消，当前程序和数据未改变。"; }
        catch (Exception e) { if (!disposed) Message = e is AppUpdateException ? e.Message : "升级未能开始，当前程序和数据已保留，请重试。"; }
        finally { End(epoch); }
    }
    private CancellationTokenSource Begin() { operation = new(); busy = true; generation++; return operation; }
    private void End(int epoch) { if (epoch != generation) return; generation++; operation = null; busy = applying = automatic = Downloading = false; if (!disposed) Notify(); }
    public void Cancel() { if (CanCancel) operation?.Cancel(); }
    private void Notify() { foreach (var name in new[] { nameof(Message),nameof(Latest),nameof(VersionText),nameof(Notes),nameof(Highlights),nameof(HasUpdate),nameof(IsBusy),nameof(CanCheck),nameof(CanInstall),nameof(CanCancel),nameof(Progress),nameof(Downloading),nameof(Indeterminate) }) Changed(name); }
    public void Dispose() { disposed = true; operation?.Cancel(); }
}
