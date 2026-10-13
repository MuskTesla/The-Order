using TheOrder.Application;
using TheOrder.Platform.Windows;
namespace TheOrder.WinUI;
public sealed partial class MainWindow
{
    private readonly RecognitionPreferencesStore recognitionPreferences;
    private bool recognitionPreferencesLoaded, recognitionPreferencesReadOnly;
    private Task recognitionPreferenceWrite = Task.CompletedTask;
    private RecognitionPreference? pendingRecognitionPreference;
    private async Task LoadRecognitionPreferencesAsync()
    {
        try {
            var saved = await recognitionPreferences.LoadAsync();
            recognition.UseOnline = saved.Preference.UseOnline; recognition.IncludePe = saved.Preference.IncludePe;
            recognitionPreferencesReadOnly = saved.ReadOnly;
            if (saved.Warning is not null) ShowLifecycleError(saved.Warning);
        } catch (Exception error) { App.TraceVisualFailure(error); ShowLifecycleError(UiText.Get("识别设置未能读取，已保留原文件并使用默认选项。")); recognitionPreferencesReadOnly = true; }
        recognitionPreferencesLoaded = true;
    }
    private void QueueRecognitionPreferences()
    {
        pendingRecognitionPreference = new(recognition.UseOnline, recognition.IncludePe);
        if (recognitionPreferenceWrite.IsCompleted) recognitionPreferenceWrite = WriteRecognitionPreferencesAsync();
    }
    private async Task WriteRecognitionPreferencesAsync()
    {
        // Coalesce rapid UI changes. Only the active value and the latest pending value are retained.
        await Task.Yield();
        while (pendingRecognitionPreference is { } value)
        {
            pendingRecognitionPreference = null;
            try { await recognitionPreferences.SaveAsync(value); }
            catch (Exception error) { App.TraceVisualFailure(error); ShowLifecycleError(UiText.Get("识别选项未保存，请检查资料目录后重试；原文件已保留。")); }
        }
    }
}
