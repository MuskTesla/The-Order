using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

public sealed record CoverOption(string? Id, string Name, string? ImageKey, string Description, bool Available = true);

public sealed partial class CoverSelectionDialog : ContentDialog, IImageHost
{
    public IImagePipeline Images { get; }
    public string? SelectedCoverId => (CoverList.SelectedItem as CoverOption)?.Id;
    public CoverSelectionDialog(OnlineCoverOptions options, IImagePipeline images, IRemoteCoverRegistry registry)
    {
        Images = images; InitializeComponent();
        var recommended = options.Recommendation;
        List<CoverOption> choices = [new(null, UiText.Get("自动推荐"), registry.Register(recommended?.Url, options.Work.FetchedAt),
            recommended is { Ready: true } ? UiText.Get("综合构图与清晰度选择；预览与书架使用同一结果。") : UiText.Get("正在准备自动推荐，请稍后重新打开；也可直接选择下方封面。"), recommended is { Ready: true })];
        foreach (var cover in options.Work.Covers ?? [])
        {
            var key = registry.Register(cover.Url, options.Work.FetchedAt);
            if (key is null) continue;
            choices.Add(new(cover.Id, cover.Url == options.Work.CoverUrl ? UiText.Get("资料源默认封面") : UiText.Get("作品封面 ") + cover.Id, key,
                (cover.Width is > 0 && cover.Height is > 0 ? $"{cover.Width} × {cover.Height}" : UiText.Get("尺寸未知"))
                + (cover.Votes is { } votes ? UiText.Format($" · {votes} 票") : "")));
        }
        CoverList.ItemsSource = choices;
        CoverList.SelectedItem = choices.FirstOrDefault(c => c.Id == options.Token.Choice?.Cover.Id) ?? choices[0];
    }
    private void OnSelection(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = CoverList.SelectedItem is CoverOption { Available: true };
}
