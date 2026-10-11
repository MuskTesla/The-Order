using System.Collections.Immutable;
using TheOrder.Domain;
namespace TheOrder.Application;

// A read-only projection. Never feed it into recognition or a library transaction.
public static class CatalogLanguage
{
    public const int DisplaySchema = 3;
    public static string Normalize(string language)
    {
        var tag = language.Replace('_', '-').ToLowerInvariant();
        if (tag.StartsWith("zh")) return tag.Contains("hant") || tag.StartsWith("zh-tw") || tag.StartsWith("zh-hk") || tag.StartsWith("zh-mo") ? "zh-Hant" : "zh-Hans";
        return tag.StartsWith("ja") ? "ja" : tag.StartsWith("en") ? "en" : tag;
    }
    public static int LanguageRank(string language, string target)
    {
        var normalized = Normalize(language);
        if (normalized == target) return 0;
        var fallbacks = target switch { "zh-Hans" => new[] { "zh-Hant", "ja", "en" }, "zh-Hant" => new[] { "zh-Hans", "ja", "en" }, "ja" => new[] { "en", "zh-Hans", "zh-Hant" }, _ => new[] { "ja", "zh-Hans", "zh-Hant" } };
        var index = Array.IndexOf(fallbacks, normalized); return index < 0 ? 10 : index + 1;
    }
    public static CatalogLocalizedText? Select(IEnumerable<CatalogLocalizedText>? values, string? language = null)
        => values?.Where(v => !string.IsNullOrWhiteSpace(v.Value))
        .OrderBy(v => LanguageRank(v.Language, language ?? UiText.Language))
        .ThenBy(v => v.IsMachine ? 1 : 0).ThenBy(v => v.Source is "kungal" or "curated" ? 0 : 1).FirstOrDefault();
    public static string Name(CatalogEntityRef entity)
    {
        var names = entity.Names ?? [];
        if (entity.Kind == CatalogEntityKind.Character && IsUnnamed(Original(entity) ?? entity.Name)) return UiText.Get("未公开姓名");
        var exact = Select(names.Where(n => Normalize(n.Language) == UiText.Language));
        if (entity.Kind is CatalogEntityKind.Tag or CatalogEntityKind.Trait)
            return exact?.Value ?? CatalogVocabulary.Name(Original(entity) ?? entity.Name);
        if (entity.Kind is CatalogEntityKind.Character or CatalogEntityKind.CreditName or CatalogEntityKind.Person)
        {
            var original = Original(entity);
            // Some upstream rows label a Latin transcription "ja". A Japanese
            // UI should retain the published original script instead. English
            // uses a published en/latin name, otherwise the original consistently
            // for both characters and voices (not a Chinese fallback or a guessed reading).
            if (UiText.Language == "ja" && original is not null && HasJapaneseScript(original) &&
                (exact is null || !HasJapaneseScript(exact.Value))) return original;
            if (exact is not null) return exact.Value;
            if (UiText.Language is "ja" or "en" && original is not null) return original;
        }
        return exact?.Value ?? Select(names)?.Value ?? entity.Name;
    }
    private static string? Original(CatalogEntityRef entity) => entity.Names?.LastOrDefault(n => n.Source == "original")?.Value
        ?? entity.Names?.LastOrDefault(n => n.Language.Length == 0)?.Value;
    private static bool HasJapaneseScript(string value) => value.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
    private static bool IsUnnamed(string value) => value.Trim().Normalize(System.Text.NormalizationForm.FormKC) is {Length:>0} name && name.All(c => c == '?');
    public static CatalogEntityRef Project(CatalogEntityRef entity) => entity with { Name = Name(entity), Subtitle = Relation(entity) };
    public static string? Relation(CatalogEntityRef entity) => entity.Subtitle is { Length: > 0 } subtitle
        ? UiText.Get(subtitle) : entity.Relation switch
    {
        "adaptation_of" => UiText.Get("改编自"), "adapted_as" or "has_adaptation" => UiText.Get("被改编为"),
        "alternative_version" => UiText.Get("不同演绎"), "shares_character" => UiText.Get("角色出演"),
        "side_story_of" => UiText.Get("有外传"), "parent_story" or "parent_story_of" => UiText.Get("外传作品"),
        "sequel" => UiText.Get("续作"), "prequel" => UiText.Get("前作"),
        "remake_of" => UiText.Get("重制自"), "remade_as" or "has_remake" => UiText.Get("重制版"),
        "fandisc_of" => UiText.Get("关联本篇"), "has_fandisc" => UiText.Get("Fan disc"),
        _ => null
    };
    public static string Title(WorkMetadata work) => Select(work.Presentation?.Names)?.Value ?? work.Title;
    public static WorkMetadata Project(WorkMetadata work)
    {
        var p = work.Presentation; if (p is null) return work;
        var intro = Select(p.Introductions);
        ImmutableArray<CatalogEntityRef> Names(ImmutableArray<CatalogEntityRef> rows) => [..rows.Select(Project)];
        var companies = Names(p.Companies);
        return work with { Title = Title(work), Description = intro?.Value ?? work.Description,
            Developers = [..work.Developers.Select(name => p.Companies.FirstOrDefault(c => c.Name == name) is { } c ? Name(c) : name)],
            Tags = p.Tags.IsEmpty ? work.Tags : [..p.Tags.Select(Name)],
            Presentation = p with { IntroSource = intro?.Source ?? p.IntroSource, Companies = companies, Engines = Names(p.Engines),
                Series = Names(p.Series), Tags = Names(p.Tags), RelatedWorks = Names(CatalogRelatedWorks.Visible(p)),
                Characters = [..p.Characters.Select(c => c with { Entity = Project(c.Entity), Voices = Names(c.Voices) })],
                Credits = [..p.Credits.Select(c => c with { People = Names(c.People) })] } };
    }
    public static string? Fallback(WorkMetadata work)
    {
        var intro = Select(work.Presentation?.Introductions);
        return intro is null || Normalize(intro.Language) == UiText.Language ? null : UiText.FormatKey("暂无所选语言的简介，显示原文（{0}）。", LanguageName(intro.Language));
    }
    public static string LanguageName(string language) => Normalize(language) switch { "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", "ja" => "日本語", "en" => "English", _ => language };
}
