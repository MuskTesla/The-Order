using Microsoft.UI.Xaml;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage
{
    private async void OnSaveLaunchDefaults(object sender, RoutedEventArgs e)
    {
        await LaunchDefaults.SaveAsync();
        if (!LaunchDefaults.HasChanges) return;
        if (LaunchDefaults.LocaleEmulatorEnabled && string.IsNullOrWhiteSpace(LaunchDefaults.LocaleEmulatorPath)) GlobalLocaleToolSection.IsExpanded = true;
        if (LaunchDefaults.MagpieEnabled && string.IsNullOrWhiteSpace(LaunchDefaults.MagpiePath)) GlobalMagpieToolSection.IsExpanded = true;
    }
    private void OnCancelLaunchDefaults(object sender, RoutedEventArgs e) => LaunchDefaults.Cancel();
    private async void OnChooseGlobalLocale(object sender, RoutedEventArgs e) => await ChooseGlobalHelper(true);
    private async void OnChooseGlobalMagpie(object sender, RoutedEventArgs e) => await ChooseGlobalHelper(false);
    private async Task ChooseGlobalHelper(bool locale)
    {
        if (!LaunchDefaults.CanChange || ChooseLaunchHelper is null) return;
        try {
            var path = await ChooseLaunchHelper();
            if (path is null || !IsLoaded || !LaunchDefaults.CanChange) return;
            if (locale) LaunchDefaults.LocaleEmulatorPath = path; else LaunchDefaults.MagpiePath = path;
        } catch { ViewModel.ReportUnexpected(); }
    }
}
