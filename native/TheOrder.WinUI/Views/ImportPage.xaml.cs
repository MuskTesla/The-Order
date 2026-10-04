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
    public Func<Task<string?>>? ChooseFolder { get; set; }
    public ImportPage(FilesViewModel viewModel, WorkspaceViewModel workspace) { ViewModel = viewModel; Workspace = workspace; InitializeComponent(); }
    public Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private async void OnChoose(object sender, RoutedEventArgs e)
    { try { if (ChooseFolder is not null && await ChooseFolder() is { } path) await ViewModel.ScanAsync(path); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelScan();
    private void OnSelectAll(object sender, RoutedEventArgs e) { CandidateList.SelectedItems.Clear(); foreach (var item in ViewModel.Candidates.Where(c => c.Preflight.CanAttach)) CandidateList.SelectedItems.Add(item); }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = CandidateList.SelectedItems.OfType<ScanCandidate>().ToImmutableArray(); if (selected.IsEmpty) return;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"导入 {selected.Length} 个候选？", Content = "将一次保存到当前隔离书架。重复目录会跳过，不会复制、执行或删除游戏文件。", PrimaryButtonText = "确认导入", CloseButtonText = "返回核对", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ImportAsync(selected);
        }
        catch (Exception) { Workspace.ReportUnexpected(); }
    }
}
