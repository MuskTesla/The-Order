using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TheOrder.Application;
namespace TheOrder.WinUI.Localization;

// XAML declares the canonical label beside the element. Stable automation IDs,
// data bindings, user content and command handlers are never translated.
public sealed class Localize : DependencyObject
{
    private static DependencyProperty Register(string name) => DependencyProperty.RegisterAttached(name, typeof(string), typeof(Localize),
        new PropertyMetadata(null, (target, args) => Apply(target, name, UiText.Get((string?)args.NewValue ?? ""))));
    private static void Apply(DependencyObject target, string name, string text)
    {
        if (name == "Name") { AutomationProperties.SetName(target, text); return; }
        if (name == "ToolTip") { ToolTipService.SetToolTip(target, text); return; }
        for (var type = target.GetType(); type is not null; type = type.BaseType)
            if ((type.GetProperty(name + "Property", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetValue(null)
                ?? type.GetField(name + "Property", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetValue(null)) is DependencyProperty property)
            { target.SetValue(property, text); return; }
        throw new InvalidOperationException("Unsupported localized XAML property: " + target.GetType().Name + "." + name);
    }
    public static readonly DependencyProperty TextProperty = Register("Text");
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty); public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);
    public static readonly DependencyProperty ContentProperty = Register("Content");
    public static string GetContent(DependencyObject d) => (string)d.GetValue(ContentProperty); public static void SetContent(DependencyObject d, string v) => d.SetValue(ContentProperty, v);
    public static readonly DependencyProperty HeaderProperty = Register("Header");
    public static string GetHeader(DependencyObject d) => (string)d.GetValue(HeaderProperty); public static void SetHeader(DependencyObject d, string v) => d.SetValue(HeaderProperty, v);
    public static readonly DependencyProperty PlaceholderTextProperty = Register("PlaceholderText");
    public static string GetPlaceholderText(DependencyObject d) => (string)d.GetValue(PlaceholderTextProperty); public static void SetPlaceholderText(DependencyObject d, string v) => d.SetValue(PlaceholderTextProperty, v);
    public static readonly DependencyProperty NameProperty = Register("Name");
    public static string GetName(DependencyObject d) => (string)d.GetValue(NameProperty); public static void SetName(DependencyObject d, string v) => d.SetValue(NameProperty, v);
    public static readonly DependencyProperty ToolTipProperty = Register("ToolTip");
    public static string GetToolTip(DependencyObject d) => (string)d.GetValue(ToolTipProperty); public static void SetToolTip(DependencyObject d, string v) => d.SetValue(ToolTipProperty, v);
    public static readonly DependencyProperty OnContentProperty = Register("OnContent");
    public static string GetOnContent(DependencyObject d) => (string)d.GetValue(OnContentProperty); public static void SetOnContent(DependencyObject d, string v) => d.SetValue(OnContentProperty, v);
    public static readonly DependencyProperty OffContentProperty = Register("OffContent");
    public static string GetOffContent(DependencyObject d) => (string)d.GetValue(OffContentProperty); public static void SetOffContent(DependencyObject d, string v) => d.SetValue(OffContentProperty, v);
    public static readonly DependencyProperty TitleProperty = Register("Title");
    public static string GetTitle(DependencyObject d) => (string)d.GetValue(TitleProperty); public static void SetTitle(DependencyObject d, string v) => d.SetValue(TitleProperty, v);
    public static readonly DependencyProperty MessageProperty = Register("Message");
    public static string GetMessage(DependencyObject d) => (string)d.GetValue(MessageProperty); public static void SetMessage(DependencyObject d, string v) => d.SetValue(MessageProperty, v);
    public static readonly DependencyProperty PrimaryButtonTextProperty = Register("PrimaryButtonText");
    public static string GetPrimaryButtonText(DependencyObject d) => (string)d.GetValue(PrimaryButtonTextProperty); public static void SetPrimaryButtonText(DependencyObject d, string v) => d.SetValue(PrimaryButtonTextProperty, v);
    public static readonly DependencyProperty SecondaryButtonTextProperty = Register("SecondaryButtonText");
    public static string GetSecondaryButtonText(DependencyObject d) => (string)d.GetValue(SecondaryButtonTextProperty); public static void SetSecondaryButtonText(DependencyObject d, string v) => d.SetValue(SecondaryButtonTextProperty, v);
    public static readonly DependencyProperty CloseButtonTextProperty = Register("CloseButtonText");
    public static string GetCloseButtonText(DependencyObject d) => (string)d.GetValue(CloseButtonTextProperty); public static void SetCloseButtonText(DependencyObject d, string v) => d.SetValue(CloseButtonTextProperty, v);
}
