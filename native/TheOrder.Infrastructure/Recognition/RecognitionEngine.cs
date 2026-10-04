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
        var local = await evidenceReader.ReadAsync(input.Entry, includePe, cancellation).ConfigureAwait(false);
        progress(RecognitionJobState.Matching);
        RecognitionIdentity identity;
        try { identity = await RecognitionIdentityBuilder.BuildAsync(input.Entry, local.Evidence, index, cancellation).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            var fallback = local.Evidence with { Titles = [.. local.Evidence.Titles.Where(t => t.Source != "readme-heading")], Warnings = local.Evidence.Warnings.Add("本地标题索引不可用，保留已读取的本地证据。") };
            identity = new(fallback, [], RecognitionIdentityBuilder.Names(fallback.Titles.Where(t => t.Source != "sidecar").Select(t => t.Value)).Take(6).ToImmutableArray(), RecognitionIdentityBuilder.References(fallback.References.Select(r => r.Value)));
        }
        var works = new Dictionary<string, WorkMetadata>(StringComparer.Ordinal);
        // An existing relation is only another candidate. It never authorizes replacement.
        if (input.Entry.Metadata is { } prior) works[prior.Id] = prior;
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
                if (!identity.References.IsEmpty) Accept(await catalog.ResolveReferencesAsync(identity.References, token).ConfigureAwait(false));
                foreach (var query in identity.Queries.Take(6)) { token.ThrowIfCancellationRequested(); Accept((await catalog.SearchAsync(query, null, token).ConfigureAwait(false)).Items); }
                // Only the strongest two suggestions receive background details. Other details are on demand.
                var strongest = ranker.Rank(identity.Evidence, works.Values, cancellation: token).Take(2).ToArray();
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
        { works.Clear(); releases.Clear(); if (input.Entry.Metadata is { } priorLocal) works[priorLocal.Id] = priorLocal; unavailable = true; message = "凭据已变化，请重新补充在线候选。"; authSnapshot = null; }
        var ranked = ranker.Rank(identity.Evidence, works.Values, releases, cancellation).Take(5).ToImmutableArray();
        if (works.Count >= 128 || identity.LocalCandidates.Length >= 128)
            ranked = [.. ranked.Select(c => c with { HighConfidence = false, Reason = c.Reason + "；候选达到上限，无法确认唯一性。" })];
        var result = new RecognitionResult(generation, input, local, identity, ranked, TitleMatching.AlgorithmVersion,
            RecognitionFingerprint.Identity(input.Entry), unavailable, message, authSnapshot?.Generation);
        // Final result admission shares the auth boundary; the queue also checks it when publishing.
        try { return authSnapshot is { } accepted ? auth!.Accept(accepted, () => { cancellation.ThrowIfCancellationRequested(); return result; }) : result; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return result with { Candidates = [], AuthGeneration = null, EnrichmentUnavailable = true, EnrichmentMessage = "凭据已变化，请重新补充在线候选。" }; }
    }, cancellation);
}
