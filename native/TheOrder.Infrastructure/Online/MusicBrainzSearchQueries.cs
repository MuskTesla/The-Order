using System.Text;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

internal static class MusicBrainzSearchQueries
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "original", "soundtrack", "soundtracks", "ost", "vocal", "harmony", "hamony", "version", "versions", "album", "music", "the", "and", "for", "instrumental" };
    internal static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    internal const int MaximumTitleQueries = 6;
    internal static string[] Retrieval(MusicAlbumSource source)
    {
        var aliases = source.Facts.TitleAliases.Where(x => MusicBrainzMatching.Normalize(x) != MusicBrainzMatching.Normalize(source.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        string Title(string text) => "release:" + Quote(text.Normalize(NormalizationForm.FormKC)[..Math.Min(text.Normalize(NormalizationForm.FormKC).Length, 300)]);
        string Or(IEnumerable<string> values) { var rows = values.ToArray(); return rows.Length == 1 ? rows[0] : "(" + string.Join(" OR ", rows) + ")"; }
        var title = Or(aliases.Append(source.Title).Select(Title));
        var queries = new List<string>();
        // Never let the first page of a common short title suppress context queries.
        if (source.ReleaseDate is not null) queries.Add(title + " AND date:" + Quote(source.ReleaseDate));
        if (aliases.Length > 0) queries.Add(Or(aliases.Select(Title)));
        var artists = ArtistTerms(source.Facts.Artists).Select(x => "artist:" + Quote(x)).ToArray();
        if (artists.Length > 0) queries.Add(title + " AND " + Or(artists));
        queries.Add(Title(source.Title));
        queries.AddRange(Fallback(source));
        return queries.Distinct().Take(MaximumTitleQueries).ToArray();
    }
    internal static string[] ArtistTerms(IEnumerable<string> artists) => artists.Take(2).SelectMany(value =>
    {
        var full = value.Normalize(NormalizationForm.FormKC);
        // CV stage names can differ between sites. The character name remains a
        // useful query, but this stripping never establishes artist equivalence.
        var character = Regex.Replace(full, @"\s*\(\s*CV\s*:.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return new[] { character, full }.Where(x => x.Length > 0).Select(x => x[..Math.Min(x.Length, 200)]);
    }).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
    internal static (bool Catalogue, bool Barcode, bool Date, bool Artist, bool Title, bool Consistent) EvidenceOrder(MusicAlbumSource source, MusicBrainzReleaseCandidate candidate)
    {
        var catalogues = MusicBrainzMatching.Catalogues(source.Facts.CatalogNumbers).Select(MusicBrainzMatching.Normalize).ToArray();
        var found = candidate.Labels.Where(x => x.CatalogNumber is not null).Select(x => MusicBrainzMatching.Normalize(x.CatalogNumber!)).ToArray();
        var artist = MusicBrainzMatching.Normalize(candidate.Artist);
        return (catalogues.Length > 0 && catalogues.All(found.Contains), source.Facts.Barcode is { } code && code == candidate.Barcode,
            source.ReleaseDate is { } date && date == candidate.Date,
            artist.Length > 0 && ArtistTerms(source.Facts.Artists).Any(x => artist.Contains(MusicBrainzMatching.Normalize(x), StringComparison.Ordinal)),
            source.Facts.TitleAliases.Prepend(source.Title).Any(x => MusicBrainzMatching.Normalize(x) == MusicBrainzMatching.Normalize(candidate.Title)),
            !MusicBrainzMatching.Assess(source, candidate).HasConflict);
    }
    internal static string[] Fallback(MusicAlbumSource source)
    {
        // These are search variants, never identity aliases. Prefer distinctive CJK fragments;
        // prefixes, punctuation and romanization mistakes must not make the database invisible.
        var terms = Regex.Matches(source.Title.Normalize(NormalizationForm.FormKC), @"[\p{L}\p{N}][\p{L}\p{N}'-]{2,159}")
            .Select(x => x.Value).Where(x => !Generic.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Any(c => c is >= '\u3040' and <= '\u9fff')).ThenByDescending(x => x.Length).Take(2).ToArray();
        var queries = new List<string>();
        string? metadata = null;
        if (source.ReleaseDate is not null)
        {
            if (source.Facts.Artists.FirstOrDefault() is { } performer)
                metadata = "artist:" + Quote(performer[..Math.Min(performer.Length, 200)]) + " AND date:" + Quote(source.ReleaseDate);
            else if (source.Facts.Labels.FirstOrDefault() is { } label)
                metadata = "label:" + Quote(label[..Math.Min(label.Length, 200)]) + " AND date:" + Quote(source.ReleaseDate);
        }
        foreach (var term in terms)
        {
            var title = "release:" + Quote(term);
            if (source.ReleaseDate is not null) queries.Add(title + " AND date:" + Quote(source.ReleaseDate));
            queries.Add(title);
            if (metadata is not null && queries.Count == (source.ReleaseDate is null ? 1 : 2)) queries.Add(metadata);
        }
        if (metadata is not null) queries.Add(metadata);
        return queries.Distinct().Take(4).ToArray();
    }
}
