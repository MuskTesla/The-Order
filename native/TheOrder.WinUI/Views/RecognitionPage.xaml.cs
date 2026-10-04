using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class RecognitionPage : Page, IImageHost
{
    public RecognitionViewModel ViewModel { get; }
    public IImagePipeline Images { get; }
    public Func<string, Task>? OpenLocalDetails { get; set; }
    private readonly DispatcherTimer updates = new() { Interval = TimeSpan.FromMilliseconds(100) };
    public RecognitionPage(RecognitionViewModel viewModel, IImagePipeline images)
    {
        ViewModel = viewModel; Images = images; InitializeComponent();
        updates.Tick += (_, _) => ViewModel.RefreshIfDirty();
        Loaded += async (_, _) => { updates.Start(); await ViewModel.InitializeAsync(); };
        Unloaded += (_, _) => updates.Stop();
    }
    private async void OnMissing(object sender, RoutedEventArgs e) => await ViewModel.StartAsync([], RecognitionMode.MissingMetadata);
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelAll();
    private void OnReview(object sender, SelectionChangedEventArgs e)
    { if (e.AddedItems.FirstOrDefault() is RecognitionReviewCard card && card.Id != ViewModel.SelectedId) { ViewModel.Select(card.Id); ReviewDetails.ChangeView(null, 0, null); } }
    private void OnCandidate(object sender, SelectionChangedEventArgs e) { if (e.AddedItems.FirstOrDefault() is RecognitionCandidateCard card) ViewModel.SelectCandidate(card.Id); }
    private async void OnRemoteDetails(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) await ViewModel.OpenDetailsAsync(id); }
    private async void OnRerun(object sender, RoutedEventArgs e) => await ViewModel.RerunAsync();
    private async void OnIgnore(object sender, RoutedEventArgs e) => await ViewModel.IgnoreAsync();
    private async void OnApply(object sender, RoutedEventArgs e) => await ViewModel.ApplyAsync();
    private async void OnManual(object sender, RoutedEventArgs e) => await ViewModel.ManualLinkAsync();
    private async void OnLocalDetails(object sender, RoutedEventArgs e)
    { try { if (ViewModel.SelectedGameId is { } id && OpenLocalDetails is not null) await OpenLocalDetails(id); } catch (Exception) { ViewModel.ReportLocalDetailsFailure(); } }
}
