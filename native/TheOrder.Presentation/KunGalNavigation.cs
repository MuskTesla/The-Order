using TheOrder.Application;
using TheOrder.Domain;
namespace TheOrder.Presentation;

public static class KunGalNavigation
{
    public static bool TryEntity(string value, out CatalogEntityRef entity)
    {
        entity = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Host is not ("kungal.com" or "www.kungal.com")) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 3) || parts[0] != "galgame") return false;
        var id = parts[^1];
        if (id.Length > 20 || id.Length == 0 || id.Any(c => c is < '0' or > '9')) return false;
        var kind = parts.Length == 2 ? CatalogEntityKind.Work : parts[1] switch { "character" => CatalogEntityKind.Character, "official" => CatalogEntityKind.Company, "staff" => CatalogEntityKind.CreditName, "tag" => CatalogEntityKind.Tag, "engine" => CatalogEntityKind.Engine, "series" => CatalogEntityKind.Series, "trait" => CatalogEntityKind.Trait, _ => (CatalogEntityKind?)null };
        if (kind is null) return false;
        entity = new(kind.Value, id, kind == CatalogEntityKind.Work ? UiText.Get("作品资料") : UiText.Get("目录资料")); return true;
    }
}
