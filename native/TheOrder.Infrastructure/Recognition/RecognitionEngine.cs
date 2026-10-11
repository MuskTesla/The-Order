using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Recognition;

// No repository reference. Input is immutable and output is a suggestion, even at score 1.
public sealed class RecognitionEngine(ITitleIndex index, IRecognitionEvidenceReader evidenceReader, IRecognitionRanker ranker,
    IRecognitionCatalog? catalog = null, OnlineAuthContext? auth = null) : IRecognitionEngine
{
    public Task<RecognitionResult> RecognizeAsync(RecognitionEntrySnapshot input, Guid generation, bool online, bool includePe,
        Action<RecognitionJobState> progress, CancellationToken cancellation) => Task.Run(async () =>
    {
        cancellation.ThrowIfCancellationRequested(); progress(RecognitionJobState.CollectingEvidence);
        var local = await evidenceReader.ReadAsync(input.EvidenceEntry, includePe, cancellation).ConfigureAwait(false);
        progress(RecognitionJobState.Matching);
        RecognitionIdentity identity;
        try { identity = await RecognitionIdentityBuilder.BuildAsync(input.EvidenceEntry, local.Evidence, index, cancellation).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            var fallback = local.Evidence with { Titles = [.. local.Evidence.Titles.Where(t => t.Source != "readme-heading")], Warnings = local.Evidence.Warnings.Add("本地标题索引不可用，保留已读取的本地证据。") };
            identity = new(fallback, [], RecognitionIdentityBuilder.Names(fallback.Titles.Where(t => t.Source != "sidecar").Select(t => t.Value)).Take(6).ToImmutableArray(), RecognitionIdentityBuilder.References(fallback.References.Select(r => r.Value)));
        }
        var works = new Dictionary<string, WorkMetadata>(StringComparer.Ordinal);
        // An existing relation is only another candidate. It never authorizes replacement.
        if (input.EvidenceEntry.Metadata is { } prior) works[prior.Id] = prior;
        var releases = new Dictionary<string, ImmutableArray<ReleaseIdentity>>(StringComparer.Ordinal);
        bool unavailable = !online || catalog is null; string? message = unavailable ? "本地识别已完成，在线补充未启用。" : null;
        OnlineAuthSnapshot? authSnapshot = null;
        if (online && catalog is not null)
        {
            progress(RecognitionJobState.Enriching);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                if (auth is not null) authSnapshot = await auth.CaptureAsync(budget.Token).ConfigureAwait(false);
                using var epoch = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, authSnapshot?.Changed ?? default);
                var token = epoch.Token;
                void Accept(IEnumerable<WorkMetadata> candidates)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var candidate in candidates.Take(128)) { if (works.Count >= 128 && !works.ContainsKey(candidate.Id)) break; works[candidate.Id] = candidate; }
                }
                var retrievalReferences = identity.References.Concat(RecognitionIdentityBuilder.References(identity.LocalCandidates.Where(c => !c.Exact).SelectMany(c => c.Record.References)))
                    .Distinct(StringComparer.Ordinal).Take(8).ToImmutableArray();
                if (!retrievalReferences.IsEmpty) Accept(await catalog.ResolveReferencesAsync(retrievalReferences, token).ConfigureAwait(false));
                var exactQueries = identity.Evidence.Titles.Where(t => ReleaseAwareRanking.IdentitySource(t.Source)).Select(t => TitleMatching.IndexKey(t.Value)).ToHashSet(StringComparer.Ordinal);
                var singletonQueries = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var query in identity.Queries.Take(6))
                {
                    token.ThrowIfCancellationRequested(); var page = await catalog.SearchAsync(query, null, token).ConfigureAwait(false); Accept(page.Items);
                    // Keep where an incomplete card was found. A longer literal local query can
                    // retrieve the right work even when a later short query has high-scoring
                    // unrelated aliases. This affects detail scheduling only, never confidence.
                    var key = TitleMatching.IndexKey(query);
                    if (page.NextCursor is null && page.Items.DistinctBy(w => w.Id).Take(2).ToArray() is [var single]
                        && exactQueries.Contains(key) && key.Length >= 8)
                        singletonQueries[single.Id] = Math.Max(singletonQueries.GetValueOrDefault(single.Id), key.Length);
                }
                // Resolve/search can return an index-referenced card without any official titles.
                // Its weak card score cannot rule out the work before fetching that missing detail.
                // This changes bounded retrieval only; inferred index IDs still grant no write or
                // ranking authority, and the complete candidates must pass the same final checks.
                var preliminary = ranker.Rank(identity.Evidence, works.Values, cancellation: token);
                static bool Incomplete(WorkMetadata work) => !(work.TitleEvidence ?? []).Any(t => !t.IsMachine && t.Kind is "official" or "original" or "latin");
                int longestStrongSupport = preliminary.Where(c => !c.Vetoed && c.Confidence > .9).SelectMany(c => c.Evidence)
                    .Where(e => e.Exact && e.Conflict is null && e.Version == "same" && ReleaseAwareRanking.IdentitySource(e.Source))
                    .Select(e => TitleMatching.IndexKey(e.ObservedTitle).Length).DefaultIfEmpty().Max();
                var specificIncomplete = preliminary.Where(c => !c.Vetoed && Incomplete(c.Work) && singletonQueries.GetValueOrDefault(c.Work.Id) > longestStrongSupport)
                    .OrderByDescending(c => singletonQueries[c.Work.Id]);
                var requestedReferences = retrievalReferences.ToHashSet(StringComparer.Ordinal);
                var incompleteReferenced = preliminary.Where(c => !c.Vetoed && Incomplete(c.Work)
                    && c.Work.References.Any(r => requestedReferences.Contains(r.Source + ":" + r.Id)));
                // Only repair weak-card retrieval when there is no strong compatible title.
                // A reference returned for a parent/related edition must not displace the
                // already strongest edition candidates. The two-detail budget is unchanged.
                var retrievalOrder = preliminary.Any(c => !c.Vetoed && c.Confidence > .9)
                    ? preliminary : incompleteReferenced.Concat(preliminary);
                var strongest = specificIncomplete.Concat(retrievalOrder).DistinctBy(c => c.Work.Id).Take(2).ToArray();
                foreach (var candidate in strongest)
                {
                    token.ThrowIfCancellationRequested();
                    var detail = await catalog.DetailsAsync(candidate.Work.Id, false, token).ConfigureAwait(false);
                    works.Remove(candidate.Work.Id); Accept([detail]);
                    releases[detail.Id] = await catalog.ReleasesAsync(detail.Id, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (authSnapshot is { } stamp) auth!.Accept(stamp, () => true);
            }
            catch (OnlineException e) { unavailable = true; message = e.Message; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { unavailable = true; message = "在线补充已取消或凭据已变化，本地候选仍然保留。"; }
        }
        cancellation.ThrowIfCancellationRequested();
        // Discard the entire remote aggregate on an epoch change, including earlier successful queries.
        if (authSnapshot is { } captured && (captured.Changed.IsCancellationRequested || auth!.Generation != captured.Generation))
        { works.Clear(); releases.Clear(); if (input.EvidenceEntry.Metadata is { } priorLocal) works[priorLocal.Id] = priorLocal; unavailable = true; message = "凭据已变化，请重新补充在线候选。"; authSnapshot = null; }
        var ranked = ranker.Rank(identity.Evidence, works.Values, releases, cancellation).Take(5).ToImmutableArray();
        if (works.Count >= 128 || identity.LocalCandidates.Length >= 128)
            ranked = [.. ranked.Select(c => c with { HighConfidence = false, Reason = c.Reason + "；候选达到上限，无法确认唯一性。" })];
        var result = new RecognitionResult(generation, input, local, identity, ranked, TitleMatching.AlgorithmVersion,
            RecognitionFingerprint.Identity(input.Entry), unavailable, message, authSnapshot?.Generation)
        { ReleaseContext = releases.ToImmutableDictionary(StringComparer.Ordinal) };
        // Final result admission shares the auth boundary; the queue also checks it when publishing.
        try { return authSnapshot is { } accepted ? auth!.Accept(accepted, () => { cancellation.ThrowIfCancellationRequested(); return result; }) : result; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return result.WithoutEnrichment(); }
    }, cancellation);
}
