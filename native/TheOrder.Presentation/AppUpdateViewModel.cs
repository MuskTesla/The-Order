using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed class AppUpdateViewModel(IAppUpdateFeed feed, IAppUpdatePackages packages, IAppUpdateInstaller installer, string currentVersion, IAppUpdateChannelStore? channels = null) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private bool disposed, busy, applying, automatic;
    private int generation;
    private VerifiedAppUpdate? downloaded;
    private Task? channelInitialization;
    public bool SupportsChannelSwitch => feed is IAppUpdateChannelFeed && channels is not null;
    public AppUpdateChannel SelectedChannel { get; private set; } = currentVersion.Contains('-', StringComparison.Ordinal) ? AppUpdateChannel.Beta : AppUpdateChannel.Stable;
    public bool CanChangeChannel => SupportsChannelSwitch && CanCheck;
    public string ChannelNotice => SelectedChannel == AppUpdateChannel.Beta
        ? "测试通道包含尚在验证的功能。切换前会备份当前资料，并继续使用同一书架。"
        : "正式通道只接收正式发行。可从 Beta 回到兼容的正式版，书架与个人记录保留。";
    public string InstallText => IsChannelChange ? (Latest!.Version.Contains('-', StringComparison.Ordinal) ? "切换到 Beta" : "回到正式版") : "立即更新";
    public bool IsChannelChange => Latest is { } release && AppUpdateVersion.TryParse(release.Version, out var to)
        && AppUpdateVersion.TryParse(CurrentVersion, out var from) && to.Channel != from.Channel;
    public string CurrentVersion { get; } = currentVersion;
    public AppUpdateRelease? Latest { get; private set; }
    private bool IsPreview => CurrentVersion.Contains('-', StringComparison.Ordinal);
    private const string PreviewMessage = "Beta 使用独立测试通道，请从 GitHub Pre-release 页面手动更新；不会自动安装正式版或覆盖稳定版。";
    public string Message { get; private set; } = feed is IAppUpdateChannelFeed && channels is not null ? "选择更新通道，检查可用版本。" : currentVersion.Contains('-', StringComparison.Ordinal) ? PreviewMessage : "点击检查更新，获取最新正式版。";
    public string VersionText => Latest is { } release ? $"发现新版本 {release.Version}" : $"当前版本 {CurrentVersion}";
    public string Notes => Latest?.Notes ?? "";
    public string Highlights => Latest is { } release ? AppUpdateNotes.Highlights(release.Notes) : "";
    public bool HasUpdate => Latest is not null;
    public bool IsBusy => busy && !automatic;
    public bool CanCheck => !disposed && (!busy || automatic);
    public bool CanInstall => !(IsPreview && !SupportsChannelSwitch) && !disposed && !busy && HasUpdate && installer.Availability.CanInstall;
    public bool CanCancel => busy && !automatic && !applying && !disposed;
    public string InstallAvailability => IsPreview && !SupportsChannelSwitch ? PreviewMessage : installer.Availability.Message;
    public double Progress { get; private set; }
    public bool Downloading { get; private set; }
    public bool Indeterminate => IsBusy && !Downloading;
    public async Task ReadReceiptAsync()
    {
        if (IsPreview && !SupportsChannelSwitch) return;
        try { var receipt = await packages.ReadReceiptAsync(CancellationToken.None); if (!disposed && !busy && receipt is not null) { Message = receipt.Message; Notify(); } }
        catch { /* Optional update receipt never prevents ordinary startup. */ }
    }
    public Task CheckAsync() => CheckCoreAsync(false);
    public Task CheckAutomaticallyAsync() => CheckCoreAsync(true);
    private Task InitializeChannelAsync()
    {
        if (channelInitialization?.IsFaulted == true || channelInitialization?.IsCanceled == true) channelInitialization = null;
        return channelInitialization ??= LoadChannelAsync();
    }
    private async Task LoadChannelAsync()
    {
        if (SupportsChannelSwitch && channels is not null)
        {
            var saved = await channels.LoadAsync(CancellationToken.None);
            if (saved is { } value) SelectedChannel = value;
            if (!disposed) { Message = "选择更新通道后检查可用版本。"; Notify(); }
        }
    }
    public async Task ChangeChannelAsync(AppUpdateChannel channel)
    {
        if (!CanChangeChannel || !Enum.IsDefined(channel)) return;
        if (busy) operation?.Cancel();
        using var token = Begin(); var epoch = generation;
        try
        {
            await InitializeChannelAsync();
            await channels!.SaveAsync(channel, token.Token);
            if (disposed || epoch != generation) return;
            SelectedChannel = channel; Latest = null; downloaded = null;
            Message = "更新通道已保存，请检查可用版本。";
        }
        catch (Exception e) { if (!disposed && epoch == generation) Message = e is AppUpdateException ? e.Message : "更新通道未保存，原选择保留。"; }
        finally { End(epoch); }
    }
    private async Task CheckCoreAsync(bool background)
    {
        if (IsPreview && !SupportsChannelSwitch) { Latest = null; Message = PreviewMessage; Notify(); return; }
        if (disposed || busy && (!automatic || background)) return;
        if (busy) operation?.Cancel(); // A manual request replaces a quiet in-flight check.
        using var token = Begin(); var epoch = generation;
        automatic = background;
        if (!background) Message = "正在检查更新…";
        Notify();
        try
        {
            await InitializeChannelAsync();
            var channel = SelectedChannel;
            var release = SupportsChannelSwitch ? await ((IAppUpdateChannelFeed)feed).LatestAsync(channel, token.Token) : await feed.LatestAsync(token.Token);
            token.Token.ThrowIfCancellationRequested();
            if (disposed || epoch != generation) return;
            if (release is not null) AppUpdateIdentity.Validate(release);
            if (release is not null && AppUpdateVersion.CanApply(CurrentVersion, release))
            { Latest = release; Message = IsChannelChange ? "已找到可切换的版本。安装前将备份资料，继续使用当前书架。" : "可下载安装新版本，书架、设置和游玩记录会保留。"; }
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
    private void Notify() { foreach (var name in new[] { nameof(Message),nameof(Latest),nameof(VersionText),nameof(Notes),nameof(Highlights),nameof(HasUpdate),nameof(IsBusy),nameof(CanCheck),nameof(CanInstall),nameof(CanCancel),nameof(Progress),nameof(Downloading),nameof(Indeterminate),nameof(SelectedChannel),nameof(CanChangeChannel),nameof(ChannelNotice),nameof(InstallText) }) Changed(name); }
    public void Dispose() { disposed = true; operation?.Cancel(); }
}
