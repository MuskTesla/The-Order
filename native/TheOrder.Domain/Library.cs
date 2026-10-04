using System.Collections.Immutable;

namespace TheOrder.Domain;

// Domain objects deliberately have no JSON attributes or file/network dependencies.
public sealed record Installation(string Id, string Folder, string? Executable, ImmutableArray<string> Arguments, string? WorkingDirectory = null);
public sealed record PlaySession(string Id, string StartedAt, string EndedAt, long Seconds);
public sealed record LocalCover(string DataUrl, int Width, int Height);
public sealed record CoverPreference(bool IsSpecified, string? Mode);
public sealed record WorkCover(string Id, string Url, int? Width, int? Height, long? Votes, string? Sexual);
public sealed record CoverChoice(string WorkId, WorkCover Cover);
public sealed record TitleEvidence(string Text, string Kind, bool IsMachine, string? Language);
public sealed record SourceRating(string Source, double Score, double Scale, double? Votes);
public sealed record WorkCharacter(string Id, string Name, string OriginalName, string? ImageUrl, string Role, ImmutableArray<string> VoiceActors);
public sealed record WorkReference(string Source, string Id, string? Url);
public sealed record WorkMetadata(
    string Id, string Title, string OriginalTitle, ImmutableArray<string> Aliases,
    ImmutableArray<TitleEvidence>? TitleEvidence, string? CoverUrl, ImmutableArray<WorkCover>? Covers,
    string? BannerUrl, string Description, string? ReleaseDate, string? ReleaseDatePrecision,
    ImmutableArray<string> Developers, ImmutableArray<string> Tags, ImmutableArray<SourceRating> Ratings,
    ImmutableArray<WorkCharacter> Characters, ImmutableArray<WorkReference> References,
    string? WebUrl, string? ContentRating, string FetchedAt);

public sealed record LibraryEntry(
    string Id, string Name, bool? NameIsCustom, WorkMetadata? Metadata,
    ImmutableArray<Installation> Installations, string? PreferredInstallationId,
    string Status, double? PersonalRating, bool Favorite, ImmutableArray<string> Categories,
    ImmutableArray<string> CustomTags, string Notes, long TotalPlaySeconds, long PlayCount,
    ImmutableArray<PlaySession> Sessions, string AddedAt, string? LastPlayedAt,
    LocalCover? LocalCover, CoverChoice? CoverChoice, CoverPreference CoverDisplay);

// Lists bind only summaries. Metadata, embedded images and history stay out of each visual item.
public sealed record LibrarySummary(string Id, string Name, string Progress, int Installations, long TotalPlaySeconds)
{
    public override string ToString() => Name;
    public string Subtitle => $"{Progress} · {Installations} 个安装 · {TotalPlaySeconds / 60} 分钟";
    public static LibrarySummary From(LibraryEntry entry) => new(entry.Id, entry.Name,
        entry.Status switch { "playing" => "在玩", "completed" => "已完成", "paused" => "暂停", "dropped" => "搁置", _ => "计划游玩" },
        entry.Installations.Length, entry.TotalPlaySeconds);
}
