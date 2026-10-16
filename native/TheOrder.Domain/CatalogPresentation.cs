using System.Collections.Immutable;
namespace TheOrder.Domain;
// Read-only catalog display facts. Never encoded into the library or recognition evidence.
public enum CatalogEntityKind { Work, Character, Company, CreditName, Person, Tag, Engine, Series, Trait }
public sealed record CatalogLocalizedText(string Language, string Value, string Source = "", bool IsMachine = false);
public sealed record CatalogRelationEvidence(string Type, string Phrase);
public sealed record CatalogEntityRef(CatalogEntityKind Kind,string Id,string Name,string? Subtitle=null,string? ImageUrl=null,string? Relation=null)
{
    // Display proof from an explicit galgame + KunGal claim. A catalog work ID
    // alone also describes anime and other media, and is not a KunGal page ID.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? KunGalGalgameId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CatalogLocalizedText[]? Names { get; init; }
    // Multiple source edges can describe one exact work. Keep their original
    // tokens and direction phrases even when the UI shows only one identity card.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CatalogRelationEvidence[]? RelationEvidence { get; init; }
}
public sealed record CatalogArt(string Url,int Width,int Height,string Kind,string? Caption=null);
public sealed record CatalogCharacter(CatalogEntityRef Entity,string OriginalName,string Role,string Spoiler,ImmutableArray<CatalogEntityRef> Voices);
public sealed record CatalogCredit(string Role,ImmutableArray<CatalogEntityRef> People)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleKey { get; init; }
}
public sealed record CatalogPresentation(string IntroSource,ImmutableArray<CatalogArt> Art,
    ImmutableArray<CatalogEntityRef> Companies,ImmutableArray<CatalogEntityRef> Engines,ImmutableArray<CatalogEntityRef> Series,
    ImmutableArray<CatalogEntityRef> Tags,ImmutableArray<CatalogCharacter> Characters,ImmutableArray<CatalogCredit> Credits,ImmutableArray<CatalogEntityRef> RelatedWorks)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? RelatedSchema { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LanguageSchema { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CatalogLocalizedText[]? Names { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CatalogLocalizedText[]? Introductions { get; init; }
}
public sealed record CatalogEntityDetail(CatalogEntityRef Entity,string Description,ImmutableArray<string> Facts,
    ImmutableArray<CatalogEntityRef> Related,ImmutableArray<WorkReference> References)
{
    public CatalogLocalizedText[]? Introductions { get; init; }
}
public sealed record CatalogEntityWorks(ImmutableArray<WorkMetadata> Works,string? NextCursor);
public sealed record CatalogEntityPage(ImmutableArray<CatalogEntityRef> Items,string? NextCursor);
