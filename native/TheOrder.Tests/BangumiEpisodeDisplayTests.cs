using System.Globalization;
using TheOrder.Application;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiEpisodeDisplayTests
{
    private static BangumiEpisode Row(int id, int type, decimal sort, string subject = "51") =>
        new(id, subject, type, sort, "中文标题", "Original title", "2008-01-01", "24:00", "脚本：某人\n分镜、演出：某人、");
    private sealed class Reader(Func<string, int, BangumiEpisodePage> page) : IBangumiEpisodesReader
    {
        public int Calls;
        public Task<BangumiEpisodesResult> LoadAsync(string id, int offset, bool refresh, CancellationToken cancellation)
        { Calls++; cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new BangumiEpisodesResult(page(id, offset), false, false, null)); }
    }
    private static BangumiEpisodePage Page(string id, int offset, int total, params BangumiEpisode[] rows) =>
        new(id, offset, total, rows, new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AllGroupsUseSourceTypeThenDecimalSortWithoutInventingAViewingTimeline()
    {
        var input = new[] { Row(30, 1, 1), Row(10, 0, 10), Row(3, 0, 2.5m), Row(2, 0, 2), Row(31, 1, .5m) };
        Assert.Equal(new[] { 2, 3, 10, 31, 30 }, BangumiEpisodeDisplay.Project(input, null).Select(x => x.Id));
        Assert.Equal(new[] { 30, 10, 3, 2, 31 }, input.Select(x => x.Id));
        Assert.Equal(new[] { 31, 30 }, BangumiEpisodeDisplay.Project(input, 1).Select(x => x.Id));
    }
    [Fact]
    public void TiedNumbersHaveDeterministicIdOrderButKeepDifferentIdentities()
    {
        var rows = new[] { Row(10, 0, 1), Row(9, 0, 1) };
        Assert.Equal(new[] { 9, 10 }, BangumiEpisodeDisplay.Project(rows, 0).Select(x => x.Id));
    }
    [Fact]
    public void FiltersCountOnlyLoadedRowsAndRetainEveryKnownType()
    {
        var rows = Enumerable.Range(0, 7).Reverse().Select(type => Row(type + 1, type, 1)).ToArray();
        var filters = BangumiEpisodeDisplay.Filters(rows);
        Assert.Null(filters[0].Type); Assert.Equal(7, filters[0].Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(x => (int?)x), filters.Skip(1).Select(x => x.Type));
        Assert.All(filters.Skip(1), x => Assert.Equal(1, x.Count));
        Assert.Equal("特别篇", filters[2].Caption); Assert.Equal("预告 / 宣传", filters[5].Caption);
        Assert.Single(BangumiEpisodeDisplay.Filters([]));
    }
    [Theory] [InlineData(-1)] [InlineData(7)] [InlineData(int.MaxValue)]
    public void UnsupportedTypeRejectsProjection(int type) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BangumiEpisodeDisplay.Project([], type));
    [Fact]
    public void EpisodeSortKeepsSourceFractionAndInvariantPunctuation()
    {
        var saved = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Assert.Equal("23.5", BangumiEpisodeDisplay.Number(Row(1, 1, 23.5m))); }
        finally { CultureInfo.CurrentCulture = saved; }
    }
    [Fact]
    public void EpisodeLinkUsesExactOfficialIdentityInsteadOfTitleOrSubjectNumber()
    {
        var uri = BangumiEpisodeDisplay.EpisodeUri(37338);
        Assert.Equal("https://bgm.tv/ep/37338", uri.AbsoluteUri); Assert.Empty(uri.Query); Assert.Empty(uri.Fragment);
        Assert.Throws<ArgumentOutOfRangeException>(() => BangumiEpisodeDisplay.EpisodeUri(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BangumiEpisodeDisplay.EpisodeUri(-1));
    }
    [Fact]
    public async Task LocalFilteringDoesNotFetchOrChangePaginationAndSourceSwitchResetsIt()
    {
        var reader = new Reader((id, offset) => Page(id, offset, 100, Row(1, 0, 1, id), Row(2, 1, 23, id)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync();
        model.SetType(1); Assert.Equal(1, reader.Calls); Assert.Equal(2, model.Episodes.Length); Assert.Single(model.VisibleEpisodes);
        Assert.Equal(100, model.Total); Assert.Equal(50, model.NextOffset); Assert.True(model.HasUnloadedEpisodes); Assert.True(model.CanMore);
        Assert.Equal(23, model.VisibleEpisodes[0].Sort); Assert.Equal("脚本：某人\n分镜、演出：某人、", model.VisibleEpisodes[0].Summary);
        model.SetSource("876"); Assert.Null(model.SelectedType); Assert.Empty(model.VisibleEpisodes); Assert.False(model.HasUnloadedEpisodes);
    }
    [Fact]
    public async Task MorePagesPreserveFilterAndCannotMistakeUnloadedTypeForAnEmptyDirectory()
    {
        var reader = new Reader((id, offset) => offset == 0 ? Page(id, 0, 51, Row(1, 0, 1, id)) : Page(id, 50, 51, Row(2, 1, 1, id)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync();
        model.SetType(1); Assert.Empty(model.VisibleEpisodes); Assert.Equal(BangumiEpisodesState.Ready, model.State); Assert.True(model.HasUnloadedEpisodes);
        await model.MoreAsync(); Assert.Equal(1, model.SelectedType); Assert.Equal(2, reader.Calls); Assert.Single(model.VisibleEpisodes); Assert.False(model.HasUnloadedEpisodes);
        Assert.False(model.CanMore); Assert.Equal(100, model.NextOffset);
    }
    [Fact]
    public async Task RefreshRetainsSelectionButDoesNotKeepRowsMissingFromNewSourceSnapshot()
    {
        var reads = 0; var reader = new Reader((id, offset) => ++reads == 1 ? Page(id, 0, 1, Row(1, 1, 1, id)) : Page(id, 0, 1, Row(2, 0, 1, id)));
        using var model = new BangumiEpisodesViewModel(reader); model.SetSource("51"); await model.ExpandAsync(); model.SetType(1);
        await model.RefreshAsync(); Assert.Equal(1, model.SelectedType); Assert.Empty(model.VisibleEpisodes); Assert.Equal(2, model.Episodes.Single().Id);
    }
    [Fact]
    public void UnsupportedFilterCannotAlterStateOrUseNetwork()
    {
        var reader = new Reader((id, offset) => Page(id, offset, 0)); using var model = new BangumiEpisodesViewModel(reader);
        model.SetSource("51"); model.SetType(-1); model.SetType(7); Assert.Null(model.SelectedType); Assert.Equal(0, reader.Calls);
        model.Dispose(); model.SetType(1); Assert.Null(model.SelectedType);
    }
}
