using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TheOrder.Application;

// Simplified Chinese message keys are the canonical copy, not catalog/user data.
// One process owns one profile and one UI language. Tests use an async-local scope.
// No OS culture, credentials, personal text or recognition evidence is changed.
public static class UiText
{
    public const string System = "system";
    public static readonly string[] Choices = [System, "zh-Hans", "zh-Hant", "ja", "en"];
    private static readonly AsyncLocal<string?> testLanguage = new();
    private static string language = "zh-Hans";
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> resources = new(Load);
    public static string Language => testLanguage.Value ?? language;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language switch { "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", "ja" => "ja-JP", _ => "en-US" });
    public static bool ValidChoice(string value) => Choices.Contains(value, StringComparer.Ordinal);
    public static string Resolve(string preference, string systemLanguage)
    {
        if (!ValidChoice(preference)) throw new ArgumentException("Unsupported UI language", nameof(preference));
        if (preference != System) return preference;
        var tag = systemLanguage.Replace('_', '-').ToLowerInvariant();
        if (tag.StartsWith("zh")) return tag.Contains("hant") || tag.StartsWith("zh-tw") || tag.StartsWith("zh-hk") || tag.StartsWith("zh-mo") ? "zh-Hant" : "zh-Hans";
        if (tag.StartsWith("ja")) return "ja";
        return "en"; // Honest fallback for an unsupported system locale.
    }
    public static void Configure(string preference, string systemLanguage) => language = Resolve(preference, systemLanguage);
    public static IDisposable ForLanguage(string value)
    {
        if (!ValidChoice(value) || value == System) throw new ArgumentException("Explicit supported language required");
        var previous = testLanguage.Value; testLanguage.Value = value; return new Scope(() => testLanguage.Value = previous);
    }
    private sealed class Scope(Action exit) : IDisposable { public void Dispose() => exit(); }
    public static string Get(string key) => Translate(key, Language);
    public static string Translate(string key, string target)
    {
        if (target == "zh-Hans" || key.Length == 0) return key;
        return resources.Value.TryGetValue(target, out var table) && table.TryGetValue(key, out var value) ? value : key;
    }
    public static string Format(FormattableString message) => string.Format(Culture, Get(message.Format), message.GetArguments());
    // Keep this entry separate: a string overload would win over FormattableString
    // for $"..." and translate an already formatted value instead of its key.
    public static string FormatKey(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);
    public static IReadOnlyDictionary<string, string> Messages(string target) => resources.Value[target];
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var assembly = typeof(UiText).Assembly;
        foreach (var target in Choices.Where(t => t is not (System or "zh-Hans")))
        {
            using var stream = assembly.GetManifestResourceStream("TheOrder.Application.Localization." + target + ".json")
                ?? throw new InvalidDataException("Missing packaged UI language " + target);
            result.Add(target, JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException("Invalid UI language resource"));
        }
        return result;
    }
}
