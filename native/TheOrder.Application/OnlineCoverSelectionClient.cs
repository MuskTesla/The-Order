using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record OnlineCoverOptions(CoverEditToken Token, WorkMetadata Work, OnlineAuthSnapshot Auth)
{
    public CoverRecommendation? Recommendation { get; init; }
}

// Reuses the catalog/auth/transaction contracts. The dialog owns no secret or persistent handle.
public sealed class OnlineCoverSelectionClient(OfflineLibraryClient offline, IOnlineCatalog catalog, OnlineAuthContext auth, ICoverRecommendationSource? recommendations = null)
{
    public async Task<OnlineCoverOptions> LoadAsync(GameDetails details, CancellationToken cancellation)
    {
        if (details.CoverToken is not { WorkId: not null } token || details.WorkId != token.WorkId)
            throw new InvalidDataException(UiText.Get("请先关联作品资料，再选择作品封面。"));
        var stamp = await auth.CaptureAsync(cancellation).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stamp.Changed);
        var work = await catalog.DetailsAsync(token.WorkId, true, stop.Token).ConfigureAwait(false);
        if (work.Id != token.WorkId) throw new StaleLibraryRequestException();
        var recommendation = recommendations is null ? new CoverRecommendation(work.CoverUrl, null, true) :
            await recommendations.RecommendationAsync(work.Id, stop.Token).ConfigureAwait(false);
        return auth.Accept(stamp, () => { stop.Token.ThrowIfCancellationRequested(); return new OnlineCoverOptions(token, work, stamp) { Recommendation = recommendation }; });
    }

    public async Task<ClientResult> SaveAsync(OnlineCoverOptions options, string? coverId, CancellationToken cancellation)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, options.Auth.Changed);
            auth.Accept(options.Auth, () => true);
            var work = await catalog.DetailsAsync(options.Work.Id, true, stop.Token).ConfigureAwait(false);
            if (work.Id != options.Work.Id) throw new StaleLibraryRequestException();
            if (coverId is null)
            {
                if (work.CoverUrl != options.Work.CoverUrl) throw new StaleLibraryRequestException();
                if (recommendations is not null)
                {
                    var current = await recommendations.RecommendationAsync(work.Id, stop.Token).ConfigureAwait(false);
                    if (!current.Ready || options.Recommendation is not { Ready: true } shown || current != shown)
                        throw new StaleLibraryRequestException();
                }
            }
            else
            {
                var before = options.Work.Covers?.FirstOrDefault(c => c.Id == coverId);
                var after = work.Covers?.FirstOrDefault(c => c.Id == coverId);
                // An ID must still belong to this work, with the same image and permission.
                if (before is null || after is null || before.Url != after.Url || before.Sexual != after.Sexual)
                    throw new StaleLibraryRequestException();
            }
            return OfflineLibraryClient.Present(await offline.Required().SaveRemoteCoverAsync(options.Token, work, coverId,
                stop.Token, () => auth.Accept(options.Auth, () => true)).ConfigureAwait(false));
        }
        catch (OnlineException e) { return new(ClientOutcome.ValidationFailure, e.Message); }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
    }
}
