using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class P1MetadataMergeTests
{
    private static readonly NativeLibraryCodec Codec = new();
    private static WorkMetadata Catalog(string id, string? bangumi = null) => Work(id) with
    { Description = "catalog " + id, References = bangumi is null ? [] : [new("bangumi", bangumi, "https://bgm.tv/subject/" + bangumi)] };
    private static MetadataSourceLayer Layer() => new("bangumi", "42",
        Catalog("bangumi:42", "42") with { Description = "source A description" }, ["description"], false);
    private static LibraryEntry WithLayer(string id, WorkMetadata? basis)
    {
        var mix = new MetadataMix(basis, [Layer()]);
        return Entry() with { Id = id, Metadata = MetadataMixing.Compose(mix), MetadataMix = mix, Notes = "notes " + id,
            TotalPlaySeconds = 60, ManualPlaytime = [new(Guid.NewGuid().ToString("D"), "2026-10-02", 30, "manual " + id, Clock)],
            GuidePaths = [@"C:\synthetic\" + id + ".txt"] };
    }
    private static SessionMemoryDisk Disk(params LibraryEntry[] entries) => new() { Main = Codec.Encode([.. entries], Clock) };
    private static TransactionalLibrary Open(SessionMemoryDisk disk) => TransactionalLibrary.Open(Codec, new SessionMemoryPersistence(disk));

    [Theory][InlineData("43")][InlineData(null)]
    public async Task DifferentWorkLayerCannotBeTransplantedDuringMerge(string? newReference)
    {
        var source = WithLayer("a", Catalog("100", "42"));
        var target = Entry() with { Id = "b", Metadata = Catalog("200", newReference), Notes = "keep target notes", TotalPlaySeconds = 120 };
        var disk = Disk(source, target); var originalBytes = disk.Main!.ToArray(); await using var library = Open(disk);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => library.LinkAsync("a", target.Metadata!));
        Assert.Contains("撤回", error.Message); Assert.Equal(0, disk.Writes); Assert.Equal(originalBytes, disk.Main);
        Assert.Equal(2, library.Snapshot.Entries.Length);
        Assert.Equal("notes a", library.Snapshot.Entries.Single(e => e.Id == "a").Notes);
        Assert.Equal(90, library.Snapshot.Entries.Single(e => e.Id == "a").EffectivePlaySeconds);
        Assert.Equal("keep target notes", library.Snapshot.Entries.Single(e => e.Id == "b").Notes);
        Assert.Single(library.Snapshot.Entries.Single(e => e.Id == "a").MetadataMix!.Layers);
    }

    [Theory][InlineData("42")][InlineData(null)]
    public async Task SameCatalogWorkMergePreservesLayerAndBothPersonalRecords(string? reference)
    {
        var source = WithLayer("a", Catalog("200", "42"));
        var target = Entry() with { Id = "b", Metadata = Catalog("200", reference), Notes = "target notes", TotalPlaySeconds = 120,
            GuidePaths = [@"C:\synthetic\target.txt"], Favorite = true };
        var disk = Disk(source, target); await using var library = Open(disk);
        await library.LinkAsync("a", target.Metadata!);
        var merged = Assert.Single(library.Snapshot.Entries);
        Assert.Equal("source A description", merged.Metadata!.Description); Assert.Single(merged.MetadataMix!.Layers);
        Assert.Equal(210, merged.EffectivePlaySeconds); Assert.Equal(2, merged.GuidePaths.Length); Assert.True(merged.Favorite);
        Assert.Contains("notes a", merged.Notes); Assert.Contains("target notes", merged.Notes); Assert.Equal(1, disk.Writes);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactSourceReferenceAllowsDirectOrReassociatedLayerToJoinCatalogWork(bool hasOldBase)
    {
        var source = WithLayer("a", hasOldBase ? Catalog("100", "42") : null);
        var target = Entry() with { Id = "b", Metadata = Catalog("200", "42"), Notes = "target notes" };
        var disk = Disk(source, target); await using var library = Open(disk);
        await library.LinkAsync("a", target.Metadata!);
        var merged = Assert.Single(library.Snapshot.Entries);
        Assert.Equal("200", merged.Metadata!.Id); Assert.Equal("200", merged.MetadataMix!.Base!.Id);
        Assert.Equal("source A description", merged.Metadata.Description); Assert.Equal(90, merged.EffectivePlaySeconds);
        Assert.Contains("target notes", merged.Notes); Assert.Equal(1, disk.Writes);
    }

    [Fact]
    public async Task TargetLayerConflictAlsoRejectsWholeMergeDespiteMatchingCatalogId()
    {
        var source = Entry() with { Notes = "source notes" }; var target = WithLayer("b", Catalog("200", "42"));
        var disk = Disk(source, target); var originalBytes = disk.Main!.ToArray(); await using var library = Open(disk);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.LinkAsync("a", Catalog("200", "43")));
        Assert.Equal(0, disk.Writes); Assert.Equal(originalBytes, disk.Main); Assert.Equal(2, library.Snapshot.Entries.Length);
    }
}
