using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Presentation;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed partial class DetailsPage : Page, IImageHost
{
    public WorkspaceViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    public Func<Task>? ManageInstallations { get; set; }
    public Func<Task>? ChangeCover { get; set; }
    private async void OnInstallations(object sender, RoutedEventArgs e) { try { if (ManageInstallations is not null) await ManageInstallations(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    private async void OnCover(object sender, RoutedEventArgs e) { try { if (ChangeCover is not null) await ChangeCover(); } catch (Exception) { ViewModel.ReportUnexpected(); } }
    public DetailsPage(WorkspaceViewModel viewModel, IImagePipeline images) { ViewModel = viewModel; Images = images; InitializeComponent(); }
    public string DirtyText(bool dirty) => dirty ? "有尚未保存的修改" : "当前资料已保存";
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        await ViewModel.CancelAndReloadDetailsAsync();
    }
}
