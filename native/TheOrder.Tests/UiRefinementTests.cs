using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
namespace TheOrder.Tests;
public sealed class ArtworkSourcesTests
{
    [Fact] public void BannerBelongsToWorkAndDoesNotChangePersonalPoster()
    {
        var poster = new WorkCover("poster", "https://example.org/poster.jpg", 900, 1200, 10000, null);
        var work = Work() with { BannerUrl = "https://example.org/banner.jpg", CoverUrl = poster.Url, Covers = [poster] };
        var entry = Entry() with { Metadata = work, CoverChoice = new(work.Id, poster) };
        Assert.Equal(work.BannerUrl, ArtworkSources.Landscape(work));
        Assert.Equal(ImageSourceKeys.Remote(work.BannerUrl, work.FetchedAt), ArtworkSources.Key(entry));
        Assert.Equal(poster.Url, CoverSources.Selected(entry));
    }
    [Fact] public void LandscapeUsesDimensionsRatherThanVotesOrAnotherWork()
    {
        var work = Work() with { Covers = [new("huge-votes", "https://example.org/portrait", 900, 1200, 99999, null), new("wide", "https://example.org/wide", 1600, 900, 1, null), new("short", "https://example.org/short", 500, 281, 99999, null), new("blank", "", 1920, 1080, 0, null)] };
        Assert.Equal("https://example.org/wide", ArtworkSources.Landscape(work));
        Assert.Null(ArtworkSources.Landscape(work with { Covers = [work.Covers!.Value[0]] }));
        var entry = Entry() with { Metadata = work with { Covers = [] } };
        Assert.Equal(ImageSourceKeys.For(entry), ArtworkSources.Key(entry));
    }
}
[Collection("Native profile resource")]
public sealed class RecognitionPreferencesTests
{
    [Theory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task ChoicesRoundTripWithoutChangingExistingPreferences(bool online, bool pe)
    {
        using var f = new ProfileFixture(); byte[] old;
        using (var p = f.Open()) { old = File.ReadAllBytes(Path.Combine(f.Root,"native-ui.json")); await using var store = new RecognitionPreferencesStore(p.Configuration()); await store.SaveAsync(new(online,pe)); }
        using var reopened = f.Open(); await using var next = new RecognitionPreferencesStore(reopened.Configuration());
        Assert.Equal(new RecognitionPreference(online,pe), (await next.LoadAsync()).Preference);
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(f.Root,"native-ui.json")));
    }
    [Theory][InlineData("version")][InlineData("identity")][InlineData("schema")]
    public async Task UnsupportedOrForeignFileIsRetained(string fault)
    {
        using var f = new ProfileFixture(); using var p = f.Open(); await using var store = new RecognitionPreferencesStore(p.Configuration()); await store.SaveAsync(new(false,false));
        var path=Path.Combine(f.Root,RecognitionPreferencesStore.FileName); var json=JsonNode.Parse(File.ReadAllBytes(path))!;
        if(fault=="version") json["Version"]=2; else if(fault=="identity") json["StoreId"]=Guid.NewGuid().ToString(); else json["Future"]=true;
        File.WriteAllText(path,json.ToJsonString()); var before=File.ReadAllBytes(path);
        Assert.True((await store.LoadAsync()).ReadOnly);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new())); Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Fact] public async Task InterruptedCommitPreservesPriorChoice()
    {
        using var f=new ProfileFixture();using var p=f.Open();await using var good=new RecognitionPreferencesStore(p.Configuration());await good.SaveAsync(new(false,false));
        await using var failed=new RecognitionPreferencesStore(p.Configuration(), stage=>stage==PersistenceStage.BeforeReplace?throw new IOException("synthetic"):Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(()=>failed.SaveAsync(new())); Assert.Equal(new RecognitionPreference(false,false),(await good.LoadAsync()).Preference);
    }
}
