using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using TheOrder.Application;

namespace TheOrder.WinUI;

public sealed partial class MainWindow
{
    private void OnAppUpdateState(object? sender, PropertyChangedEventArgs e)
    {
        if (updatesStopped || e.PropertyName != nameof(updates.HasUpdate)) return;
        UpdateBadge.Visibility = updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SettingsNavigation, UiText.Get(updates.HasUpdate ? "设置，有新版本可用" : "设置"));
    }
    private async void OnAutomaticUpdate(object? sender, object e)
    {
        if (updatesStopped) return;
        if (updateTimer is not null) updateTimer.Interval = TimeSpan.FromHours(12);
        await updates.CheckAutomaticallyAsync();
    }
}
