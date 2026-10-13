using TheOrder.Application;
using TheOrder.Domain;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class P1PersonalSyncTests
{
    private static LibraryEntry Direct(string source, string id)
    {
        var work = Work(source + ":" + id) with { References = [new(source, id, source == "bangumi" ? "https://bgm.tv/subject/" + id : "https://vndb.org/" + id)] };
        return Entry("direct source") with { Id = "direct-entry", Metadata = work,
            MetadataMix = new(null, [new(source, id, work, ["description"], false)]), TotalPlaySeconds = 1200 };
    }
    [Theory][InlineData("bangumi", "42")][InlineData("vndb", "v42")]
    public async Task DirectOnlyIdentityCannotBeUploadedButOtherWorksStillSync(string source, string id)
    {
        var f = new PersonalSyncTests.Fixture(); await f.Login();
        var linked = f.Library.Entries.Single() with { TotalPlaySeconds = 600 };
        var direct = Direct(source, id); f.Library.Entries = [linked, direct];
        var preview = await f.Sync.PreviewAsync(new(Playtime: PersonalSyncDirection.Upload), default);
        var unsupported = preview.Rows.Single(x => x.EntryId == direct.Id);
        Assert.True(unsupported.Conflict); Assert.Null(unsupported.Upload); Assert.Null(unsupported.Import);
        var choices = PersonalSyncCoordinator.DefaultChoices(preview); Assert.Equal(PersonalSyncChoice.Skip, choices[unsupported.Id]);
        var invalid = new Dictionary<string, PersonalSyncChoice> { [unsupported.Id] = PersonalSyncChoice.Upload };
        await Assert.ThrowsAsync<OnlineException>(() => f.Sync.ApplyAsync(preview, invalid, false, null, default)); Assert.Equal(0, f.Cloud.Writes);
        var result = await f.Sync.ApplyAsync(preview, choices, false, null, default);
        Assert.Equal(1, result.Completed); Assert.Equal("200", Assert.Single(f.Cloud.Times).WorkId);
        Assert.Equal(10, f.Cloud.Times.Single().Minutes); Assert.Equal(direct, f.Library.Entries.Single(x => x.Id == direct.Id));
    }
    [Fact] public async Task DirectOnlyEntriesAreExcludedFromFolderMappingsAndCreations()
    {
        var f = new PersonalSyncTests.Fixture(); await f.Login(); f.Library.Entries = f.Library.Entries.Add(Direct("bangumi", "42"));
        await Assert.ThrowsAsync<OnlineException>(() => f.Sync.MapFolderAsync(f.Cloud.Folders.Single(), ["bangumi:42"], default));
        await Assert.ThrowsAsync<OnlineException>(() => f.Sync.CreateMappedFolderAsync("direct", ["bangumi:42"], default));
        Assert.Equal(0, f.Cloud.Creates); Assert.Equal(0, f.Storage.Saves);
        await f.Sync.MapFolderAsync(f.Cloud.Folders.Single(), ["200"], default);
        Assert.Equal("200", Assert.Single((await f.State()).Folders).Members.Single());
    }
    [Fact] public void DirectOverridesDoNotChangeAnExistingNextMoeIdentity()
    {
        var entry = Entry() with { Metadata = Work(), MetadataMix = new(Work(), Direct("vndb", "v42").MetadataMix!.Layers) };
        Assert.True(MetadataMixing.HasNextMoeIdentity(entry));
        Assert.False(MetadataMixing.HasNextMoeIdentity(Direct("vndb", "v42")));
    }
    [Fact] public void MergeCannotSilentlyDiscardConflictingSourceFieldPreferences()
    {
        var first = Direct("bangumi", "42").MetadataMix!; var layer = first.Layers.Single();
        var work = Work() with { References = layer.Work.References };
        Assert.Single(MetadataMixing.Merge(work, first, first)!.Layers);
        Assert.Throws<InvalidDataException>(() => MetadataMixing.Merge(work, first, first with { Layers = [layer with { Fields = ["tags"] }] }));
        Assert.Throws<InvalidDataException>(() => MetadataMixing.Merge(work, first, first with { Layers = [layer with { Work = layer.Work with { Description = "different" } }] }));
    }
}
