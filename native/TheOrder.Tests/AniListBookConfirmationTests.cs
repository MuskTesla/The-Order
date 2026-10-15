using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class AniListBookConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static AniListSource Source() => new("43194", AniListMediaKind.Manga, "缘之空", "ヨスガノソラ", "2010-05-26", "漫画", null, [])
        { BookLevel = BookEntityLevel.Series };
    private static AniListMedia Media() => new(45821, null, AniListMediaKind.Manga, "Yosuga no Sora", "ヨスガノソラ", "", "MANGA", "FINISHED", null,
        "2009-08-26", "2010-11-26", null, 14, 2, []);
    private static AniListDetails Details() => new(Media(), "合成简介", [], [], [], [], [], [], 1, false, false, Now);
    private static AniListFakeProvider Provider() => new() { Results = [Media()], Fetch = (_, _, _) => Task.FromResult(Details()) };
    private static BangumiRelationItem Item() => new("43194", BangumiRelationKind.Book, "缘之空", "ヨスガノソラ", "书籍关联", null);
    private static AniListBridgeReader Bridge() => new()
    {
        Load = (_, _) => Task.FromResult(new BangumiSubjectDetailsResult(new("43194", BangumiRelationKind.Book, "缘之空", "ヨスガノソラ", null,
            "合成简介", "2010-05-26", "漫画", null, null, null, Now)
            { BookFacts = new(true, 2, 14, [], [], [], [], [], [], null) }, false, false, null))
    };

    [Theory]
    [InlineData("漫画", "MANGA")]
    [InlineData("轻小说", "NOVEL")]
    public void BookDateDifferenceWarnsWithoutUsingAnimePremiereAsIdentity(string platform, string format)
    {
        var source = Source() with { Platform = platform }; var media = Media() with { Format = format };
        var assessment = AniListMatching.Assess(source, media);
        Assert.False(assessment.Conflict);
        Assert.Contains(assessment.Evidence, x => x.Contains("2010-05-26") && x.Contains("2009-08-26") && x.Contains("出版与连载"));
        Assert.False(AniListMatching.StrongEvidence(source, media)); Assert.Null(AniListMatching.AutomaticCandidate(source, [media]));
    }
    [Fact]
    public async Task SeriesWithDifferentPublicationYearCanBePreviewedConfirmedAndReopened()
    {
        var disk = new AniListMemoryCache(); var provider = Provider(); var reader = new AniListService(provider, disk);
        using (var vm = new AniListDetailsViewModel(Bridge(), reader))
        {
            await vm.OpenAsync(Item()); Assert.Equal(AniListDetailState.Choosing, vm.State); Assert.False(vm.CanConfirm);
            Assert.DoesNotContain(disk.Values.Keys, x => x.StartsWith("binding:"));
            await vm.PreviewAsync(45821); Assert.True(vm.CanConfirm); Assert.Contains("出版与连载", vm.Message);
            await vm.ConfirmAsync(); Assert.Equal(AniListDetailState.Ready, vm.State); Assert.False(vm.HasUnconfirmedPreview);
            Assert.Equal("45821", (await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(vm.Source!), default)).Confirmed!.Target.Id);
            Assert.DoesNotContain(disk.Values.Values, x => x.Binding is not null);
        }
        using var reopened = new AniListDetailsViewModel(Bridge(), new AniListService(provider, disk));
        await reopened.OpenAsync(Item()); Assert.Equal(AniListDetailState.Ready, reopened.State); Assert.Equal(45821, reopened.Media!.Id);
    }
    [Theory]
    [InlineData(BookEntityLevel.Volume)]
    [InlineData(BookEntityLevel.Publication)]
    public async Task DifferentDateDoesNotLetSingleVolumeBindToMultiVolumeSeries(BookEntityLevel level)
    {
        var source = Source() with { BookLevel = level }; var disk = new AniListMemoryCache();
        Assert.True(AniListMatching.Assess(source, Media()).Conflict);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AniListService(Provider(), disk).ConfirmAsync(source, Details(), default));
        Assert.DoesNotContain(disk.Values.Keys, x => x.StartsWith("binding:"));
    }
    [Theory]
    [InlineData("type")]
    [InlineData("format")]
    [InlineData("external-id")]
    public async Task DateWarningCannotOverrideMediaFormatOrExplicitIdentityConflict(string conflict)
    {
        var source = conflict == "external-id" ? Source() with { ExternalLinks = ["https://anilist.co/manga/99999"] } : Source();
        var media = conflict switch { "type" => Media() with { Kind = AniListMediaKind.Anime }, "format" => Media() with { Format = "NOVEL" }, _ => Media() };
        var disk = new AniListMemoryCache();
        Assert.True(AniListMatching.Assess(source, media).Conflict);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AniListService(Provider(), disk).ConfirmAsync(source, Details() with { Media = media }, default));
        Assert.DoesNotContain(disk.Values.Keys, x => x.StartsWith("binding:"));
    }
    [Fact]
    public async Task ExplicitBookIdentityStillWorksWithoutDateHeuristic()
    {
        var source = Source() with { ExternalLinks = ["https://anilist.co/manga/45821"] };
        var match = await new AniListService(Provider()).MatchAsync(source, null, false, default);
        Assert.Equal(45821, match.Binding?.MediaId); Assert.Equal("Bangumi 明确外部编号", match.Binding!.Evidence);
    }
    [Fact]
    public async Task AChangedBookPublicationDateDoesNotEraseTheStableUserConfirmation()
    {
        var disk = new AniListMemoryCache(); var service = new AniListService(Provider(), disk);
        await service.ConfirmAsync(Source(), Details(), default);
        Assert.NotNull((await service.MatchAsync(Source(), null, false, default)).Binding);
        Assert.NotNull((await service.MatchAsync(Source() with { Date = "2010-06-01" }, null, false, default)).Binding);
        Assert.NotNull((await disk.IdentityDecisions.ReadAsync(MediaIdentityContext.From(Source()), default)).Confirmed);
        Assert.DoesNotContain(disk.Values.Values, x => x.Binding is not null);
    }
}
