using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Recognition;

public static class RecognitionIdentityBuilder
{
    public static ImmutableArray<string> Names(IEnumerable<string> values) => [.. values.Select(TitleMatching.Prepared)
        .Where(s => TitleMatching.IndexKey(s).Length > 0).DistinctBy(TitleMatching.IndexKey, StringComparer.Ordinal).Take(8)];
    public static ImmutableArray<string> References(IEnumerable<string> values)
    {
        var unique = values.Select(LocalEvidenceParser.NormalizeReference).OfType<string>().Distinct(StringComparer.Ordinal).Take(128).ToArray();
        return [.. new[] { "vndb:", "bangumi:", "steam:" }.SelectMany(source => unique.Where(r => r.StartsWith(source, StringComparison.Ordinal)).Take(2)).Concat(unique).Distinct(StringComparer.Ordinal).Take(8)];
    }
    public static async Task<RecognitionIdentity> BuildAsync(LibraryEntry entry, RecognitionEvidence evidence, ITitleIndex index, CancellationToken cancellation)
    {
        var readme = await index.FindReadmeTitlesAsync([.. evidence.Titles.Where(t => t.Source == "readme-heading").Select(t => t.Value)], cancellation).ConfigureAwait(false);
        evidence = evidence with { Titles = [.. evidence.Titles.Where(t => t.Source != "readme-heading").Concat(readme.Select(t => new LocalTitleEvidence(t, "readme-title"))).Take(40)] };
        var names = Names(evidence.Titles.Where(t => t.Source != "sidecar").Select(t => t.Value));
        var matches = await index.FindAsync(names, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var identities = matches.Select(m => References(m.Record.References).FirstOrDefault() ?? "unknown:" + m.Row).Distinct(StringComparer.Ordinal).ToArray();
        // A saturated lookup is incomplete: an unseen row can carry a conflicting identity.
        var unambiguous = identities.Length == 1 && matches.Length < 128 ? matches : [];
        var indexedRefs = References(unambiguous.SelectMany(m => m.Record.References));
        var proofs = unambiguous.SelectMany(m => evidence.Titles.Where(t => t.Source is not ("sidecar" or "readme-title") && m.Record.Names.Any(n => TitleMatching.IndexKey(n) == TitleMatching.IndexKey(t.Value)))
            .Select(t => new TitleIndexProof(t.Value, t.Source, [.. m.Record.Names.Take(30)], References(m.Record.References)))).Take(20).ToImmutableArray();
        var combined = evidence with { References = [.. evidence.References.Concat(indexedRefs.Select(r => new ReferenceEvidence(r, "title-index"))).Take(40)], IndexProofs = proofs,
            Warnings = matches.Length >= 128 ? evidence.Warnings.Add("本地同名候选达到上限，无法判断唯一性，请手动核对。") : evidence.Warnings };
        var primary = Names(evidence.Titles.Where(t => t.Source is "manual-title" or "config-title").Select(t => t.Value)
            .Concat(evidence.Titles.Where(t => t.Source is "product-name" or "file-description").Select(t => t.Value)).Append(entry.Name)
            .Concat(evidence.Titles.Where(t => t.Source is "folder" or "readme-title").Select(t => t.Value))
            .Concat(evidence.Titles.Where(t => t.Source is not ("sidecar" or "custom-name")).Select(t => t.Value))).Where(v => TitleMatching.Compare(v, v).Score > 0).ToArray();
        var queries = primary.Take(3).Concat(primary.SelectMany(TitleMatching.Variants)).Concat(primary.Skip(3)).Concat(unambiguous.SelectMany(m => m.Record.Names))
            .Where(v => TitleMatching.Compare(v, v).Score > 0).DistinctBy(v => TitleMatching.Compact(TitleMatching.Normalize(v)), StringComparer.Ordinal).Take(6).ToImmutableArray();
        return new(combined, matches, queries, References(combined.References.Select(r => r.Value)));
    }
}
