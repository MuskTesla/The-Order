using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;
public sealed partial class OnlinePage : Page, IImageHost
{
    public OnlineViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    private string? selected;
    public OnlinePage(OnlineViewModel viewModel, IImagePipeline images) { ViewModel = viewModel; Images = images; InitializeComponent(); }
    public Visibility BusyVisibility(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;
    private async void OnSearch(object sender, RoutedEventArgs e) => await ViewModel.SearchAsync();
    private async void OnMore(object sender, RoutedEventArgs e) => await ViewModel.SearchAsync(true);
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.Leave();
    private async void OnWork(object sender, ItemClickEventArgs e) { if (e.ClickedItem is OnlineCard card) { selected = card.Id; await ViewModel.OpenDetailsAsync(card.Id); } }
    private async void OnRefresh(object sender, RoutedEventArgs e) { if (selected is { } id) await ViewModel.OpenDetailsAsync(id, true); }
    private async void OnAdd(object sender, RoutedEventArgs e) => await ViewModel.AddWishlistAsync();
}
