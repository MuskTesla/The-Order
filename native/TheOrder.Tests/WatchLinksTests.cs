using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class WatchLinksTests
{
    private static readonly AnimeWatchIdentity Clannad = new(2167, "51"), After = new(4181, "876");
    private static WatchLink Link(string url, string source = "AniList") => new(Clannad, "Platform", url, WatchLinkKind.Playback, [], WatchLinkVerification.ProviderListed, [new(source, "https://anilist.co/anime/2167", "STREAMING")]);
    private static readonly BangumiWatchDataIndex Index = new(ReadBuiltIn());
    private static BangumiWatchSnapshot ReadBuiltIn() { using var http = new HttpClient(); return new BangumiWatchDataProvider(http).BuiltInAsync(default).GetAwaiter().GetResult(); }
    private static WatchLink[] Catalog(AnimeWatchIdentity identity) => Index.Find(new(identity)).Links.Where(x => WatchLinks.PlatformKey(x) == "bilibili").ToArray();
    [Fact] public void SourcesAreIndependentOfAniListAndKnownSeasonsDoNotCrossBind()
    {
        Assert.Equal("https://www.bilibili.com/bangumi/play/ss1177", Assert.Single(Catalog(Clannad)).Url);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ss1178", Assert.Single(Catalog(After)).Url);
        Assert.Single(Catalog(new(null, "51"))); Assert.Single(Catalog(new(4181, null)));
        Assert.Empty(Catalog(new(4181, "51"))); Assert.Empty(Catalog(new(int.MaxValue, null)));
        Assert.Empty(Catalog(new(null, "999999999"))); Assert.Empty(WatchLinks.Merge(Clannad, Catalog(After)));
    }
    [Fact] public void CatalogDoesNotPretendWikidataSuppliedBilibiliIdOrCurrentPlaybackRights()
    {
        var link = Assert.Single(Catalog(After)); Assert.Equal(new[] { "CN" }, link.Regions); Assert.Equal(WatchLinkVerification.ProviderListed, link.Verification);
        Assert.DoesNotContain(link.Sources, x => x.Source == "Wikidata"); Assert.All(link.Sources, x => Assert.NotNull(x.DataVersion));
        Assert.Contains(link.Sources, x => x.Note.Contains("未核验", StringComparison.Ordinal));
    }
    [Fact] public void MatchingPlaybackUrlsMergeAndRetainBothSources()
    {
        var a = Link("https://www.bilibili.com/bangumi/play/ss1177?spm_id_from=x"); var b = Catalog(Clannad)[0];
        var merged = Assert.Single(WatchLinks.Merge(Clannad, [a, b])); Assert.Equal(4, merged.Sources.Length); Assert.Equal(WatchLinkVerification.ProviderListed, merged.Verification);
        Assert.Equal(new[] { "CN" }, merged.Regions);
    }
    [Fact] public void DifferentEpisodesSeasonsAndUnknownParametersRemainDistinct()
    {
        Assert.Equal(3, WatchLinks.Merge(Clannad, [Link("https://www.bilibili.com/bangumi/play/ss1177"), Link("https://www.bilibili.com/bangumi/play/ss1178"), Link("https://www.bilibili.com/bangumi/play/ep1")]).Length);
        Assert.Equal(2, WatchLinks.Merge(Clannad, [Link("https://www.example.com/play?id=1"), Link("https://www.example.com/play?id=2")]).Length);
    }
    [Theory][InlineData("https://www.bilibili.com/bangumi/media/md1177",WatchLinkKind.Information)]
    [InlineData("https://www.bilibili.com/video/BV1anything",WatchLinkKind.Other)]
    [InlineData("https://www.bilibili.com/bangumi/play/ss1177",WatchLinkKind.Playback)]
    public void InformationAndUploadedVideosAreNotLicensedEpisodeEntrypoints(string url, WatchLinkKind kind) => Assert.Equal(kind, WatchLinks.Classify(url, WatchLinkKind.Playback));
    [Fact] public void TrailerStoreAndPlaybackStaySeparateEvenForIdenticalUrl()
    {
        var link = Link("https://www.example.com/title"); Assert.Equal(3, WatchLinks.Merge(Clannad, [link, link with { Kind = WatchLinkKind.Trailer }, link with { Kind = WatchLinkKind.Store }]).Length);
    }
    [Fact] public void UserPreferredOrderingDoesNotDiscardLinks()
    {
        var others = new[] { Link("https://www.hidive.com/tv/clannad/") with { Platform = "HIDIVE" }, Link("https://www.amazon.com/Clannad/dp/B005IGW5Y0") with { Platform = "Amazon" } };
        var all = others.Concat(Catalog(Clannad));
        Assert.Equal("bilibili", WatchLinks.PlatformKey(WatchLinks.Merge(Clannad, all, WatchLinkSort.BilibiliFirst)[0]));
        Assert.Equal("HIDIVE", WatchLinks.Merge(Clannad, all, WatchLinkSort.SourceOrder)[0].Platform);
        Assert.Equal(all.OrderBy(x => x.Platform, StringComparer.CurrentCulture).Select(x => x.Platform), WatchLinks.Merge(Clannad, all, WatchLinkSort.PlatformName).Select(x => x.Platform)); Assert.Equal(3, WatchLinks.Merge(Clannad, all).Length);
    }
    [Fact] public void LanguageNeverBecomesWorldwideOrAssumedCountryAvailability()
    {
        var detail = AniListTestData.Details() with { WatchLinks = [new(1, "HIDIVE", "https://www.hidive.com/tv/clannad/", "English")] };
        var link = Assert.Single(AniListWatchLinksAdapter.From(Clannad, detail)); Assert.Empty(link.Regions); Assert.Equal(WatchLinkVerification.ProviderListed, link.Verification);
        Assert.Empty(AniListWatchLinksAdapter.From(After, detail));
    }
    [Fact] public void InvalidIdentityRegionAndExecutableUrlsAreRejected()
    {
        var link = Link("https://www.example.com/title");
        Assert.Empty(WatchLinks.Merge(Clannad, [link with { Identity = new(4181, "876") }, link with { Regions = ["Worldwide"] }, link with { Url = "javascript:bad()" }]));
        Assert.Empty(WatchLinks.Merge(new(null, null), [link]));
    }
    [Fact] public async Task PreferencePersistsWithoutChangingRequiredProfileFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-watch-pref-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profile = NativeProfileStore.Open(new(NativeProfileKind.Test, root), new NativeLibraryCodec());
            var library = profile.ReadComponent("library.json"); var ui = profile.ReadComponent("native-ui.json"); var manifest = File.ReadAllBytes(Path.Combine(root, "native-profile.json"));
            await using (var store = new WatchLinkPreferencesStore(profile.Configuration())) { Assert.Equal(WatchLinkSort.BilibiliFirst, (await store.LoadAsync()).Sort); await store.SaveAsync(WatchLinkSort.SourceOrder); }
            await using var reopened = new WatchLinkPreferencesStore(profile.Configuration()); Assert.Equal(WatchLinkSort.SourceOrder, (await reopened.LoadAsync()).Sort);
            Assert.Equal(library, profile.ReadComponent("library.json")); Assert.Equal(ui, profile.ReadComponent("native-ui.json")); Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(root, "native-profile.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ForeignAndUnknownPreferenceFilesArePreservedAndReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "the-order-watch-foreign-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = NativeProfileStore.Open(new(NativeProfileKind.Test, Path.Combine(root, "a")), new NativeLibraryCodec());
            using var b = NativeProfileStore.Open(new(NativeProfileKind.Test, Path.Combine(root, "b")), new NativeLibraryCodec());
            await using var first = new WatchLinkPreferencesStore(a.Configuration()); await first.SaveAsync(WatchLinkSort.SourceOrder);
            var target = Path.Combine(b.Root, WatchLinkPreferencesStore.FileName); File.Copy(Path.Combine(a.Root, WatchLinkPreferencesStore.FileName), target);
            await using var second = new WatchLinkPreferencesStore(b.Configuration()); var bytes = File.ReadAllBytes(target);
            Assert.True((await second.LoadAsync()).ReadOnly); await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(WatchLinkSort.BilibiliFirst)); Assert.Equal(bytes, File.ReadAllBytes(target));
            var json = JsonSerializer.SerializeToNode(new { Version = 2, ProfileId = b.Manifest.ProfileId, StoreId = b.Manifest.StoreId, Sort = 0 })!.ToJsonString(); File.WriteAllText(target, json);
            Assert.True((await second.LoadAsync()).ReadOnly); await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(WatchLinkSort.SourceOrder)); Assert.Equal(json, File.ReadAllText(target));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
