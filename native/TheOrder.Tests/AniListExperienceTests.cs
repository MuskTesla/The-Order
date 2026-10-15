using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class AniListExperienceTests
{
    private static AniListSource StrongSource() => AniListTestData.Source() with { Episodes = 23 };
    [Fact] public async Task UniqueCompositeEvidenceLoadsFullDetailsAndPersistsAutomaticProof()
    {
        var provider = new AniListFakeProvider(); var disk = new AniListMemoryCache(); var source = StrongSource();
        var match = await new AniListService(provider, disk).MatchAsync(source, null, false, default);
        Assert.Equal(2167, match.Binding?.MediaId); Assert.StartsWith("自动关联", match.Binding!.Evidence);
        Assert.Equal(1, provider.Fetches);
        var restored = await new AniListService(provider, disk).MatchAsync(source, null, false, default);
        Assert.Equal(match.Binding, restored.Binding); Assert.Equal(1, provider.Searches);
    }
    [Theory] [InlineData(null, "TV")][InlineData("2007", "TV")][InlineData("2007-10", "TV")][InlineData("2007-10-04", null)]
    public void MissingDateOrFormatCannotBeReplacedByExactTitle(string? date, string? format)
        => Assert.Null(AniListMatching.AutomaticCandidate(StrongSource() with { Date = date, Platform = format }, [AniListTestData.Media()]));
    [Theory] [InlineData("2007-11-05", "TV", 23)][InlineData("2007-10-05", "MOVIE", 23)][InlineData("2007-10-05", "TV", 22)]
    public void SameYearOrConflictingEpisodesAreNotAutomaticIdentity(string date, string format, int episodes)
        => Assert.False(AniListMatching.StrongEvidence(StrongSource(), AniListTestData.Media() with { StartDate = date, Format = format, Episodes = episodes }));
    [Fact] public void SameNameWithMultipleSupportedIdentitiesAbstains()
    {
        var first = AniListTestData.Media();
        Assert.Null(AniListMatching.AutomaticCandidate(StrongSource(), [first, first with { Id = 4181 }]));
    }
    [Fact] public void OneTvEpisodeDifferenceRequiresTheExactSamePremiereDay()
    {
        var media = AniListTestData.Media(); var source = StrongSource() with { Date = media.StartDate, Episodes = 24 };
        Assert.True(AniListMatching.StrongEvidence(source, media));
        Assert.False(AniListMatching.StrongEvidence(source with { Date = "2007-10-04" }, media));
        Assert.False(AniListMatching.StrongEvidence(source with { Episodes = 25 }, media));
        Assert.False(AniListMatching.StrongEvidence(source with { Platform = "OVA" }, media with { Format = "OVA" }));
    }
    [Fact] public void ASequelWithDifferentDateAndTitleDoesNotBlockTheSupportedOriginal()
    {
        var first = AniListTestData.Media();
        var second = first with { Id = 4181, Title = "Clannad After Story", NativeTitle = "クラナド アフターストーリー", EnglishTitle = "After Story", Synonyms = [], StartDate = "2008-10-03" };
        Assert.Equal(first, AniListMatching.AutomaticCandidate(StrongSource(), [first, second]));
    }
    [Fact] public async Task ChangedFullDetailsPreventSavingSearchResultAsIdentity()
    {
        var disk = new AniListMemoryCache();
        var provider = new AniListFakeProvider { Fetch = (_, _, _) => Task.FromResult(AniListTestData.Details() with { Media = AniListTestData.Media() with { StartDate = "2008-10-05" } }) };
        var match = await new AniListService(provider, disk).MatchAsync(StrongSource(), null, false, default);
        Assert.Null(match.Binding); Assert.DoesNotContain(disk.Values.Keys, x => x.StartsWith("binding:"));
    }
    [Fact] public async Task StorageFailureCannotClaimAnAutomaticAssociation()
    {
        var match = await new AniListService(new AniListFakeProvider(), new AniListMemoryCache { Refuse = true }).MatchAsync(StrongSource(), null, false, default);
        Assert.Null(match.Binding); Assert.Single(match.Candidates); Assert.NotNull(match.Notice);
    }
    [Fact] public async Task ExplicitAlternativeSearchNeverAutomaticallySelectsOrReplacesTheBinding()
    {
        var disk = new AniListMemoryCache(); var service = new AniListService(new AniListFakeProvider(), disk); var source = StrongSource();
        await service.ConfirmAsync(source, AniListTestData.Details(), default);
        var before = await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(source),default);
        var match = await service.MatchAsync(source, source.Title, false, default);
        Assert.Null(match.Binding); Assert.Single(match.Candidates);
        var after = await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(source),default);
        Assert.Equal(before.Revision,after.Revision); Assert.Equal(before.Confirmed,after.Confirmed);
    }
    [Fact] public async Task CandidateRefreshBypassesBindingWithoutReplacingNativeTitleSearchByTranslatedDisplayTitle()
    {
        var disk = new AniListMemoryCache(); var provider = new AniListFakeProvider(); var service = new AniListService(provider, disk);
        var source = StrongSource() with { Title = "中文标题" };
        await service.ConfirmAsync(source, AniListTestData.Details(), default);
        var candidates = await service.FindCandidatesAsync(source, false, default);
        Assert.Null(candidates.Binding); Assert.NotEmpty(candidates.Candidates);
        Assert.Equal(2167, (await service.MatchAsync(source, null, false, default)).Binding?.MediaId);
    }
    [Fact] public async Task AManualChoiceWinsOverLateAutomaticDetails()
    {
        var late = new TaskCompletionSource<AniListDetails>();
        var provider = new AniListFakeProvider { Fetch = (_, _, _) => late.Task }; var service = new AniListService(provider); var source = StrongSource();
        var automatic = service.MatchAsync(source, null, false, default);
        await service.ConfirmAsync(source, AniListTestData.Details(4181), default);
        late.SetResult(AniListTestData.Details());
        Assert.Equal(4181, (await automatic).Binding?.MediaId);
        Assert.Equal(4181, (await service.MatchAsync(source, null, false, default)).Binding?.MediaId);
    }
    [Fact] public async Task CancelledAutomaticDetailReadNeverCreatesABinding()
    {
        var late = new TaskCompletionSource<AniListDetails>(); using var stop = new CancellationTokenSource(); var disk = new AniListMemoryCache();
        var service = new AniListService(new AniListFakeProvider { Fetch = (_, _, _) => late.Task }, disk);
        var pending = service.MatchAsync(StrongSource(), null, false, stop.Token); stop.Cancel(); late.SetResult(AniListTestData.Details());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.DoesNotContain(disk.Values.Keys, x => x.StartsWith("binding:"));
    }
    [Fact] public async Task ReopenedConfirmedWorkShowsAlternativesAndKeepsOriginalUntilConfirmation()
    {
        var disk = new AniListMemoryCache(); var provider = new AniListFakeProvider(); var reader = new AniListService(provider, disk);
        using (var original = new AniListDetailsViewModel(new AniListBridgeReader(), reader))
        {
            await original.OpenAsync(Item()); await original.PreviewAsync(2167); await original.ConfirmAsync();
        }
        provider.Results = [AniListTestData.Media(), AniListTestData.Media() with { Id = 4181 }];
        using var reopened = new AniListDetailsViewModel(new AniListBridgeReader(), reader);
        await reopened.OpenAsync(Item()); Assert.Empty(reopened.Candidates);
        await reopened.ChooseAgainAsync(); Assert.Equal(AniListDetailState.Choosing, reopened.State); Assert.NotEmpty(reopened.Candidates);
        var binding = (await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(reopened.Source!), default)).Confirmed;
        Assert.Equal("2167", binding!.Target.Id); Assert.Null(reopened.Displayed);
    }
    private static BangumiRelationItem Item() => new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD -クラナド-", "动画", null);
}
