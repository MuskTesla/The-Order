using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Legacy;

public static class LegacyMapping
{
    public static LibraryEntry Map(LegacyGameDto dto, bool coverDisplaySpecified) => new(
        dto.Id, dto.Name, dto.NameIsCustom, dto.Metadata is null ? null : MapMetadata(dto.Metadata),
        dto.Installations.Select(i => new Installation(i.Id, i.FolderPath, i.ExecutablePath, [.. i.Arguments])).ToImmutableArray(),
        dto.PreferredInstallationId, dto.Status, dto.PersonalRating, dto.Favorite, [.. dto.Categories], [.. dto.CustomTags], dto.Notes,
        (long)Math.Floor(dto.TotalPlaySeconds), (long)Math.Floor(dto.PlayCount),
        dto.Sessions.Select(s => new PlaySession(s.Id, s.StartedAt, s.EndedAt, (long)Math.Floor(s.Seconds))).ToImmutableArray(),
        dto.AddedAt, dto.LastPlayedAt,
        dto.LocalCover is { } local ? new(local.DataUrl, local.Width, local.Height) : null,
        dto.CoverChoice is { } choice ? new(choice.WorkId, MapCover(choice.Cover)) : null,
        new(coverDisplaySpecified, dto.NonPortraitCoverDisplay));

    private static WorkCover MapCover(LegacyCoverDto c) => new(c.Id, c.Url, c.Width, c.Height, c.Votes, c.Sexual);
    private static WorkMetadata MapMetadata(LegacyMetadataDto m) => new(
        m.Id, m.Title, m.OriginalTitle, [.. m.Aliases],
        m.TitleEvidence?.Select(t => new TitleEvidence(t.Text, t.Kind, t.IsMachine, t.Language)).ToImmutableArray(),
        m.CoverUrl, m.Covers?.Select(MapCover).ToImmutableArray(), m.BannerUrl, m.Description, m.ReleaseDate, m.ReleaseDatePrecision,
        [.. m.Developers], [.. m.Tags], m.Ratings.Select(r => new SourceRating(r.Source, r.Score, r.Scale, r.Votes)).ToImmutableArray(),
        m.Characters.Select(c => new WorkCharacter(c.Id, c.Name, c.OriginalName, c.ImageUrl, c.Role, [.. c.VoiceActors])).ToImmutableArray(),
        m.References.Select(r => new WorkReference(r.Source, r.Id, r.Url)).ToImmutableArray(), m.WebUrl, m.ContentRating, m.FetchedAt);
}
