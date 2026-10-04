using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.WinUI.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    private readonly CancellationToken lifetime;
    private bool initialLoad;
    public WorkspaceViewModel ViewModel { get; }
    public LibraryPage(WorkspaceViewModel viewModel, CancellationToken lifetime)
    {
        ViewModel = viewModel; this.lifetime = lifetime;
        InitializeComponent();
        Loaded += async (_, _) => { if (!initialLoad) { initialLoad = true; await ViewModel.LoadAsync(lifetime); } };
    }
    private async void OnReload(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync(lifetime);
}
