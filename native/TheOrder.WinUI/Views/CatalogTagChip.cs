using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.WinUI.Views;

// Work tags and character traits share their vocabulary-independent visual treatment.
internal static class CatalogTagChip
{
    internal static Border Create(string name, CatalogEntityRef? entity, Func<CatalogEntityRef, Task>? browse)
    {
        var label = new TextBlock { Text = name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CatalogTagTextStyle"] };
        UIElement content = label;
        if (entity is not null && TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(entity.Id))
        {
            var link = new HyperlinkButton { Content = label, FontSize = 14, Padding = new Thickness(0), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, name + UiText.Get("，资料详情"));
            ToolTipService.SetToolTip(link, name);
            link.Click += async (_, _) => { if (browse is not null) await browse(entity); };
            content = link;
        }
        return new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CatalogTagChipStyle"], Child = content };
    }
}
