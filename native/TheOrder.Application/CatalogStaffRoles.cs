using System.Text;
using System.Text.Json;

namespace TheOrder.Application;

// Catalog professions have their own vocabulary: "编辑" means Editing here,
// not the UI's Edit action. Raw role keys/names and people remain unchanged.
public static class CatalogStaffRoles
{
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> rows = new(Load);
    private static string Key(string value) => value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
    public static string Name(string original, string? roleKey = null) => Translate(original, UiText.Language, roleKey);
    public static string Translate(string original, string language, string? roleKey = null)
    {
        if (!(roleKey is not null && rows.Value.TryGetValue(Key(roleKey), out var row)) && !rows.Value.TryGetValue(Key(original), out row)) return original;
        return row[language switch { "zh-Hans" => 1, "zh-Hant" => 2, "ja" => 3, _ => 4 }];
    }
    public static bool IsVoiceActing(string original, string? roleKey = null) => Translate(original, "en", roleKey) == "Voice acting";
    public static bool HasTranslation(string original) => rows.Value.ContainsKey(Key(original));
    private static IReadOnlyDictionary<string, string[]> Load()
    {
        using var stream = typeof(CatalogStaffRoles).Assembly.GetManifestResourceStream("TheOrder.Application.Localization.catalog-staff-roles.json")
            ?? throw new InvalidDataException("Missing catalog professions");
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in JsonSerializer.Deserialize<string[][]>(stream) ?? throw new InvalidDataException("Invalid catalog professions"))
        {
            if (row.Length != 5 || row.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid catalog profession row");
            foreach (var name in row) result.TryAdd(Key(name), row);
        }
        return result;
    }
}
