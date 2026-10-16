using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

// Display projections only. These identities describe the source of a field;
// neither a language preference nor a translation establishes entity equality.
public sealed record ContentIdentity(string Provider, string Resource, string Id);
public enum ContentTextOrigin { ProviderOriginal, ProviderTranslation, HumanTranslation, MachineTranslation, ProviderMachineTranslation }
public sealed record ContentText(ContentIdentity Identity, string Field, string Text, string Language,
    ContentTextOrigin Origin = ContentTextOrigin.ProviderOriginal, bool IsOriginal = false, string? LanguageHint = null, string? Attribution = null)
{
    public bool IsMachine => Origin is ContentTextOrigin.MachineTranslation or ContentTextOrigin.ProviderMachineTranslation;
}
public sealed record ContentLanguagePreference(string Preferred = "zh-Hans", string Fallback = "original");
public sealed record ContentLanguagePreferenceRead(ContentLanguagePreference Preference, string? Warning = null, bool ReadOnly = false);
public interface IContentLanguagePreferencesStore
{
    Task<ContentLanguagePreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(ContentLanguagePreference value, CancellationToken cancellation = default);
}
public sealed record ContentSelection(ContentText? Selected, ContentText? Original, ImmutableArray<ContentText> Variants)
{
    public string Text => Selected?.Text ?? "";
    public string OriginalText => Original?.Text ?? "";
    public bool HasAlternative => Variants.Any(x => x.Text != Text);
    public string Caption => Selected is null ? "" : ContentLanguage.SourceName(Selected) + " · " +
        (Selected.Language is "" or "und" ? "原文（语言未标注）" : ContentLanguage.LanguageName(Selected.Language)) +
        (Selected.Origin == ContentTextOrigin.ProviderMachineTranslation ? " · 资料源机器译文"
            : Selected.IsMachine ? " · 机器翻译" : Selected.Origin == ContentTextOrigin.HumanTranslation ? " · 人工翻译" : "");
}

public static class ContentLanguage
{
    public const string Original = "original";
    public static readonly string[] Choices = ["zh-Hans", "zh-Hant", "ja", "en", Original];
    private static ContentLanguagePreference? configured;
    private static readonly AsyncLocal<ContentLanguagePreference?> scoped = new();
    // Legacy callers/tests retain their UI-language projection until the app
    // explicitly initializes the independent content companion at startup.
    public static ContentLanguagePreference Current => scoped.Value ?? Volatile.Read(ref configured) ?? new(UiText.Language);
    public static string Preferred => Current.Preferred;
    public static bool Valid(ContentLanguagePreference? value) => value is not null && Choices.Contains(value.Preferred, StringComparer.Ordinal)
        && Choices.Contains(value.Fallback, StringComparer.Ordinal);
    public static void Configure(ContentLanguagePreference value)
    { if (!Valid(value)) throw new ArgumentException("Unsupported content language"); Volatile.Write(ref configured, value); }
    public static IDisposable ForPreference(ContentLanguagePreference value)
    { if (!Valid(value)) throw new ArgumentException("Unsupported content language"); var old = scoped.Value; scoped.Value = value; return new Scope(() => scoped.Value = old); }
    private sealed class Scope(Action exit) : IDisposable { public void Dispose() => exit(); }
    public static string Normalize(string? value)
    {
        var tag = value?.Trim().Replace('_', '-').ToLowerInvariant() ?? "und";
        if (tag is "" or "und") return "und";
        if (tag is "chi" or "zho" or "zh") return "zh"; // Language alone does not declare a script.
        if (tag.StartsWith("zh-hant", StringComparison.Ordinal) || tag.StartsWith("zh-tw", StringComparison.Ordinal)
            || tag.StartsWith("zh-hk", StringComparison.Ordinal) || tag.StartsWith("zh-mo", StringComparison.Ordinal)) return "zh-Hant";
        if (tag.StartsWith("zh-hans", StringComparison.Ordinal) || tag.StartsWith("zh-cn", StringComparison.Ordinal) || tag.StartsWith("zh-sg", StringComparison.Ordinal)) return "zh-Hans";
        return tag is "jpn" or "ja" || tag.StartsWith("ja-", StringComparison.Ordinal) ? "ja"
            : tag is "eng" or "en" || tag.StartsWith("en-", StringComparison.Ordinal) ? "en" : tag;
    }
    public static string LanguageName(string value) => Normalize(value) switch
    { "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", "zh" => "中文（字形未标注）", "ja" => "日本語", "en" => "English", Original => "原文", "und" => "语言未标注", var tag => tag };
    public static string Term(string value) => Preferred == Original ? value : CatalogVocabulary.Name(value, Preferred);
    public static string SourceName(ContentText value) => value.Identity.Provider + (value.Attribution is { Length: > 0 } source && source != "original" ? " / " + source : "");
    private static bool TextValid(ContentText value) => value.Identity is not null
        && new[] { value.Identity.Provider, value.Identity.Resource, value.Identity.Id, value.Field }.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 300 && !x.Any(char.IsControl))
        && !string.IsNullOrWhiteSpace(value.Text) && value.Text.Length <= 200000 && value.Language is { Length: <= 64 }
        && !value.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) && Enum.IsDefined(value.Origin)
        && (value.Attribution is null || value.Attribution.Length <= 200 && !value.Attribution.Any(char.IsControl))
        && (value.LanguageHint is null || value.LanguageHint.Length <= 64 && !value.LanguageHint.Any(char.IsControl));
    private static int Rank(ContentText value, ContentLanguagePreference preference)
    {
        var language = Normalize(value.Language);
        // A provider locale hint helps choose untagged prose, but is never
        // promoted to a declared language in provenance or persisted source data.
        if (language == "und") language = Normalize(value.LanguageHint);
        var preferred = preference.Preferred;
        if (preferred == Original) return value.IsOriginal ? 0 : 8;
        // Prefer existing non-machine Chinese in either script, then the
        // provider's supplied translation, before falling back to foreign prose.
        if (language == preferred) return value.IsMachine ? 2 : 0;
        if (preferred.StartsWith("zh-", StringComparison.Ordinal) && (language == "zh" || language.StartsWith("zh-", StringComparison.Ordinal))) return value.IsMachine ? 3 : 1;
        if (preference.Fallback != Original && language == preference.Fallback) return value.IsMachine ? 5 : 4;
        if (value.IsOriginal) return 6;
        return 8;
    }
    public static ContentSelection Select(IEnumerable<ContentText> variants, ContentLanguagePreference? preference = null)
    {
        var selectedPreference = preference ?? Current;
        if (!Valid(selectedPreference)) throw new ArgumentException("Unsupported content language");
        var all = variants.Where(x => x is not null && TextValid(x)).Distinct().Take(256).ToImmutableArray();
        // Translations already returned by the provider are display variants,
        // including provider-labelled machine translations. Optional local packs
        // remain disabled; source identities and unknown languages are not guessed.
        var selected = all.Where(x => !x.IsMachine || x.Origin == ContentTextOrigin.ProviderMachineTranslation
                && selectedPreference.Preferred != Original && Rank(x, selectedPreference) < 6)
            .OrderBy(x => Rank(x, selectedPreference))
            .ThenBy(x => Normalize(x.Language) == "und" ? 1 : 0).FirstOrDefault();
        var original = all.FirstOrDefault(x => x.IsOriginal && !x.IsMachine);
        return new(selected, original, all);
    }
    public static ContentSelection Raw(ContentIdentity identity, string field, string text, string language = "und")
        => Select([new(identity, field, text, language, IsOriginal: true)]);
    public static ContentSelection Catalog(ContentIdentity identity, string field, IEnumerable<CatalogLocalizedText>? values, string original, string? originalLanguage = null)
    {
        var sourceRows = (values ?? []).ToArray();
        var hasOriginalLanguage = originalLanguage is { Length: > 0 } && sourceRows.Any(x => !x.IsMachine && Normalize(x.Language) == Normalize(originalLanguage));
        ContentText Variant(CatalogLocalizedText x)
        {
            var isOriginal = !x.IsMachine && (x.Source == "original" || (hasOriginalLanguage ? Normalize(x.Language) == Normalize(originalLanguage) : x.Value == original));
            return new(identity, field, x.Value, Normalize(x.Language), x.IsMachine ? ContentTextOrigin.ProviderMachineTranslation
                : isOriginal ? ContentTextOrigin.ProviderOriginal : ContentTextOrigin.ProviderTranslation, isOriginal, Attribution: x.Source);
        }
        var rows = sourceRows.Select(Variant).ToList();
        if (!rows.Any(x => x.Text == original)) rows.Add(new(identity, field, original, "und", IsOriginal: !rows.Any(x => x.IsOriginal)));
        return Select(rows);
    }
    public static ContentSelection CatalogSynopsis(WorkMetadata work) => Catalog(new("NextMoe", "Work", work.Id), "synopsis",
        work.Presentation?.Introductions, work.Description, work.Presentation?.Names?.FirstOrDefault(x => x.Source == "original")?.Language);
    public static ContentSelection Aliases(ContentIdentity identity, IEnumerable<string> values)
        => Select(values.Select(x => new ContentText(identity, "aliases", x, "und", IsOriginal: true)));
    private static string ProseHint(string text, string providerLocale)
        => text.Any(c => c is >= '\u3040' and <= '\u30ff') ? "ja"
        : text.Any(c => c is >= '\u3400' and <= '\u9fff') && providerLocale == "en" ? "und" : providerLocale;
    public static IEnumerable<ContentText> BangumiTitles(BangumiSubjectDetails value)
    {
        var id = new ContentIdentity("Bangumi", "Subject:" + value.Kind, value.Id);
        yield return new(id, "title", value.OriginalTitle.Length > 0 ? value.OriginalTitle : value.Title, "und", IsOriginal: true);
        // ChineseTitle is the actual name_cn field. Legacy cache fallback is
        // only a locale hint, never a newly asserted translation.
        if (value.ChineseTitle is { Length: > 0 } chinese)
            yield return new(id, "title", chinese, "zh", ContentTextOrigin.ProviderTranslation);
        else if (value.Title != value.OriginalTitle)
            yield return new(id, "title", value.Title, "und", ContentTextOrigin.ProviderTranslation, LanguageHint: "zh-Hans");
    }
    public static ContentSelection BangumiTitle(BangumiSubjectDetails value) => Select(BangumiTitles(value));
    public static ContentSelection AniListTitle(AniListMedia media, BangumiSubjectDetails? bridge = null, bool trusted = false)
    {
        var id = new ContentIdentity("AniList", media.Kind.ToString(), media.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var rows = new List<ContentText> { new(id, "title", media.NativeTitle.Length > 0 ? media.NativeTitle : media.Title, "und", IsOriginal: true,
                LanguageHint: media.NativeTitle.Any(c => c is >= '\u3040' and <= '\u9fff') ? "ja" : null),
            new(id, "title", media.EnglishTitle, "en", ContentTextOrigin.ProviderTranslation),
            new(id, "title", media.RomajiTitle ?? "", "und", ContentTextOrigin.ProviderTranslation, LanguageHint: "ja") };
        if (trusted && bridge is not null && (media.Kind == AniListMediaKind.Anime ? bridge.Kind == BangumiRelationKind.Anime : bridge.Kind == BangumiRelationKind.Book))
            rows.AddRange(BangumiTitles(bridge));
        return Select(rows);
    }
    public static ContentSelection Synopsis(AniListDetails? details, BangumiSubjectDetails? bridge, bool trusted)
    {
        var rows = new List<ContentText>();
        if (details is not null) rows.Add(new(new("AniList", details.Media.Kind.ToString(), details.Media.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "synopsis", details.Description, "und", IsOriginal: true, LanguageHint: ProseHint(details.Description, "en")));
        if (bridge is not null && (details is null || trusted && (details.Media.Kind == AniListMediaKind.Anime ? bridge.Kind == BangumiRelationKind.Anime : bridge.Kind == BangumiRelationKind.Book))) rows.Add(new(new("Bangumi", "Subject:" + bridge.Kind, bridge.Id),
            "synopsis", bridge.Summary, "und", IsOriginal: details is null, LanguageHint: ProseHint(bridge.Summary, "zh-Hans")));
        return Select(rows);
    }
    public static ContentSelection Person(AniListPerson person, string resource = "Person")
    {
        var id = new ContentIdentity("AniList", resource, person.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Select([new(id, "name", person.NativeName.Length > 0 ? person.NativeName : person.Name, "und", IsOriginal: true, LanguageHint: "ja"),
            new(id, "name", person.Name, "und", ContentTextOrigin.ProviderTranslation, LanguageHint: "en")]);
    }
    public static ContentSelection MusicTitle(MusicBrainzAlbum album, BangumiSubjectDetails? bridge, bool trusted)
    {
        var rows = new List<ContentText> { new(new("MusicBrainz", "Release", album.Release.Id), "title", album.Release.Title,
            "und", IsOriginal: true, LanguageHint: Normalize(album.Release.TextLanguage)) };
        if (trusted && bridge?.Kind == BangumiRelationKind.Music) rows.AddRange(BangumiTitles(bridge));
        return Select(rows);
    }
    public static ContentSelection PublicationTitle(BookPublication publication)
        => Select([new(new("NDL", publication.Identity.Level.ToString(), publication.Id), "title", publication.Title,
            "und", IsOriginal: true, LanguageHint: publication.Languages.Length == 1 ? Normalize(publication.Languages[0]) : null)]);
}

// A small future seam, not an inference runtime or an installed package.
// Implementations must be explicitly enabled, local, cancellable and return a
// separate variant. Names/IDs/ISBN/dates are not eligible translation inputs.
public sealed record LocalTranslationRequest(ContentIdentity Identity, string Field, string SourceText, string SourceLanguage, string TargetLanguage)
{
    public bool IsEligible => Identity is not null && Field == "synopsis" && !string.IsNullOrWhiteSpace(SourceText) && SourceText.Length <= 65536
        && !SourceText.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
        && ContentLanguage.Choices.Contains(TargetLanguage, StringComparer.Ordinal) && TargetLanguage != ContentLanguage.Original;
}
public interface ILocalContentTranslationPack
{
    string PackId { get; }
    bool Supports(string sourceLanguage, string targetLanguage);
    Task<ContentText?> TranslateSynopsisAsync(LocalTranslationRequest request, CancellationToken cancellation);
}
