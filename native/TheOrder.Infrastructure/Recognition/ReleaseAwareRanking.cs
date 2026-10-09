using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Recognition;

// Keeps the Electron oracle scores and its hard conflicts, then resolves edition identity
// explicitly instead of vetoing every "unspecified" version:
// - A local edition marker is resolved when the candidate's own titles or one of its release
//   titles carry it. If only another candidate carries it, that candidate is the more specific
//   identity and this one needs confirmation. Otherwise it stays unresolved.
// - A marker that only the candidate carries is resolved when other local identity evidence carries
//   it; it is not resolved by absence. An exact plain-title competitor always blocks it.
// - Build versions and display resolutions are packaging, never a different work.
// Executable/archive names and sidecars do not create version uncertainty; their explicit chapter
// or edition contradictions remain hard conflicts in the oracle.
public sealed class ReleaseAwareRanking : IRecognitionRanker
{
    private readonly EvidenceRanking compatibility = new();
    // EvidenceRanking's own uniqueness verdicts for an eligible top candidate.
    private const string SplitReason = "来源编号同时支持多个作品，需要核对", TieReason = "前两候选证据接近，需要补充线索";
    internal static bool IdentitySource(string source) => source is "folder" or "custom-name" or "product-name" or "file-description" or "manual-title" or "config-title" or "readme-title";
    private static bool Packaging(string marker) => marker.StartsWith("version:", StringComparison.Ordinal) || marker.StartsWith("resolution:", StringComparison.Ordinal) || marker is "r18" or "all-ages";
    private static ImmutableHashSet<string> Identity(TitleMarkers m) => [.. m.Editions.Concat(m.Expansions).Where(x => !Packaging(x))];

    public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, IEnumerable<WorkMetadata> works,
        IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases = null, CancellationToken cancellation = default)
    {
        var ranked = compatibility.Rank(evidence, works, releases, cancellation);
        if (ranked.IsEmpty) return ranked;
        var locals = evidence.Titles.Where(t => IdentitySource(t.Source)).DistinctBy(t => t.Value, StringComparer.Ordinal).ToArray();
        var values = locals.Select(t => t.Value).ToArray();
        var names = ranked.ToDictionary(c => c.Work.Id, c => Names(c.Work), StringComparer.Ordinal);
        var resolved = ranked.Select(candidate =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (candidate.Vetoed) return candidate;
            var rows = releases is not null && releases.TryGetValue(candidate.Work.Id, out var r) && !r.IsDefault ? r : [];
            if (Resolve(candidate, values, names, rows, ranked, cancellation) is { } problem)
                return candidate with { Vetoed = true, VersionNeedsConfirmation = true, HighConfidence = false, EvidenceLevel = 0, Reason = problem };
            // An identity title that is exactly an official release title of this work names it.
            if (candidate.Confidence <= .95 && ReleaseExact(candidate, locals, rows) is { } proof)
                return candidate with { Confidence = .955, EvidenceLevel = Math.Max(candidate.EvidenceLevel, 1), HighConfidence = false,
                    Reason = "本地完整标题与正式发行名称一致", Evidence = candidate.Evidence.Add(proof) };
            // A long, specific alias (often a translated title) is weaker than an official title but
            // still names the work; uniqueness and homonym checks below decide whether it is enough.
            if (candidate.Confidence <= .9 && AliasExact(locals, names[candidate.Work.Id]) is { } alias)
                return candidate with { Confidence = .93, EvidenceLevel = Math.Max(candidate.EvidenceLevel, 1), HighConfidence = false,
                    Reason = "本地完整标题与作品别名一致", Evidence = candidate.Evidence.Add(alias) };
            return candidate with { HighConfidence = false };
        }).ToArray();
        static bool Eligible(RankedRecognitionCandidate c) => !c.Vetoed && c.Confidence > .9;
        var ordered = resolved.Select((c, i) => (c, i))
            .OrderBy(p => p.c.Vetoed && !p.c.VersionNeedsConfirmation).ThenByDescending(p => Eligible(p.c))
            .ThenByDescending(p => Eligible(p.c) ? p.c.EvidenceLevel : 0).ThenByDescending(p => p.c.Confidence).ThenBy(p => p.i).Select(p => p.c).ToArray();
        var top = ordered[0];
        if (Eligible(top))
        {
            var oracle = ranked[0];
            string? doubt = Uniqueness(top, ordered, evidence, locals, names[top.Work.Id], releases);
            // The oracle's own split/near-tie judgement stands when it concerns the same work.
            if (doubt is null && oracle.Work.Id == top.Work.Id && !oracle.HighConfidence && oracle.Reason is SplitReason or TieReason) doubt = oracle.Reason;
            ordered[0] = doubt is null ? top with { HighConfidence = true } : top with { Reason = top.Reason + "；" + doubt };
        }
        return [.. ordered];
    }

    private static string? Uniqueness(RankedRecognitionCandidate top, RankedRecognitionCandidate[] ordered, RecognitionEvidence evidence,
        LocalTitleEvidence[] locals, string[] own, IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases)
    {
        // As in the oracle, only candidates with at least the same evidence level contend: a title
        // match does not tie with a matching physical identifier.
        var contender = ordered.Skip(1).Where(o => !o.Vetoed && o.EvidenceLevel >= top.EvidenceLevel && (o.Confidence >= .85 || o.MatchedReferences.Length > 0))
            .Select(o => o.Confidence).DefaultIfEmpty(double.NegativeInfinity).Max();
        if (top.Confidence - contender < .075) return TieReason;
        // Exact titles that support this candidate. If every one of them is shared with another
        // identity in the local index, only an independent physical identifier can make it unique.
        var rows = releases is not null && releases.TryGetValue(top.Work.Id, out var r) && !r.IsDefault ? r : [];
        var supporting = locals.Where(t => own.Concat(rows.Select(x => LocalTitleCleaner.Clean(x.Title))).Any(n => TitleMatching.Compare(t.Value, n) is { Exact: true, Version: "same" })).ToArray();
        var refs = top.Work.References.Select(x => x.Source + ":" + x.Id).ToHashSet(StringComparer.Ordinal);
        bool Shared(LocalTitleEvidence t) => !evidence.Homonyms.IsDefault && evidence.Homonyms.Any(h => TitleMatching.IndexKey(h.ObservedTitle) == TitleMatching.IndexKey(t.Value) && h.Identities.Any(id => !refs.Contains(id)));
        bool physical = top.MatchedReferences.Any(m => evidence.References.Any(e => e.Value == m && e.Source is "steam-config" or "steam-file"));
        if (supporting.Length > 0 && supporting.All(Shared) && !physical) return "本地标题与其他作品同名，需要补充线索";
        return null;
    }

    private static CandidateExplanation? AliasExact(LocalTitleEvidence[] locals, string[] own)
    {
        foreach (var local in locals)
        {
            var key = TitleMatching.IndexKey(local.Value);
            bool specific = key.All(char.IsAscii) ? key.Length >= 8 : key.EnumerateRunes().Count() >= 4;
            if (!specific) continue;
            foreach (var name in own)
                if (TitleMatching.Compare(local.Value, name) is { Exact: true, Distinctive: true, Version: "same", Conflict: null })
                    return new(local.Source, local.Value, name, 1, true, "same", null);
        }
        return null;
    }

    private static CandidateExplanation? ReleaseExact(RankedRecognitionCandidate candidate, LocalTitleEvidence[] locals, ImmutableArray<ReleaseIdentity> releases)
    {
        foreach (var release in releases.Where(r => r.WorkId == candidate.Work.Id))
            foreach (var title in new[] { release.Title, release.OriginalTitle }.Where(t => t.Length is > 0 and < 500).Distinct(StringComparer.Ordinal).Select(LocalTitleCleaner.Clean))
                foreach (var local in locals)
                    if (TitleMatching.Compare(local.Value, title) is { Exact: true, Distinctive: true, Version: "same", Conflict: null })
                        return new(local.Source, local.Value, title, 1, true, "same", null);
        return null;
    }

    internal static string[] Names(WorkMetadata work) => [.. new[] { work.Title, work.OriginalTitle }
        .Concat((work.TitleEvidence ?? []).Where(t => !t.IsMachine).Select(t => t.Text)).Concat(work.Aliases)
        .Where(t => t.Length is > 0 and < 500).Distinct(StringComparer.Ordinal).Take(200)];

    private static bool About(string local, string name, TitleComparison c) =>
        c.Score >= .5 || TitleMatching.Markers(local).Base is { Length: >= 2 } b && b == TitleMatching.Markers(name).Base;

    private static string[] Canonical(WorkMetadata work) => [.. new[] { work.Title, work.OriginalTitle }
        .Concat((work.TitleEvidence ?? []).Where(t => !t.IsMachine && t.Kind is "official" or "original" or "latin").Select(t => t.Text)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)];

    private static string? Resolve(RankedRecognitionCandidate candidate, string[] locals, Dictionary<string, string[]> names,
        ImmutableArray<ReleaseIdentity> releases, ImmutableArray<RankedRecognitionCandidate> all, CancellationToken cancellation)
    {
        var own = names[candidate.Work.Id]; var canonical = Canonical(candidate.Work).ToHashSet(StringComparer.Ordinal);
        foreach (var local in locals)
        {
            cancellation.ThrowIfCancellationRequested();
            // Only this work's names that the local title is about, and that do not contradict it.
            var relevant = own.Select(n => (Name: n, Comparison: TitleMatching.Compare(local, n)))
                .Where(p => p.Comparison.Version != "conflict" && About(local, p.Name, p.Comparison)).ToArray();
            if (relevant.Length == 0 || relevant.Any(p => p.Comparison.Exact && p.Comparison.Version == "same")) continue;
            var mine = Identity(TitleMatching.Markers(local));
            if (!mine.IsEmpty && !relevant.Any(p => Identity(TitleMatching.Markers(p.Name)).IsSupersetOf(mine)))
            {
                bool Covers(string title) => Identity(TitleMatching.Markers(title)).IsSupersetOf(mine)
                    && TitleMatching.Compare(local, title) is { Version: not "conflict" } c && About(local, title, c);
                if (!releases.Any(r => r.WorkId == candidate.Work.Id && (Covers(r.Title) || Covers(r.OriginalTitle))))
                    return all.Any(o => o.Work.Id != candidate.Work.Id && !o.Vetoed && names[o.Work.Id].Any(Covers))
                        ? "另一候选包含本地标题中的版本标识；请核对后选择。"
                        : "本地标题含有资料中未确认的版本标识；请核对后选择。";
            }
            // A plain title is not proof of a remake/FD that every relevant official title names.
            // Another local identity source must say so. Descriptive aliases do not count here.
            var official = relevant.Where(p => canonical.Contains(p.Name)).Select(p => Identity(TitleMatching.Markers(p.Name))).ToArray();
            if (official.Length > 0 && official.All(m => !m.IsSubsetOf(mine)))
            {
                var remoteExtra = official.OrderBy(m => m.Count).First().Except(mine);
                bool carried = locals.Any(other => other != local && Identity(TitleMatching.Markers(other)).IsSupersetOf(remoteExtra)
                    && own.Any(n => TitleMatching.Compare(other, n) is { Version: not "conflict" } c && About(other, n, c)));
                if (!carried) return "作品资料含有本地标题未体现的版本标识；请核对后选择。";
            }
        }
        return null;
    }
}
