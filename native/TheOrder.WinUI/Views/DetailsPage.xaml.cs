using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class DetailsPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    public DetailsPage(WorkspaceViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); }
    public string DirtyText(bool dirty) => dirty ? "有尚未保存的修改" : "当前资料已保存";
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelDraft();
        if (ViewModel.SelectedId is { } id) await ViewModel.NavigateAsync(WorkspacePage.Details, id, () => Task.FromResult(UnsavedChoice.Stay));
    }
}
