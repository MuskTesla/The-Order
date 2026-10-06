using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Presentation;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed partial class DetailsPage : Page, IImageHost
{
    public WorkspaceViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    public LaunchViewModel Launch { get; }
    public Func<Task>? ManageInstallations { get; set; }
    public Func<Task>? RefreshOnline { get; set; }
    public Func<Task>? Recognize { get; set; }
    private async void OnRecognize(object sender, RoutedEventArgs e) { try { if (Recognize is not null) await Recognize(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    public Func<Task>? RemoveWishlist { get; set; }
    private async void OnRefreshOnline(object sender, RoutedEventArgs e) { try { if (RefreshOnline is not null) await RefreshOnline(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnRemoveWishlist(object sender, RoutedEventArgs e) { try { if (RemoveWishlist is not null) await RemoveWishlist(); } catch { ViewModel.ReportUnexpected(); } }
    public Func<Task>? ChangeCover { get; set; }
    private async void OnInstallations(object sender, RoutedEventArgs e) { try { if (ManageInstallations is not null) await ManageInstallations(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    private async void OnCover(object sender, RoutedEventArgs e) { try { if (ChangeCover is not null) await ChangeCover(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    public DetailsPage(WorkspaceViewModel viewModel, IImagePipeline images, LaunchViewModel launch)
    {
        ViewModel = viewModel; Images = images; Launch = launch; InitializeComponent();
        Loaded += async (_, _) => { try { if (ViewModel.SelectedId is { } id) await Launch.OpenAsync(id); } catch { ViewModel.ReportUnexpected(); } };
        Unloaded += (_, _) => Launch.Leave();
    }
    private async void OnLaunch(object sender, RoutedEventArgs e) { try { await Launch.LaunchAsync(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnConfirmPosition(object sender, RoutedEventArgs e) { try { await Launch.ConfirmAsync(); } catch { ViewModel.ReportUnexpected(); } }
    private async void OnLaunchInstallation(object sender, SelectionChangedEventArgs e)
    { try { if (LaunchInstallation.SelectedItem is LaunchInstallationItem item) await Launch.SelectAsync(item.Id); } catch { ViewModel.ReportUnexpected(); } }
    private void OnCancelLaunch(object sender, RoutedEventArgs e) => Launch.Cancel();
    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reflow the existing controls; drafts, launch state and image ownership stay in place.
        VisualStateManager.GoToState(this, e.NewSize.Width < 720 ? "DetailsNarrow" : "DetailsWide", false);
    }
    public string StatusLabel(string status) => "当前状态 · " + StatusNames.Display(status);
    public string AlternateTitle(string primary, string other) => string.Equals(primary?.Trim(), other?.Trim(), StringComparison.Ordinal) ? "" : other ?? "";
    public Visibility AlternateTitleVisible(string primary, string other) => string.IsNullOrWhiteSpace(AlternateTitle(primary, other)) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasInstallations(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoInstallations(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisible(bool enabled) => enabled ? Visibility.Visible : Visibility.Collapsed;
    public string DirtyText(bool dirty) => dirty ? "有尚未保存的修改" : "当前资料已保存";
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        await ViewModel.CancelAndReloadDetailsAsync();
    }
}
