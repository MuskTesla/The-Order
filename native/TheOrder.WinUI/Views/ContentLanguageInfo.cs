using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;

namespace TheOrder.WinUI.Views;

// One shared, lazy original/source disclosure across media. No fetch, rewrite or
// translation is performed when it expands; source strings remain unchanged.
public sealed class ContentLanguageInfo : StackPanel
{
    private readonly Expander section = new() { Header = "原文与资料语言", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ContentSelection[] selections = [];
    private string? rendered;
    public bool IsExpanded { get => section.IsExpanded; set => section.IsExpanded = value; }
    internal object Evidence => new { preference = ContentLanguage.Current, expanded = IsExpanded,
        fields = selections.Select(x => new { selected = x.Selected, original = x.Original, variantCount = x.Variants.Length }).ToArray() };
    public ContentLanguageInfo()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch; Children.Add(section);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(section, "ContentLanguageOriginals");
        section.Expanding += (_, _) => Render();
    }
    public void Show(params ContentSelection[] values)
    {
        selections = values.Where(x => !x.Variants.IsEmpty).ToArray();
        Visibility = selections.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsExpanded) Render();
    }
    private void Render()
    {
        var key = ContentLanguage.Current + "/" + string.Join("\u001f", selections.SelectMany(x => x.Variants).Select(x => x.Identity + "/" + x.Field + "/" + x.Language + "/" + x.Text));
        if (key == rendered) return;
        rendered = key;
        var body = new StackPanel { Spacing = 12, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var selection in selections)
        {
            body.Children.Add(new TextBlock { Text = selection.Selected is null ? "以下译文未自动采用" : "当前显示 · " + selection.Caption, FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
            foreach (var variant in selection.Variants.DistinctBy(x => (x.Identity, x.Field, x.Language, x.Text)))
            {
                var field = variant.Field switch { "title" => "标题", "synopsis" => "简介", "name" => "姓名", "aliases" => "别名", _ => variant.Field };
                var label = field + " · " + ContentLanguage.SourceName(variant) + " · " + ContentLanguage.LanguageName(variant.Language)
                    + (variant.IsMachine ? " · 机器翻译（未自动采用）" : variant.IsOriginal ? " · 原文" : "");
                var caption = new TextBlock { Text = label, Opacity = .7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                ToolTipService.SetToolTip(caption, variant.Identity.Provider + " / " + variant.Identity.Resource + " / " + variant.Identity.Id);
                var text = new TextBlock { Text = variant.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                body.Children.Add(new StackPanel { Spacing = 5, Children = { caption, text } });
            }
        }
        section.Content = body;
    }
}
