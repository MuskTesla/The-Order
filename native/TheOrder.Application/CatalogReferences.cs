using TheOrder.Domain;

namespace TheOrder.Application;

// These relationships are display references, never recognition evidence or library merges.
public static class CatalogReferences
{
    public static CatalogEntityRef? Preferred(WorkMetadata work)
    {
        if (work.Presentation is not { } display) return null;
        var parents = display.RelatedWorks.Where(r => r.Kind == CatalogEntityKind.Work && r.Id != work.Id && r.Relation is "remake_of" or "remaster_of" or "port_of")
            .DistinctBy(r => r.Id).ToArray();
        if (parents.Length == 1) return parents[0];
        if (parents.Length > 1) return null;
        var alternatives = display.RelatedWorks.Where(r => r.Kind == CatalogEntityKind.Work && r.Id != work.Id && r.Relation == "alternative_version")
            .DistinctBy(r => r.Id).ToArray();
        return alternatives.Length == 1 ? alternatives[0] : null;
    }

    public static bool ExpandReference(WorkMetadata work) => work.Description.Length < 500 || work.Characters.IsEmpty;
}
