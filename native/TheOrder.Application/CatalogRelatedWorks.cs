using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

// Related cards are display facts, never local/remote recognition evidence.
public static class CatalogRelatedWorks
{
    // Schema 2 preserves every original relation on each exact work identity.
    // This is a display-cache refresh marker, not a library data migration.
    public const int Schema = 2;
    public static bool Eligible(CatalogEntityRef work) => work.Kind == CatalogEntityKind.Work &&
        work.KunGalGalgameId is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit);
    public static ImmutableArray<CatalogEntityRef> Visible(CatalogPresentation? display) =>
        display is null ? [] : [..display.RelatedWorks.Where(Eligible).DistinctBy(w => w.Id).Take(64)];
    public static bool NeedsRefresh(CatalogPresentation? display) =>
        display is { RelatedWorks.IsEmpty: false } && display.RelatedSchema != Schema;
}
