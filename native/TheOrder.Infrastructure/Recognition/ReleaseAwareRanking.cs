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
    internal static bool Packaging(string marker) => marker.StartsWith("version:", StringComparison.Ordinal) || marker.StartsWith("resolution:", StringComparison.Ordinal) || marker is "r18" or "all-ages";
    internal static ImmutableHashSet<string> Identity(TitleMarkers m) => [.. m.Editions.Concat(m.Expansions).Where(x => !Packaging(x))];

    public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence, IEnumerable<WorkMetadata> works,
        IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases = null, CancellationToken cancellation = default)
    {
        // Index identifiers plan retrieval and support explicit same-row proofs. They are inferred
        // from a title, not independently read identifiers that can veto a competing work.
        var ranked = compatibility.Rank(evidence with { References = [.. evidence.References.Where(r => r.Source != "title-index")] }, works, releases, cancellation);
        if (ranked.IsEmpty) return ranked;
        var locals = evidence.Titles.Where(t => IdentitySource(t.Source)).DistinctBy(t => t.Value, StringComparer.Ordinal).ToArray();
        var values = locals.Select(t => t.Value).ToArray();
        var chineseSpellings = values.ToDictionary(v => v, LocalQueryVariants.Chinese, StringComparer.Ordinal);
        var names = ranked.ToDictionary(c => c.Work.Id, c => Names(c.Work), StringComparer.Ordinal);
        var resolved = ranked.Select(candidate =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (candidate.Vetoed) return candidate;
            var rows = releases is not null && releases.TryGetValue(candidate.Work.Id, out var r) && !r.IsDefault ? r : [];
            if (Resolve(candidate, values, names, rows, ranked, cancellation) is { } problem)
                return candidate with { Vetoed = true, VersionNeedsConfirmation = true, HighConfidence = false, EvidenceLevel = 0, Reason = problem };
            // Short product names are not identifying on their own. A binary's product title,
            // its publisher, an unambiguous index row and this work's release must agree.
            if (ProgramCompanyProof(evidence, candidate.Work, rows) is { } program)
                return candidate with { Confidence = Math.Max(candidate.Confidence, .975), EvidenceLevel = Math.Max(candidate.EvidenceLevel, 2), HighConfidence = false,
                    Reason = "程序作品名称、发行公司与索引及正式发行资料相互印证", Evidence = candidate.Evidence.Add(program) };
            // An identity title that is exactly an official release title of this work names it.
            if (candidate.Confidence <= .95 && ReleaseExact(candidate, locals, rows) is { } proof)
                return candidate with { Confidence = .955, EvidenceLevel = Math.Max(candidate.EvidenceLevel, 1), HighConfidence = false,
                    Reason = "本地完整标题与正式发行名称一致", Evidence = candidate.Evidence.Add(proof) };
            if (candidate.Confidence <= .9 && ChineseExact(locals, candidate.Work, chineseSpellings) is { } spelling)
                return candidate with { Confidence = .94, EvidenceLevel = Math.Max(candidate.EvidenceLevel, 1), HighConfidence = false,
                    Reason = "本地中文标题与非机器官方中文标题繁简一致", Evidence = candidate.Evidence.Add(spelling) };
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
        // Among near-tied eligible candidates of the top evidence level, one whose exact local titles
        // dominate every other member's goes first, independent of catalog id order.
        var group = ordered.TakeWhile(c => Eligible(c) && c.EvidenceLevel == ordered[0].EvidenceLevel && ordered[0].Confidence - c.Confidence < .075).ToArray();
        if (group.Length > 1 && group.FirstOrDefault(a => group.All(b => b == a || Dominates(a, b, locals, names, releases))) is { } dominant && dominant != ordered[0])
            ordered = [dominant, .. ordered.Where(c => c != dominant)];
        var top = ordered[0];
        if (Eligible(top))
        {
            var oracle = ranked[0];
            string? doubt = Uniqueness(top, ordered, evidence, locals, names, releases);
            // The oracle's identifier split verdict stands when it concerns the same work. Near ties
            // are recomputed above, with release evidence and specificity the oracle lacks.
            if (doubt is null && oracle.Work.Id == top.Work.Id && !oracle.HighConfidence && oracle.Reason == SplitReason
                && ranked.Any(c => c.MatchedReferences.Length > 0 && c.Work.Id != top.Work.Id && !SameCatalogIdentity(top.Work, c.Work))) doubt = oracle.Reason;
            var reason = top.Reason.Replace("；" + TieReason, "", StringComparison.Ordinal).Replace("；" + SplitReason, "", StringComparison.Ordinal);
            if (reason is TieReason or SplitReason) reason = "名称与资料一致";
            if (doubt is null && ordered.Skip(1).Any(c => !c.Vetoed && SameCatalogIdentity(top.Work, c.Work)))
                reason += "；同版本目录记录经标题及来源资料复核，优先采用身份资料更完整的记录";
            ordered[0] = doubt is null ? top with { HighConfidence = true, Reason = reason } : top with { Reason = reason + "；" + doubt };
        }
        return [.. ordered];
    }

    // Local identity titles that exactly name this candidate or one of its known releases.
    private static LocalTitleEvidence[] Supporting(RankedRecognitionCandidate candidate, LocalTitleEvidence[] locals, string[] own,
        IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases)
    {
        var rows = releases is not null && releases.TryGetValue(candidate.Work.Id, out var r) && !r.IsDefault ? r : [];
        var names = own.Concat(rows.Select(x => LocalTitleCleaner.Clean(x.Title))).ToArray();
        return [.. locals.Where(t => names.Any(n => TitleMatching.Compare(t.Value, n) is { Exact: true, Version: "same" })
            || ChineseExact([t], candidate.Work) is not null)];
    }

    // A candidate whose only exact local titles are truncations of a longer local title that names
    // the other candidate exactly ("Escalation" vs "Escalation ~Kyouai no Fugue~") does not tie
    // with it: the more specific local evidence decides.
    private static bool Dominates(RankedRecognitionCandidate a, RankedRecognitionCandidate b, LocalTitleEvidence[] locals,
        Dictionary<string, string[]> names, IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases)
    {
        var mine = Supporting(a, locals, names[a.Work.Id], releases); var theirs = Supporting(b, locals, names[b.Work.Id], releases);
        // Same-row title evidence can distinguish records only after independent catalog metadata
        // confirms the same edition. An unidentified homonym remains a competing identity.
        if (mine.Length > 0 && a.MatchedReferences.Length > 0 && b.MatchedReferences.Length == 0
            && SameCatalogIdentity(a.Work, b.Work) && theirs.All(mine.Contains)) return true;
        // Choose the richer representation only inside a corroborated equivalent group. Scores
        // and numerical work IDs do not decide between different identities.
        if (mine.Length > 0 && theirs.Length > 0 && SameCatalogIdentity(a.Work, b.Work) && theirs.All(mine.Contains)
            && CatalogCoverage(a.Work).CompareTo(CatalogCoverage(b.Work)) > 0) return true;
        return theirs.Length > 0 && mine.Any(t => !theirs.Contains(t)) && theirs.All(s => mine.Any(t =>
            TitleMatching.IndexKey(t.Value) is var longer && TitleMatching.IndexKey(s.Value) is var shorter && longer.Length > shorter.Length && longer.Contains(shorter, StringComparison.Ordinal)));
    }

    private static string? Uniqueness(RankedRecognitionCandidate top, RankedRecognitionCandidate[] ordered, RecognitionEvidence evidence,
        LocalTitleEvidence[] locals, Dictionary<string, string[]> names, IReadOnlyDictionary<string, ImmutableArray<ReleaseIdentity>>? releases)
    {
        var supporting = Supporting(top, locals, names[top.Work.Id], releases);
        // As in the oracle, only candidates with at least the same evidence level contend: a title
        // match does not tie with a matching physical identifier.
        var contender = ordered.Skip(1).Where(o => !o.Vetoed && o.EvidenceLevel >= top.EvidenceLevel && (o.Confidence >= .85 || o.MatchedReferences.Length > 0) && !Dominates(top, o, locals, names, releases))
            .Select(o => o.Confidence).DefaultIfEmpty(double.NegativeInfinity).Max();
        if (top.Confidence - contender < .075) return TieReason;
        // If every exact supporting title is shared with another identity in the local index, only
        // an independent physical identifier can make the candidate unique.
        var refs = top.Work.References.Select(x => x.Source + ":" + x.Id).ToHashSet(StringComparer.Ordinal);
        bool Shared(LocalTitleEvidence t) => !evidence.Homonyms.IsDefault && evidence.Homonyms.Any(h => TitleMatching.IndexKey(h.ObservedTitle) == TitleMatching.IndexKey(t.Value) && h.Identities.Any(id => !refs.Contains(id)));
        bool physical = top.MatchedReferences.Any(m => evidence.References.Any(e => e.Value == m && e.Source is "steam-config" or "steam-file"));
        if (supporting.Length > 0 && supporting.All(Shared) && !physical) return "本地标题与其他作品同名，需要补充线索";
        // A short Latin word can name unrelated works, including one missing from both the
        // catalog result and our finite index. Repeating that word in an index/release is not
        // independent identity evidence. This is a narrow weak-input guard, not an index blacklist.
        var localKeys = locals.Select(t => TitleMatching.IndexKey(t.Value)).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        bool directIdentifier = top.MatchedReferences.Any(m => evidence.References.Any(e => e.Value == m && e.Source is not ("title-index" or "sidecar")));
        var rows = releases is not null && releases.TryGetValue(top.Work.Id, out var available) && !available.IsDefault ? available : [];
        if (!directIdentifier && ProgramCompanyProof(evidence, top.Work, rows) is null && localKeys.Length == 1 && localKeys[0].Length <= 6 && localKeys[0].All(char.IsAsciiLetter))
            return "本地只有短拉丁名称，索引和同名发行资料不能区分同名作品，请补充独立编号或手动核对";
        return null;
    }

    private static CandidateExplanation? ProgramCompanyProof(RecognitionEvidence evidence, WorkMetadata work, ImmutableArray<ReleaseIdentity> releases)
    {
        if (work.CatalogIdentity is not { } catalog || catalog.Companies.IsDefault || evidence.IndexProofs.IsDefault) return null;
        static string Brand(string value) => TitleMatching.Compact(TitleMatching.Rx("株式会社|有限会社").Replace(
            TitleMatching.Rx(@"\b(?:copyright|corporation|company|co|ltd|limited|inc|llc|corp|\d{4})\b").Replace(TitleMatching.Normalize(value), ""), ""));
        bool Same(string a, string b) => TitleMatching.Compare(a, b) is { Exact: true, Version: "same", Conflict: null };
        bool Company(string value) => Brand(value) is { Length: >= 3 } brand && catalog.Companies.Take(32).Any(c => Brand(c) == brand);
        if (!evidence.Companies.Any(Company)) return null;
        var own = Canonical(work); var refs = work.References.Select(r => r.Source + ":" + r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var program in evidence.Titles.Where(t => t.Source is "product-name" or "file-description"))
        {
            if (!own.Any(n => Same(program.Value, n)) || !evidence.Titles.Any(t => t.Source == "folder" && Same(t.Value, program.Value))) continue;
            if (!evidence.IndexProofs.Any(p => p.Source is "folder" or "product-name" or "file-description" && Same(p.ObservedTitle, program.Value)
                && p.RecordNames.Any(n => Same(n, program.Value)) && p.References.Any(refs.Contains)
                && !p.References.GroupBy(r => r.Split(':')[0], StringComparer.Ordinal).Any(g => g.Distinct(StringComparer.Ordinal).Count() > 1))) continue;
            if (!releases.Any(r => r.WorkId == work.Id && r.Kind is "default" or "digital" or "physical"
                && new[] { r.Title, r.OriginalTitle }.Any(n => Same(n, program.Value)))) continue;
            return new(program.Source, program.Value, own.First(n => Same(program.Value, n)), 1, true, "same", null);
        }
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

    private static CandidateExplanation? ChineseExact(LocalTitleEvidence[] locals, WorkMetadata work, Dictionary<string, string[]>? spellings = null)
    {
        var titles = (work.TitleEvidence ?? []).Where(t => !t.IsMachine && t.Kind is "official" or "original").Take(200).ToArray();
        foreach (var local in locals)
        {
            var scripts = spellings?.GetValueOrDefault(local.Value) ?? LocalQueryVariants.Chinese(local.Value);
            if (scripts.Length == 0) continue;
            foreach (var official in titles)
                if (LocalQueryVariants.ChineseExact(local.Value, official.Text, official.Language, scripts))
                    return new(local.Source, local.Value, official.Text, 1, true, "same", null);
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

    private static bool MachineOnly(WorkMetadata work, string value)
    {
        var matching = (work.TitleEvidence ?? []).Where(t => TitleMatching.IndexKey(t.Text) == TitleMatching.IndexKey(value)).ToArray();
        return matching.Length > 0 && matching.All(t => t.IsMachine);
    }

    internal static string[] Names(WorkMetadata work) => [.. new[] { work.Title, work.OriginalTitle }
        .Concat((work.TitleEvidence ?? []).Where(t => !t.IsMachine).Select(t => t.Text)).Concat(work.Aliases)
        .Where(t => t.Length is > 0 and < 500 && !MachineOnly(work, t)).Distinct(StringComparer.Ordinal).Take(200)];

    private static bool About(string local, string name, TitleComparison c) =>
        c.Score >= .5 || TitleMatching.Markers(local).Base is { Length: >= 2 } b && b == TitleMatching.Markers(name).Base;

    private static string[] Canonical(WorkMetadata work) => [.. new[] { work.Title, work.OriginalTitle }
        .Where(t => !(work.TitleEvidence ?? []).Any(p => TitleMatching.IndexKey(p.Text) == TitleMatching.IndexKey(t))
            || (work.TitleEvidence ?? []).Any(p => TitleMatching.IndexKey(p.Text) == TitleMatching.IndexKey(t) && !p.IsMachine && p.Kind is "official" or "original" or "latin"))
        .Concat((work.TitleEvidence ?? []).Where(t => !t.IsMachine && t.Kind is "official" or "original" or "latin").Select(t => t.Text)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)];

    private static bool SameCatalogIdentity(WorkMetadata a, WorkMetadata b)
    {
        static bool WorkReference(WorkReference r) => r.Source == "vndb" ? TitleMatching.Rx(@"^v[1-9]\d*$").IsMatch(r.Id)
            : r.Source is "bangumi" or "erogamescape" && TitleMatching.Rx(@"^[1-9]\d*$").IsMatch(r.Id);
        var left = a.References.Where(WorkReference).ToArray();
        var right = b.References.Where(WorkReference).ToArray();
        if (left.Concat(right).GroupBy(r => r.Source, StringComparer.Ordinal).Any(g => g.GroupBy(r => r.Id).Count() > 1)) return false;
        // A common title or official site cannot overrule contradictory source identifiers.
        if (left.Any(x => right.Any(y => x.Source == y.Source) && !right.Any(y => x.Source == y.Source && x.Id == y.Id))
            || right.Any(x => left.Any(y => x.Source == y.Source) && !left.Any(y => x.Source == y.Source && x.Id == y.Id))) return false;
        var mine = Canonical(a); var theirs = Canonical(b);
        if (!mine.SelectMany(t => Identity(TitleMatching.Markers(t))).ToHashSet(StringComparer.Ordinal)
                .SetEquals(theirs.SelectMany(t => Identity(TitleMatching.Markers(t))))
            || !mine.Any(x => theirs.Any(y => TitleMatching.Compare(x, y) is { Exact: true, Version: "same", Conflict: null }))) return false;
        if (left.Any(x => right.Any(y => x.Source == y.Source && x.Id == y.Id))) return true;
        if (left.Length == 0 || right.Length == 0) return false;
        var sitesA = ProductSites(a); var sitesB = ProductSites(b);
        if (sitesA.Intersect(sitesB, StringComparer.Ordinal).Any()) return true;
        // Some sources catalog a named reboot separately, without a cross-reference or site.
        // This is a narrow catalog-level inference, not an independently read local identifier:
        // both primary original titles must exactly name a long, explicitly marked edition,
        // and each record must be backed by a different, non-contradictory known source.
        string originalA = a.OriginalTitle, originalB = b.OriginalTitle;
        var key = TitleMatching.IndexKey(originalA);
        return key.Length >= 16 && !Identity(TitleMatching.Markers(originalA)).IsEmpty
            && mine.Contains(originalA, StringComparer.Ordinal) && theirs.Contains(originalB, StringComparer.Ordinal)
            && TitleMatching.Compare(originalA, originalB) is { Exact: true, Version: "same", Conflict: null }
            && !left.Any(x => right.Any(y => x.Source == y.Source))
            && Identity(TitleMatching.Markers(a.Title)).SetEquals(Identity(TitleMatching.Markers(originalA)))
            && Identity(TitleMatching.Markers(b.Title)).SetEquals(Identity(TitleMatching.Markers(originalB)));
    }

    private static string[] ProductSites(WorkMetadata work) => work.CatalogIdentity is not { OfficialSites.IsDefault: false } info ? []
        : [.. info.OfficialSites.Take(32).Select(raw =>
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0) return "";
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)) path = path[..^11];
            return path.Length < 3 ? "" : uri.IdnHost.ToLowerInvariant() + path;
        }).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];

    private static (int Sites, int Titles, int Sources, int Companies) CatalogCoverage(WorkMetadata work) =>
        (ProductSites(work).Length, Canonical(work).Length, work.References.Select(r => r.Source).Distinct(StringComparer.Ordinal).Count(),
            work.CatalogIdentity is { Companies.IsDefault: false } info ? info.Companies.Length : 0);

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
            if (relevant.Length == 0) continue;
            var mine = Identity(TitleMatching.Markers(local));
            bool Names(string title) => Identity(TitleMatching.Markers(title)).IsSupersetOf(mine)
                && TitleMatching.Compare(local, title) is { Version: not "conflict" } c && About(local, title, c);
            // An inconsistent parent record may carry the edition in OriginalTitle while its
            // displayed official title still names the base work. Prefer a separate record whose
            // primary title names that exact edition in the same title language.
            if (!mine.IsEmpty && !Identity(TitleMatching.Markers(candidate.Work.Title)).IsSupersetOf(mine)
                && all.Any(o => o.Work.Id != candidate.Work.Id && !o.Vetoed && Names(o.Work.Title)
                    && TitleMatching.Markers(o.Work.Title).Base == TitleMatching.Markers(candidate.Work.Title).Base))
                return "另一候选的主标题明确对应本地版本；当前记录的主标题与版本资料不一致，请核对后选择。";
            // An official title of another work naming the edition outranks this work listing it only
            // as an alias (a catalog can hold both).
            if (!mine.IsEmpty && !relevant.Any(p => canonical.Contains(p.Name) && Names(p.Name))
                && all.Any(o => o.Work.Id != candidate.Work.Id && !o.Vetoed && Canonical(o.Work).Any(Names)))
                return "另一候选的正式标题包含本地标题中的版本标识；请核对后选择。";
            if (relevant.Any(p => p.Comparison.Exact && p.Comparison.Version == "same")) continue;
            if (!mine.IsEmpty && !relevant.Any(p => Identity(TitleMatching.Markers(p.Name)).IsSupersetOf(mine)))
            {
                bool Covers(string title) => Identity(TitleMatching.Markers(title)).IsSupersetOf(mine)
                    && TitleMatching.Compare(local, title) is { Version: not "conflict" } c && About(local, title, c);
                // A catalog that represents the edition as its own work names it more specifically than
                // a release of this work does (some catalogs list both).
                if (all.Any(o => o.Work.Id != candidate.Work.Id && !o.Vetoed && names[o.Work.Id].Any(Covers)))
                    return "另一候选包含本地标题中的版本标识；请核对后选择。";
                if (!releases.Any(r => r.WorkId == candidate.Work.Id && (Covers(r.Title) || Covers(r.OriginalTitle))))
                    return "本地标题含有资料中未确认的版本标识；请核对后选择。";
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
