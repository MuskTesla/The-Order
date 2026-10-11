using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record CoverRecommendation(string? Url, string? Signature, bool Ready);
public interface ICoverRecommendationSource
{
    Task<CoverRecommendation> RecommendationAsync(string workId, CancellationToken cancellation);
}
// Reads a saved display decision only: no catalog request, image load or preparation.
// Null means no usable saved decision; a decision with a null key intentionally clears the cover.
public sealed record CachedCoverRecommendation(string? Key);
public interface ICachedCoverRecommendationSource
{
    Task<CachedCoverRecommendation?> ReadCoverAsync(string workId, CancellationToken cancellation);
}

public sealed partial class CatalogPresentationService : ICoverRecommendationSource, ICachedCoverRecommendationSource
{
    public async Task<CachedCoverRecommendation?> ReadCoverAsync(string workId, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        stop.Token.ThrowIfCancellationRequested();
        if (!ValidId(workId)) return null;
        var entry = await ReadCachedAsync(workId, stop.Token).ConfigureAwait(false);
        if (entry is null || !CoverReady(entry)) return null;
        var key = registry.Register(entry.Work.CoverUrl, entry.Work.FetchedAt);
        return entry.Work.CoverUrl is not null && key is null ? null : new(key);
    }

    public async Task<CoverRecommendation> RecommendationAsync(string workId, CancellationToken cancellation)
    {
        var entry = await GetAsync(workId, cancellation: cancellation).ConfigureAwait(false);
        bool ready = CoverReady(entry);
        if (!ready) Queue([workId], priority: true);
        return ready ? new(entry.Work.CoverUrl, entry.CoverDecision!.InputSignature, true) : new(null, null, false);
    }

    private async Task<CatalogPresentationEntry> PrepareCoverAsync(CatalogPresentationEntry entry, CancellationToken cancellation)
    {
        var work = entry.Work;
        if (!entry.CatalogCoverKnown)
        {
            try
            {
                var fresh = await catalog.DetailsAsync(work.Id, false, cancellation).ConfigureAwait(false);
                lock (gate) catalogLoads++;
                if (fresh.Id != work.Id) throw new OnlineException(OnlineFailure.InvalidResponse);
                entry = entry with { CatalogCover = CoverAesthetics.CatalogDefault(fresh), CatalogCoverKnown = true };
            }
            catch (OnlineException)
            {
                // v21's verified result can remain while the primary is offline.
                // An unclassified legacy scan must never become an aesthetic candidate.
                var fallback = CoverAesthetics.Candidates(work, null);
                work = work with { CoverUrl = fallback.Any(c => c.Cover.Url == work.CoverUrl) ? work.CoverUrl : fallback.FirstOrDefault()?.Cover.Url };
                var incomplete = CachedCatalogWork.From(work);
                return entry with { Current = incomplete, Signature = Signature(incomplete, entry.Reference), CoverSchema = 0 };
            }
        }
        var candidates = CoverAesthetics.Candidates(work, entry.CatalogCover);
        var input = CoverAesthetics.Signature(candidates);
        if (CoverReady(entry)) return entry;

        var rows = ImmutableArray.CreateBuilder<CoverAssessment>(); bool complete = true;
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = registry.Register(candidate.Cover.Url, work.FetchedAt);
            if (key is null) { complete = false; continue; }
            var result = await images.LoadAsync(key, CoverAesthetics.AnalysisEdge, cancellation).ConfigureAwait(false);
            if (result.Pixels is not { } pixels) { complete = false; continue; }
            rows.Add(CoverAesthetics.Assess(candidate, pixels));
        }
        var assessments = rows.ToImmutable();
        var selected = CoverAesthetics.Choose(assessments, work.CoverUrl)?.Candidate.Cover.Url;
        // If no image could be read, keep a validated candidate rather than
        // destroying a useful offline cover. The incomplete result is retryable.
        selected ??= candidates.FirstOrDefault(c => c.Cover.Url == work.CoverUrl)?.Cover.Url ?? candidates.FirstOrDefault()?.Cover.Url;
        work = work with { CoverUrl = selected };
        var current = CachedCatalogWork.From(work);
        return entry with { Current = current, Signature = Signature(current, entry.Reference), CoverSchema = complete ? CoverSchemaVersion : 0,
            CoverDecision = new(CoverAesthetics.Algorithm, input, assessments, selected, complete) };
    }
}
