using Microsoft.UI.Xaml;

namespace TheOrder.WinUI.Views;

public sealed partial class SettingsPage
{
    private async void OnBangumiQuickLogin(object sender, RoutedEventArgs e)
    { BangumiToken.Password = ""; BangumiEditor.Visibility = Visibility.Collapsed; await Bangumi.SignInAsync(); }
    private void OnBangumiAuthorize(object sender, RoutedEventArgs e)
    { BangumiEditor.Visibility = Visibility.Visible; BangumiToken.Focus(FocusState.Programmatic); }
    private async void OnBangumiSave(object sender, RoutedEventArgs e)
    {
        var token = BangumiToken.Password; BangumiToken.Password = "";
        await Bangumi.SaveAsync(token);
        if (IsLoaded && Bangumi.HasAuthorization && !Bangumi.Access.Rejected && Bangumi.Message.StartsWith("授权已保存", StringComparison.Ordinal))
            BangumiEditor.Visibility = Visibility.Collapsed;
    }
    private void OnBangumiCancelEditor(object sender, RoutedEventArgs e)
    { Bangumi.Cancel(); BangumiToken.Password = ""; BangumiEditor.Visibility = Visibility.Collapsed; }
    private async void OnBangumiTest(object sender, RoutedEventArgs e) => await Bangumi.TestAsync();
    private async void OnBangumiRemove(object sender, RoutedEventArgs e)
    { BangumiToken.Password = ""; BangumiEditor.Visibility = Visibility.Collapsed; await Bangumi.ClearAsync(); }
    private void OnBangumiCancel(object sender, RoutedEventArgs e) => Bangumi.Cancel();
}
