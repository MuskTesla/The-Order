using System.Text;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

internal static class MusicBrainzSearchQueries
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "original", "soundtrack", "soundtracks", "ost", "vocal", "harmony", "hamony", "version", "versions", "album", "music", "the", "and", "for", "instrumental" };
    internal static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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
