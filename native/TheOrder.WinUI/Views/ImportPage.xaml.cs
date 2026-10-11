using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;
public sealed partial class ImportPage : Page
{
    public FilesViewModel ViewModel { get; }
    public WorkspaceViewModel Workspace { get; }
    public RecognitionViewModel Recognition { get; }
    public Func<Task<string?>>? ChooseFolder { get; set; }
    public ImportPage(FilesViewModel viewModel, WorkspaceViewModel workspace, RecognitionViewModel recognition)
    { ViewModel = viewModel; Workspace = workspace; Recognition = recognition; InitializeComponent(); }
    public Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private async void OnChoose(object sender, RoutedEventArgs e)
    { try { if (ChooseFolder is not null && await ChooseFolder() is { } path) await ViewModel.ScanAsync(path); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelScan();
    private async void OnRetry(object sender, RoutedEventArgs e)
    { try { await ViewModel.RetryScanAsync(); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private void OnSelectAll(object sender, RoutedEventArgs e) { CandidateList.SelectedItems.Clear(); foreach (var item in ViewModel.Candidates.Where(c => c.Preflight.CanAttach)) CandidateList.SelectedItems.Add(item); }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = CandidateList.SelectedItems.OfType<ScanCandidate>().ToImmutableArray(); if (selected.IsEmpty) return;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = UiText.Format($"导入 {selected.Length} 个候选？"), Content = UiText.Get("保存到当前书架后立即自动识别；排序分严格大于 0.9、身份与版本一致且唯一领先时自动应用，歧义或证据不足的结果在作品详情中选择。重复目录跳过，不会复制、执行或删除游戏文件。"), PrimaryButtonText = UiText.Get("确认导入"), CloseButtonText = UiText.Get("返回核对"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ImportAsync(selected);
        }
        catch (Exception) { Workspace.ReportUnexpected(); }
    }
}
