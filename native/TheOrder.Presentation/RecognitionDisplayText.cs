using TheOrder.Application;

namespace TheOrder.Presentation;

// Saved reasons remain canonical evidence. Translate only the displayed explanation,
// including older pending results, without changing their ranking or stored bytes.
public static class RecognitionDisplayText
{
    public static string Message(string text)
    {
        var translated = UiText.Get(text);
        if (translated != text || !text.Contains('；')) return translated;
        return string.Join(UiText.Language == "en" ? "; " : "；", text.Split('；').Select(UiText.Get));
    }

    public static string Reason(string text, bool highConfidence) => Message(highConfidence ? text
        : text.Replace("完整官方标题唯一对应", "完整官方标题与本地名称一致", StringComparison.Ordinal));
}
