using System.ComponentModel;
using Microsoft.UI.Xaml;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class DetailsPage
{
    private NextMoeSettingsViewModel? personalSync;
    private CancellationTokenSource? personalFactsRead;
    internal void ConfigurePersonalSync(NextMoeSettingsViewModel sync)
    {
        personalSync = sync;
        Loaded += (_, _) => { sync.PropertyChanged += OnPersonalSync; _ = RefreshPersonalFactsAsync(); };
        Unloaded += (_, _) => { sync.PropertyChanged -= OnPersonalSync; personalFactsRead?.Cancel(); CloudPersonalSection.Visibility = Visibility.Collapsed; };
    }
    private void OnPersonalSync(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(NextMoeSettingsViewModel.Status) or nameof(NextMoeSettingsViewModel.LastSync)) _ = RefreshPersonalFactsAsync(); }
    private async Task RefreshPersonalFactsAsync()
    {
        personalFactsRead?.Cancel(); CloudPersonalSection.Visibility = Visibility.Collapsed;
        if (!IsLoaded || personalSync is null || ViewModel.Draft?.Source.Metadata is not { } work) return;
        using var stop = new CancellationTokenSource(); personalFactsRead = stop;
        try
        {
            var facts = await personalSync.ImportedFactsAsync(work.Id, stop.Token);
            if (stop.IsCancellationRequested || !IsLoaded || ViewModel.Draft?.Source.Metadata?.Id != work.Id || facts is null) return;
            var values = new List<string>();
            if (facts.Minutes is { } minutes) values.Add($"云端累计时长：{minutes / 60} 小时 {minutes % 60} 分钟");
            if (facts.Rating is { } rating) values.Add($"NextMoe 个人评分：{rating} / 10");
            if (facts.State is { } status) values.Add("云端状态：" + StatusNames.Display(PersonalSyncPlanning.LocalStatus(status))
                + (facts.Completion switch { "one_route" => " · 已完成一条线路", "main" => " · 已完成主线", "all" => " · 全部完成", _ => "" }));
            CloudPersonalText.Text = string.Join("\n", values); CloudPersonalSection.Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch { if (!stop.IsCancellationRequested) { CloudPersonalText.Text = "云端个人记录读取未完成，请在设置中重试。"; CloudPersonalSection.Visibility = Visibility.Visible; } }
        finally { if (ReferenceEquals(personalFactsRead, stop)) personalFactsRead = null; }
    }
}
