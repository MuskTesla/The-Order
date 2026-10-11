using TheOrder.Application;
namespace TheOrder.Presentation;

// Selection is persisted immediately; apply on the next ordinary launch, so a
// language change cannot destroy a draft, queued import or running game/session.
public sealed class LanguageSettings(ILanguagePreferencesStore store, string systemLanguage, bool enableSelection = true) : ObservableState, IAsyncDisposable
{
    private bool initialized, readOnly, saving, disposed;
    private Task write = Task.CompletedTask;
    public string Selected { get; private set; } = UiText.System;
    public string Effective { get; private set; } = enableSelection ? UiText.Resolve(UiText.System, systemLanguage) : "zh-Hans";
    public bool SelectionAvailable => enableSelection;
    public string SystemLanguage { get; } = systemLanguage;
    public bool CanChange => enableSelection && initialized && !readOnly && !saving && !disposed;
    public string Message { get; private set; } = "";
    public async Task InitializeAsync()
    {
        if (initialized || disposed) return;
        try
        {
            var read = await store.LoadAsync();
            if (disposed) return;
            Selected = read.Preference.Language; readOnly = read.ReadOnly;
            // A Chinese-only build preserves the saved preference without applying
            // or overwriting it. Re-enabling the feature restores that choice.
            Effective = enableSelection ? UiText.Resolve(Selected, SystemLanguage) : "zh-Hans";
            UiText.Configure(Effective, SystemLanguage);
            Message = read.Warning is { } warning ? UiText.Get(warning) : UiText.Get("界面和目录资料优先使用所选语言；缺少译文时保留原文。");
        }
        catch (Exception)
        {
            readOnly = true; Effective = enableSelection ? UiText.Resolve(UiText.System, SystemLanguage) : "zh-Hans"; UiText.Configure(Effective, SystemLanguage);
            Message = UiText.Get("语言设置未能读取，已跟随系统；原文件保留。");
        }
        initialized = true; Notify();
    }
    public Task ChangeAsync(string value)
    {
        if (!CanChange || value == Selected) return Task.CompletedTask;
        if (!UiText.ValidChoice(value)) throw new ArgumentException("Unsupported UI language");
        saving = true; Message = UiText.Get("正在保存…"); Notify();
        return write = SaveAsync(value);
    }
    private async Task SaveAsync(string value)
    {
        try { await store.SaveAsync(new(value)); Selected = value; Message = UiText.Get("已保存。语言将在正常退出并重新打开后生效；当前编辑和计时不受影响。"); }
        catch (Exception) { Message = UiText.Get("语言设置未能保存，仍使用原选项；请重试。"); }
        finally { saving = false; if (!disposed) Notify(); }
    }
    private void Notify() { Changed(nameof(Selected)); Changed(nameof(Effective)); Changed(nameof(CanChange)); Changed(nameof(Message)); }
    public async ValueTask DisposeAsync() { disposed = true; await write; }
}
