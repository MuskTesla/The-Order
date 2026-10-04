using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;
public sealed partial class InstallationsPage : Page
{
    public FilesViewModel ViewModel { get; }
    public WorkspaceViewModel Workspace { get; }
    public Func<Task<string?>>? ChooseFolder { get; set; }
    public Func<Task<string?>>? ChooseExecutable { get; set; }
    public InstallationsPage(FilesViewModel viewModel, WorkspaceViewModel workspace) { ViewModel = viewModel; Workspace = workspace; InitializeComponent(); }
    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private InstallationRow? Selected => InstallationList.SelectedItem as InstallationRow;
    private void OnAdd(object sender, RoutedEventArgs e) => ViewModel.Edit(null);
    private void OnEdit(object sender, RoutedEventArgs e) { if (Selected is { } row) ViewModel.Edit(row); }
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelDraft();
    private async void OnFolder(object sender, RoutedEventArgs e) => await Pick(ChooseFolder, value => ViewModel.Draft!.Folder = value);
    private async void OnExecutable(object sender, RoutedEventArgs e) => await Pick(ChooseExecutable, value => ViewModel.Draft!.Executable = value);
    private async void OnWorking(object sender, RoutedEventArgs e) => await Pick(ChooseFolder, value => ViewModel.Draft!.WorkingDirectory = value);
    private async Task Pick(Func<Task<string?>>? picker, Action<string> apply)
    { try { if (picker is not null && await picker() is { } value && ViewModel.Draft is not null) apply(value); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private void OnArgument(object sender, RoutedEventArgs e) { if (ViewModel.Draft is { } draft && draft.Arguments.Count < 100) draft.Arguments.Add(new("")); }
    private void OnRemoveArgument(object sender, RoutedEventArgs e) { if (sender is Button { Tag: ArgumentDraft arg }) ViewModel.Draft?.Arguments.Remove(arg); }
    private async void OnSave(object sender, RoutedEventArgs e) { try { await ViewModel.SaveAsync(); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private async void OnPrefer(object sender, RoutedEventArgs e) { try { if (Selected is { } row) await ViewModel.PreferAsync(row); } catch (Exception) { Workspace.ReportUnexpected(); } }
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected is not { } row) return;
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除安装关系？", Content = "只从书架移除此安装记录。不会删除文件、目录或卸载游戏。", PrimaryButtonText = "移除关系", CloseButtonText = "保留", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.RemoveAsync(row);
        }
        catch (Exception) { Workspace.ReportUnexpected(); }
    }
}
