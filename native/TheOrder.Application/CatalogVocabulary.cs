using System.Text;
using System.Text.Json;

namespace TheOrder.Application;

// Display-only terminology for catalog tags/traits whose v2 row has one name.
// Exact whole-label matches only: never rewrite user tags, ids, evidence or prose.
// Unknown vocabulary remains visible in its original form, not silently dropped.
public static class CatalogVocabulary
{
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> words = new(Load);
    private static string Key(string value) => value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
    public static string Name(string original) => Name(original, UiText.Language);
    public static string Name(string original, string language)
    {
        if (!words.Value.TryGetValue(Key(original), out var row)) return original;
        return row[language switch { "zh-Hans" => 0, "zh-Hant" => 1, "ja" => 2, _ => 3 }];
    }
    public static bool HasTranslation(string original) => words.Value.ContainsKey(Key(original));
    private static IReadOnlyDictionary<string, string[]> Load()
    {
        using var stream = typeof(CatalogVocabulary).Assembly.GetManifestResourceStream("TheOrder.Application.Localization.catalog-vocabulary.json")
            ?? throw new InvalidDataException("Missing catalog vocabulary");
        var rows = JsonSerializer.Deserialize<string[][]>(stream) ?? throw new InvalidDataException("Invalid catalog vocabulary");
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Length != 4 || row.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid catalog vocabulary row");
            // Each locale is a reversible display alias of the same term.
            // A collision may not silently acquire another term's translation.
            foreach (var name in row)
            {
                var key = Key(name);
                if (result.TryGetValue(key, out var old) && !old.SequenceEqual(row)) continue;
                result.TryAdd(key, row);
            }
        }
        return result;
    }
}
