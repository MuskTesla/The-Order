using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed record CoverOption(string? Id, string Name, string? ImageKey, string Description);

public sealed partial class CoverSelectionDialog : ContentDialog, IImageHost
{
    public IImagePipeline Images { get; }
    public string? SelectedCoverId => (CoverList.SelectedItem as CoverOption)?.Id;
    public CoverSelectionDialog(OnlineCoverOptions options, IImagePipeline images, IRemoteCoverRegistry registry)
    {
        Images = images; InitializeComponent();
        List<CoverOption> choices = [new(null, "资料源默认封面", registry.Register(options.Work.CoverUrl, options.Work.FetchedAt), "恢复默认选图，不以票数或长宽比代替资料源选择。")];
        foreach (var cover in options.Work.Covers ?? [])
        {
            var key = registry.Register(cover.Url, options.Work.FetchedAt);
            if (key is null) continue;
            choices.Add(new(cover.Id, "作品封面 " + cover.Id, key,
                (cover.Width is > 0 && cover.Height is > 0 ? $"{cover.Width} × {cover.Height}" : "尺寸未知")
                + (cover.Votes is { } votes ? $" · {votes} 票" : "")));
        }
        CoverList.ItemsSource = choices;
        CoverList.SelectedItem = choices.FirstOrDefault(c => c.Id == options.Token.Choice?.Cover.Id) ?? choices[0];
    }
    private void OnSelection(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = CoverList.SelectedItem is CoverOption;
}
