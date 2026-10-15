using System.Text;
using System.Text.Json;
using TheOrder.Infrastructure.Recognition;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

// Display-only consolidation of duplicate catalog roster entries. No canonical
// ids, local identity facts, recognition evidence or library data are inferred.
internal static class CatalogRosterDto
{
    internal static string? VoiceIdentity(JsonElement voice) => ValidId(Text(voice, "person_id")) ? "person:" + Text(voice, "person_id") : ValidId(Text(voice, "id")) ? "credit:" + Text(voice, "id") : null;
    private static string Clean(string name) => string.Concat(name.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    private static HashSet<string> Spellings(JsonElement row)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in CatalogDisplayDto.Names(row).Where(n => !n.IsMachine).Select(n => n.Value).Append(Text(row, "display_name")).Where(n => n.Length is > 0 and <= 500))
        {
            // Orthographic variants only, not fuzzy names, invented readings or aliases.
            foreach (var spelling in LocalQueryVariants.Chinese(name).Prepend(name))
                if (Clean(spelling) is {Length:>0} key && !key.All(c => c == '?')) result.Add(key);
        }
        return result;
    }
    private sealed record Row(JsonElement Value, HashSet<string> Names, JsonElement[] Voices)
    {
        public bool Supplement => Image(Field(Value, "image")) is null && Text(Value, "roster_role") is "" or "unknown" && Text(Value, "identity").Length == 0;
        public bool IllustratedRoster => Image(Field(Value, "image")) is not null && Text(Value, "roster_role") is "main" or "secondary" or "appears";
    }
    private static bool VoiceNamesMatch(JsonElement a, JsonElement b)
    {
        var personA = Text(a, "person_id"); var personB = Text(b, "person_id");
        if (ValidId(personA) && ValidId(personB) && personA != personB) return false;
        return VoiceIdentity(a) is not null && VoiceIdentity(b) is not null && Spellings(a).Overlaps(Spellings(b));
    }
    internal static IEnumerable<JsonElement[]> Groups(JsonElement work)
    {
        var rows = Rows(work, "characters").Take(500).Where(c => ValidId(Text(c, "id"))).Select(c => new Row(c, Spellings(c), Rows(c, "voices").Take(16).ToArray()));
        var groups = new List<List<Row>>();
        foreach (var row in rows)
        {
            // A shared name and actor can describe two distinct characters.
            // Group exact character identities before considering incomplete
            // supplemental display rows; never build transitive identity unions.
            var matches = groups.Where(g => g.Any(other => Text(row.Value, "id") == Text(other.Value, "id"))).ToArray();
            if (matches.Length == 0) groups.Add([row]);
            else { var group = matches[0]; group.Add(row); foreach (var other in matches.Skip(1)) { group.AddRange(other); groups.Remove(other); } }
        }
        // Incomplete supplemental rows can use a unique, independently illustrated
        // roster record with both character and actor spelling corroboration. Two
        // complete records, ambiguous matches or name-only actors are never collapsed.
        var anchors = groups.ToDictionary(g => g, g => g.ToArray());
        foreach (var group in groups.Where(g => g.All(r => r.Supplement)).ToArray())
        {
            // Freeze exact-ID evidence before absorbing supplemental rows. An
            // absorbed row must not become proof for a later, different ID.
            var exact = groups.Where(g => g != group && anchors[g].Any(r => r.IllustratedRoster)
                && anchors[g].Any(r => group.Any(s => s.Names.Overlaps(r.Names)
                    && s.Voices.Any(v => VoiceIdentity(v) is { } id && anchors[g].SelectMany(x => x.Voices).Any(w => VoiceIdentity(w) == id))))).ToArray();
            var matches = exact.Length > 0 ? exact : groups.Where(g => g != group && anchors[g].Any(r => r.IllustratedRoster && group.Any(s => s.Names.Overlaps(r.Names) && s.Voices.Any(v => r.Voices.Any(w => VoiceNamesMatch(v, w)))))).ToArray();
            if (matches.Length == 1) { matches[0].AddRange(group); groups.Remove(group); }
        }
        return groups.Select(g => g.Select(r => r.Value).ToArray()).ToArray();
    }
    internal static IEnumerable<JsonElement> Voices(IEnumerable<JsonElement> rows)
    {
        var kept = new List<JsonElement>();
        foreach (var voice in rows.Where(v => Localized(v).Length > 0))
        {
            var identity = VoiceIdentity(voice);
            if (kept.Any(other => identity is not null && identity == VoiceIdentity(other) || VoiceNamesMatch(voice, other) || identity is null && VoiceIdentity(other) is null && Clean(Localized(voice)) == Clean(Localized(other)))) continue;
            kept.Add(voice); if (kept.Count == 16) break;
        }
        return kept;
    }
}
