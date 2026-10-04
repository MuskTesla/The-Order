using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.WinUI.Presentation;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class ErrorState : ObservableState
{
    private string message = "";
    public string Message { get => message; private set { message = value; Changed(); Changed(nameof(IsOpen)); } }
    public bool IsOpen => Message.Length != 0;
    public void Report(string value) => Message = value;
    public void Clear() => Message = "";
}

public sealed class WorkspaceViewModel(IReadOnlyLibrary library, ErrorState errors, NativeConfiguration configuration, ILogger<WorkspaceViewModel> logger) : ObservableState
{
    private IReadOnlyList<LibrarySummary> items = [];
    private string status = "尚未读取合成资料";
    private bool busy;
    public IReadOnlyList<LibrarySummary> Items { get => items; private set { items = value; Changed(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); Changed(nameof(CanReload)); } }
    public bool CanReload => !IsBusy;
    public string DataDirectory => configuration.DataDirectory ?? "未指定；正式书架不会被读取";
    public async Task LoadAsync(CancellationToken token)
    {
        if (IsBusy) return;
        IsBusy = true; errors.Clear(); Status = "正在只读校验合成资料…";
        try
        {
            var result = await library.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
            {
                Items = []; Status = "读取失败 · 未加载书架"; errors.Report(result.Error!);
                logger.LogWarning("Synthetic library load failed: {Status}", result.Status);
                return;
            }
            Items = result.Entries.Select(LibrarySummary.From).ToArray();
            Status = Items.Count == 0 ? "合成书架为空 · 已成功读取" : $"已读取 {Items.Count} 部合成作品 · 只读";
            logger.LogInformation("Synthetic library loaded: {Count} entries", Items.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = "读取已取消"; }
        catch (Exception ex)
        {
            Items = []; Status = "读取失败 · 未加载书架";
            errors.Report("读取过程中发生意外错误。原文件未修改；请检查开发日志。");
            logger.LogError("Unexpected library failure: {ExceptionType}", ex.GetType().Name);
        }
        finally { IsBusy = false; }
    }
}
