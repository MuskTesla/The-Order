using System.ComponentModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TheOrder.Application;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    public WorkspaceViewModel ViewModel { get; }
    public Func<string, Task>? OpenDetails { get; set; }
    public Func<Task<bool>>? ResolveUnsaved { get; set; }
    private bool synchronizing = true;
    private readonly bool diagnostics;
    private readonly Stopwatch display = Stopwatch.StartNew();
    private readonly ConditionalWeakTable<object, object> seenContainers = new();
    private int createdContainers;
    private double firstDisplay;
    private int maxRealized;
    private ListViewBase Active => ViewModel.Layout == "grid" ? GameGrid : GameList;
    public LibraryPage(WorkspaceViewModel viewModel, bool diagnostics)
    {
        ViewModel = viewModel; this.diagnostics = diagnostics; InitializeComponent();
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        Synchronize();
    }
    public Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnState; Synchronize();
        if (diagnostics) { DiagnosticsText.Visibility = Visibility.Visible; LayoutUpdated += OnLayout; }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) { ViewModel.PropertyChanged -= OnState; LayoutUpdated -= OnLayout; }
    private void OnState(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(WorkspaceViewModel.Items) or nameof(WorkspaceViewModel.Layout) or nameof(WorkspaceViewModel.Query)) Synchronize(); }
    private static string Value(ComboBox box, string fallback = "all") => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
    private static void Select(ComboBox box, string value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string ?? "") == value);
    private void Synchronize()
    {
        synchronizing = true;
        var q = ViewModel.Query;
        SearchBox.Text = q.Text; CategoryFilter.Text = q.Category; TagFilter.Text = q.Tag; FavoriteFilter.IsChecked = q.Favorites;
        Select(ViewChoice, q.View); Select(SortChoice, q.Sort); Select(StatusChoice, q.Status); Select(MinimumChoice, q.MinRating);
        var grid = ViewModel.Layout == "grid";
        GameGrid.Visibility = Visible(grid); GameList.Visibility = Visible(!grid);
        if (grid) { GameList.ItemsSource = null; if (!ReferenceEquals(GameGrid.ItemsSource, ViewModel.Items)) GameGrid.ItemsSource = ViewModel.Items; }
        else { GameGrid.ItemsSource = null; if (!ReferenceEquals(GameList.ItemsSource, ViewModel.Items)) GameList.ItemsSource = ViewModel.Items; }
        synchronizing = false;
    }
    private Task ApplyQuery() => synchronizing ? Task.CompletedTask : ViewModel.SetQueryAsync(new(SearchBox.Text, Value(ViewChoice), Value(StatusChoice), FavoriteFilter.IsChecked == true,
        CategoryFilter.Text, TagFilter.Text, Value(MinimumChoice, ""), Value(SortChoice, "added")));
    private async void OnSearch(object sender, TextChangedEventArgs e) => await ApplyQuery();
    private async void OnFilter(object sender, SelectionChangedEventArgs e) => await ApplyQuery();
    private async void OnFavoriteFilter(object sender, RoutedEventArgs e) => await ApplyQuery();
    private async void OnGrid(object sender, RoutedEventArgs e) => await ViewModel.ChangeAppearanceAsync(ViewModel.Theme, "grid");
    private async void OnList(object sender, RoutedEventArgs e) => await ViewModel.ChangeAppearanceAsync(ViewModel.Theme, "list");
    private async void OnReload(object sender, RoutedEventArgs e) { if (ResolveUnsaved is null || await ResolveUnsaved()) await ViewModel.ReloadAsync(); }
    private async Task OpenSelected() { if (Active.SelectedItem is GameSummary item && OpenDetails is not null) await OpenDetails(item.Id); }
    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenSelected();
    private async void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenSelected();
    private async void OnItemsKey(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await OpenSelected(); } }
    private async void OnClear(object sender, RoutedEventArgs e) { SavedChoice.SelectedItem = null; await ViewModel.SetQueryAsync(new(Sort: ViewModel.Query.Sort)); }
    private async void OnSavedFilter(object sender, SelectionChangedEventArgs e) { if (SavedChoice.SelectedItem is SavedFilter saved) await ViewModel.SetQueryAsync(saved.Query); }
    private async void OnSaveFilter(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "名称", PlaceholderText = "例如：周末想玩的作品", MaxLength = 80 };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "保存当前筛选", Content = name, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.SaveFilterAsync(name.Text);
    }
    private async void OnBatch(object sender, RoutedEventArgs e)
    {
        var ids = Active.SelectedItems.Cast<GameSummary>().Select(i => i.Id).ToImmutableArray();
        if (ids.Length is < 1 or > 1000)
        { await new ContentDialog { XamlRoot = XamlRoot, Title = "请选择作品", Content = "批量修改支持 1 至 1000 个条目。可使用卡片勾选框多选。", CloseButtonText = "知道了" }.ShowAsync(); return; }
        var status = new ComboBox { Header = "游玩状态", ItemsSource = new[] { "保持不变", "未开始", "在玩", "已完成", "搁置", "放弃" }, SelectedIndex = 0 };
        var favorite = new ComboBox { Header = "收藏", ItemsSource = new[] { "保持不变", "收藏", "取消收藏" }, SelectedIndex = 0 };
        var categories = new TextBox { Header = "分类（逗号或换行分隔）" }; var tags = new TextBox { Header = "个人标签（逗号或换行分隔）" };
        var remove = new CheckBox { Content = "移除这些分类和标签（默认追加）" };
        var panel = new StackPanel { Spacing = 12 }; foreach (var element in new UIElement[] { status, favorite, categories, tags, remove }) panel.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"批量修改 {ids.Length} 部作品", Content = panel, PrimaryButtonText = "保存一次修改", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.ApplyBatchAsync(ids, PreferenceValidation.Statuses[status.SelectedIndex], new[] { "keep", "yes", "no" }[favorite.SelectedIndex], categories.Text, tags.Text, remove.IsChecked == true);
    }
    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!diagnostics || args.InRecycleQueue) return;
        if (!seenContainers.TryGetValue(args.ItemContainer, out _)) { seenContainers.Add(args.ItemContainer, new object()); createdContainers++; }
        if (firstDisplay == 0) firstDisplay = display.Elapsed.TotalMilliseconds;
    }
    private void OnLayout(object? sender, object e)
    {
        var realized = Active.ItemsPanelRoot?.Children.Count ?? 0; maxRealized = Math.Max(maxRealized, realized);
        var value = $"{ViewModel.Layout} | items={ViewModel.Items.Count} | realized={realized} | max={maxRealized} | containers={createdContainers} | first={firstDisplay:F0}ms | query={ViewModel.LastQueryMilliseconds:F1}ms";
        if (DiagnosticsText.Text != value) DiagnosticsText.Text = value;
    }
}
