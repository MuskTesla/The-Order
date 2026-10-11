using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;
namespace TheOrder.WinUI.Views;
public sealed partial class RecognitionReview : UserControl, IImageHost
{
    public RecognitionViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    public RecognitionReview(RecognitionViewModel viewModel, IImagePipeline images)
    { ViewModel = viewModel; Images = images; InitializeComponent(); RemoteRichFacts.Images = images; ManualIdBox.KeyDown += OnManualKey; }
    private Func<string, Task>? openLink;
    public void ConfigureCatalog(IRemoteCoverRegistry registry, Func<string, Task> openLink) { this.openLink = openLink; RemoteRichFacts.Registry = registry; RemoteRichFacts.OpenLink = openLink; }
    private async void OnManualKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; ViewModel.ManualId = ManualIdBox.Text; await ViewModel.OpenDetailsAsync(ViewModel.ManualId); } }
    private void OnCandidate(object sender, SelectionChangedEventArgs e) { if (e.AddedItems.FirstOrDefault() is RecognitionCandidateCard card) ViewModel.SelectCandidate(card.Id); }
    private async void OnRemoteDetails(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) await ViewModel.OpenDetailsAsync(id); }
    private async void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Tag: string url } || !CandidateDetailLinks.Allowed(url)) return;
        try
        {
            if (openLink is not null) await openLink(url); else if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(url))) ViewModel.ReportLinkFailure();
        }
        catch (Exception) { ViewModel.ReportLinkFailure(); }
    }
    private async void OnRerun(object sender, RoutedEventArgs e) => await ViewModel.RerunAsync();
    private async void OnIgnore(object sender, RoutedEventArgs e) => await ViewModel.IgnoreAsync();
    private async void OnApply(object sender, RoutedEventArgs e) => await ViewModel.ConfirmSelectionAsync();
    private async void OnManual(object sender, RoutedEventArgs e) => await ViewModel.ManualLinkAsync();

}
