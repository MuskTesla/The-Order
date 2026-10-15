namespace TheOrder.Application;

// Read-only specialist navigation. These facts never become album matching or library identity evidence.
public sealed record MusicBrainzEntityFact(string Label, string Value);
public sealed record MusicBrainzEntityRelation(string RelationType, string Direction, string[] Attributes,
    MusicBrainzReference Target, string Title, string? Begin, string? End);
public sealed record MusicBrainzEntityDetail(MusicBrainzReference Reference, string Title, string? EntityType,
    string? Disambiguation, string[] Aliases, MusicBrainzEntityFact[] Facts, MusicBrainzEntityRelation[] Relations,
    bool RelationsTruncated, DateTimeOffset FetchedAtUtc)
{
    public string Url => Reference.Url;
}
public sealed record MusicBrainzEntityResult(MusicBrainzEntityDetail Detail, bool FromCache, bool IsStale, string? Warning);
public interface IMusicBrainzEntityReader
{
    Task<MusicBrainzEntityResult> LoadAsync(MusicBrainzReference reference, bool refresh, CancellationToken cancellation);
}
public static class MusicBrainzEntityValidation
{
    public const int MaximumRelations = 256;
    public static bool Reference(MusicBrainzReference? reference) => reference is not null
        && reference.Kind is MusicBrainzEntityKind.Work or MusicBrainzEntityKind.Artist or MusicBrainzEntityKind.Recording
        && MusicBrainzReference.ValidId(reference.Id);
    public static bool Detail(MusicBrainzEntityDetail? detail) => detail is not null && Reference(detail.Reference)
        && MusicBrainzValidation.Text(detail.Title, 2000, false)
        && (detail.EntityType is null || MusicBrainzValidation.Text(detail.EntityType, 100))
        && (detail.Disambiguation is null || MusicBrainzValidation.Text(detail.Disambiguation, 1000))
        && MusicBrainzValidation.Texts(detail.Aliases, 128, 2000)
        && detail.Facts is { Length: <= 32 } && detail.Facts.All(f => f is not null
            && MusicBrainzValidation.Text(f.Label, 100, false) && MusicBrainzValidation.Text(f.Value, 4000, false))
        && detail.Relations is { Length: <= MaximumRelations } && detail.Relations.All(r => r is not null
            && MusicBrainzValidation.Text(r.RelationType, 200, false) && r.Direction is "forward" or "backward"
            && MusicBrainzValidation.Texts(r.Attributes, 32, 200) && Reference(r.Target)
            && MusicBrainzValidation.Text(r.Title, 2000, false)
            && (r.Begin is null || MusicBrainzValidation.Date(r.Begin)) && (r.End is null || MusicBrainzValidation.Date(r.End)))
        && detail.FetchedAtUtc != default;
}
