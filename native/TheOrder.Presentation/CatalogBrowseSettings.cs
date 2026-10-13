using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed class CatalogBrowseSettings(ICatalogBrowsePreferencesStore store, CatalogBrowsePolicy policy) : ObservableState, IAsyncDisposable
{
    private bool initialized, readOnly, saving, disposed;
    private Task write = Task.CompletedTask;
    public bool ShowNsfw => policy.Capture().ShowNsfw;
    public bool CanChange => initialized && !readOnly && !saving && !disposed;
    public string Message { get; private set; } = UiText.Get("正在读取内容设置…");
    private void Notify() { Changed(nameof(ShowNsfw)); Changed(nameof(CanChange)); Changed(nameof(Message)); }
    public async Task InitializeAsync()
    {
        if (initialized || disposed) return;
        try
        {
            var read = await store.LoadAsync();
            if (disposed) return;
            readOnly = read.ReadOnly; policy.Set(read.Preference.ShowNsfw);
            Message = read.Warning ?? UiText.Get("按 KunGal 的 SFW / NSFW 分类展示搜索与标签结果。");
        }
        catch (Exception)
        {
            readOnly = true; policy.Set(false);
            Message = UiText.Get("内容设置未能读取，本次隐藏 NSFW；原文件保留。");
        }
        initialized = true; Notify();
    }
    public Task ChangeAsync(bool showNsfw)
    {
        if (!CanChange || showNsfw == ShowNsfw) return Task.CompletedTask;
        policy.Set(showNsfw); saving = true; Message = UiText.Get("正在保存…"); Notify();
        return write = SaveAsync(new(showNsfw));
    }
    private async Task SaveAsync(CatalogBrowsePreference value)
    {
        try { await store.SaveAsync(value); Message = UiText.Get("已保存；搜索与标签结果已重置。"); }
        catch (Exception) { Message = UiText.Get("设置未能保存，仅本次有效；原文件保留。请重试。"); }
        finally { saving = false; if (!disposed) Notify(); }
    }
    public async ValueTask DisposeAsync() { disposed = true; await write; }
}
