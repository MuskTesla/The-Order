using System.Globalization;
using System.Text.RegularExpressions;
using TheOrder.Domain;

namespace TheOrder.Application;

// Display records only. A release is never a work identity or an installation binding.
public sealed record CatalogReleaseEngine(string Id, string Title);
public sealed record CatalogReleaseEdition(string Id, string WorkId, string? Title, string? OriginalTitle,
    string Kind, string? Date, string[] Languages, string[] Platforms, WorkReference[] References)
{
    // Release-specific fact only; the work's engines must not be copied here.
    public CatalogReleaseEngine? Engine { get; init; }
}
public sealed record CatalogReleasesPage(string WorkId, CatalogReleaseEdition[] Items, string? NextCursor,
    DateTimeOffset FetchedAtUtc);
public interface ICatalogReleaseReader
{
    Task<CatalogReleasesPage> LoadReleasesAsync(string workId, string? cursor, bool refresh, CancellationToken cancellation);
}

public static partial class CatalogReleaseFacts
{
    public static bool Id(string? value) => value is not null && IdPattern().IsMatch(value);
    public static bool Cursor(string? value) => value is null || value.Length <= 512 && CursorPattern().IsMatch(value);
    // Live responses include year/month precision although the OpenAPI date format says full date.
    public static bool Date(string? value) => value is null || value.Length == 4 &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999
        || value is { Length: 7 } && DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        || value is { Length: 10 } && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    public static bool Valid(CatalogReleaseEdition? row, string workId) => row is not null && Id(row.Id) && row.WorkId == workId
        && row.Title?.Length is not > 512 && row.OriginalTitle?.Length is not > 512
        && row.Kind.Length is > 0 and <= 64 && row.Kind.Trim() == row.Kind && Date(row.Date)
        && Texts(row.Languages, 32, 32) && Texts(row.Platforms, 32, 64)
        && (row.Engine is null || Id(row.Engine.Id) && row.Engine.Title is { Length: > 0 and <= 512 }
            && row.Engine.Title.Trim() == row.Engine.Title)
        && row.References is { Length: <= 128 } && row.References.All(r => r is not null
            && r.Source.Length is > 0 and <= 64 && r.Source.Trim() == r.Source
            && r.Id.Length is > 0 and <= 256 && r.Id.Trim() == r.Id);
    private static bool Texts(string[]? values, int count, int length) => values is not null && values.Length <= count
        && values.All(v => v is not null && v.Length is > 0 && v.Length <= length && v.Trim() == v)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    public static string KindCaption(string kind) => kind switch
    {
        "default" => "一般发行", "digital" => "数字发行", "physical" => "实体发行",
        "trial" => "体验版", "patch" => "补丁", _ => "其他发行"
    };
    public static string CompanyCaption(string? role) => role switch
    {
        "developer" => "制作", "publisher" => "发行", "circle" => "社团", "brand" => "品牌", _ => "相关组织"
    };
    public static int KindOrder(string kind) => kind switch { "default" => 0, "physical" or "digital" => 1, "trial" => 2, "patch" => 3, _ => 4 };
    public static string LanguageCaption(string language) => language switch
    {
        "ja" => "日语", "en" => "英语", "zh-Hans" => "简体中文", "zh-Hant" => "繁体中文", "zh" => "中文",
        "ko" => "韩语", "fr" => "法语", "de" => "德语", "es" => "西班牙语", "ru" => "俄语", _ => language
    };
    public static string PlatformCaption(string platform) => platform switch
    {
        "win" => "Windows", "lin" => "Linux", "mac" => "macOS", "and" => "Android", "ios" => "iOS",
        "psp" => "PSP", "ps2" => "PlayStation 2", "ps3" => "PlayStation 3", "ps4" => "PlayStation 4",
        "ps5" => "PlayStation 5", "psv" => "PS Vita", "swi" => "Nintendo Switch", "web" => "网页", _ => platform
    };
    public static string WorkDate(string value, string? precision)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
        return precision switch { "year" => value[..4], "month" => value[..7], _ => value };
    }
    [GeneratedRegex("^[0-9]{1,20}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^cur_[A-Za-z0-9._~-]+$")] private static partial Regex CursorPattern();
}
