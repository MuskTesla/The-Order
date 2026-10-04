namespace TheOrder.Infrastructure.Legacy;

// Only the validated reader creates these transport records. Optional booleans retain unknown.
public sealed record LegacyLibraryDto(string Format, int Version, LegacyGameDto[] Games);
public sealed record LegacyGameDto(
    string Id, string Name, bool? NameIsCustom, LegacyMetadataDto? Metadata,
    LegacyInstallationDto[] Installations, string? PreferredInstallationId, string Status,
    double? PersonalRating, bool Favorite, string[] Categories, string[] CustomTags, string Notes,
    double TotalPlaySeconds, double PlayCount, LegacySessionDto[] Sessions, string AddedAt, string? LastPlayedAt,
    LegacyLocalCoverDto? LocalCover, LegacyCoverChoiceDto? CoverChoice, string? NonPortraitCoverDisplay);
public sealed record LegacyInstallationDto(string Id, string FolderPath, string? ExecutablePath, string[] Arguments);
public sealed record LegacySessionDto(string Id, string StartedAt, string EndedAt, double Seconds);
public sealed record LegacyLocalCoverDto(string DataUrl, int Width, int Height);
public sealed record LegacyCoverDto(string Id, string Url, int? Width, int? Height, long? Votes, string? Sexual);
public sealed record LegacyCoverChoiceDto(string WorkId, LegacyCoverDto Cover);
public sealed record LegacyTitleDto(string Text, string Kind, bool IsMachine, string? Language);
public sealed record LegacyRatingDto(string Source, double Score, double Scale, double? Votes);
public sealed record LegacyCharacterDto(string Id, string Name, string OriginalName, string? ImageUrl, string Role, string[] VoiceActors);
public sealed record LegacyReferenceDto(string Source, string Id, string? Url);
public sealed record LegacyMetadataDto(
    string Id, string Title, string OriginalTitle, string[] Aliases, LegacyTitleDto[]? TitleEvidence,
    string? CoverUrl, LegacyCoverDto[]? Covers, string? BannerUrl, string Description, string? ReleaseDate,
    string? ReleaseDatePrecision, string[] Developers, string[] Tags, LegacyRatingDto[] Ratings,
    LegacyCharacterDto[] Characters, LegacyReferenceDto[] References, string? WebUrl, string? ContentRating, string FetchedAt);
