using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class LibraryBackupStoreTests
{
    private static readonly NativeLibraryCodec Codec = new();
    private static byte[] Document(string notes = "note") => Codec.Encode([Entry() with { Notes = notes }], Clock);
    private static WindowsLibraryBackupStore Store(NativeProfileStore profile, TimeProvider? clock = null, Func<PersistenceStage, Task>? probe = null)
        => new(profile.Configuration(false), Codec, clock, probe);
    private static string DirectoryPath(ProfileFixture fixture) => Path.Combine(fixture.Root, WindowsLibraryBackupStore.DirectoryName);
    private static string SnapshotPath(ProfileFixture fixture, string id) => Path.Combine(DirectoryPath(fixture), id + ".snapshot.json");
    private sealed class ClockProvider : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.Parse(Clock);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    [Fact]
    public async Task ExternalBackupIsPortableLibraryOnlyAndCannotOverwriteProfile()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var store = Store(profile);
        var path = Path.Combine(fixture.Parent, "portable.json"); var bytes = Document();
        File.WriteAllText(Path.Combine(fixture.Root, "synthetic-secret.txt"), "never-export-this");
        await store.WriteExternalAsync(path, bytes, default);
        Assert.Equal(bytes, await store.ReadExternalAsync(path, default));
        Assert.DoesNotContain("never-export-this", File.ReadAllText(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteExternalAsync(Path.Combine(fixture.Root, "library.json"), bytes, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteExternalAsync(Path.Combine(fixture.Parent, "library.exe"), bytes, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadExternalAsync("relative.json", default));
        Assert.Equal("never-export-this", File.ReadAllText(Path.Combine(fixture.Root, "synthetic-secret.txt")));
    }
    [Fact]
    public async Task RotationKeepsTenRecentVerifiedSnapshotsAndNeverDeletesUnownedFiles()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var clock = new ClockProvider(); var store = Store(profile, clock);
        string? first = null, newest = null;
        for (var i = 0; i < 14; i++)
        {
            clock.Utc = clock.Utc.AddHours(1); var result = await store.SaveSnapshotAsync(Document("note-" + i), 1, i == 12 ? "恢复前" : "自动", default);
            first ??= result.Record.Id; newest = result.Record.Id;
            File.WriteAllText(Path.Combine(DirectoryPath(fixture), "unowned.txt"), "preserve");
        }
        var state = await store.ReadStateAsync(default);
        Assert.Equal(10, state.Records.Length); Assert.Equal(newest, state.Records[0].Id); Assert.DoesNotContain(state.Records, r => r.Id == first);
        Assert.Equal("note-13", Codec.Decode(await store.ReadSnapshotAsync(newest!, default))[0].Notes);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(DirectoryPath(fixture), "unowned.txt")));
        Assert.Contains(state.Records, r => r.Reason == "恢复前");
    }
    [Theory]
    [InlineData("Version")]
    [InlineData("ProfileId")]
    [InlineData("Checksum")]
    [InlineData("Document")]
    [InlineData("FutureField")]
    public async Task CorruptOrForeignSnapshotStopsMaintenanceWithoutOverwritingIt(string field)
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var store = Store(profile);
        var saved = await store.SaveSnapshotAsync(Document(), 1, "自动", default); var path = SnapshotPath(fixture, saved.Record.Id);
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (field)
        {
            case "Version": json[field] = 2; break;
            case "ProfileId": json[field] = Guid.NewGuid().ToString(); break;
            case "Document": json[field] = null; break;
            default: json[field] = "invalid"; break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json); File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadStateAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSnapshotAsync(Document("new"), 1, "自动", default));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(DirectoryPath(fixture), "*.snapshot.json"));
    }
    [Fact]
    public async Task UnsupportedPreferencesAndUnknownSnapshotNameAreNeverSilentlyReplaced()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var store = Store(profile);
        await store.SaveSettingsAsync(new(false), default); var path = Path.Combine(DirectoryPath(fixture), "settings.json");
        var json = JsonNode.Parse(File.ReadAllBytes(path))!; json["Version"] = 2; var bytes = JsonSerializer.SerializeToUtf8Bytes(json); File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSettingsAsync(new(true), default));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.Delete(path); File.WriteAllText(Path.Combine(DirectoryPath(fixture), "foreign.snapshot.json"), "preserve");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSnapshotAsync(Document(), 1, "自动", default));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(DirectoryPath(fixture), "foreign.snapshot.json")));
    }
    [Fact]
    public async Task FailedAtomicWritePreservesOldExportAndEarlierCheckpoint()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); bool fail = false;
        var store = Store(profile, probe: stage => stage == PersistenceStage.BeforeReplace && fail ? Task.FromException(new IOException("synthetic replace failure")) : Task.CompletedTask);
        var old = await store.SaveSnapshotAsync(Document(), 1, "自动", default); var path = Path.Combine(fixture.Parent, "export.json");
        await store.WriteExternalAsync(path, Document(), default); var original = File.ReadAllBytes(path); fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.WriteExternalAsync(path, Document("new"), default));
        await Assert.ThrowsAsync<IOException>(() => store.SaveSnapshotAsync(Document("new"), 1, "自动", default));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(old.Record.Id, Assert.Single((await store.ReadStateAsync(default)).Records).Id);
    }
    [Fact]
    public async Task OversizedAndCancelledReadsOrWritesDoNotCreatePartialExports()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var store = Store(profile);
        var path = Path.Combine(fixture.Parent, "large.json");
        using (var file = File.Create(path)) file.SetLength(WindowsLibraryBackupStore.MaximumDocumentBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadExternalAsync(path, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var output = Path.Combine(fixture.Parent, "cancelled.json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteExternalAsync(output, Document(), cancellation.Token));
        Assert.False(File.Exists(output));
    }
    [Fact]
    public async Task AutomaticBackupsRespectOptOutIntervalAndCanonicalContentThenSaveBeforeRestore()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open(); var factory = new Factory(profile);
        await using var client = new OfflineLibraryClient(factory, new LegacyClientTextRules()); await client.LoadAsync(default);
        var clock = new ClockProvider(); var store = Store(profile, clock); var service = new LibraryBackupService(client, store, Codec, clock);
        Assert.Null(await service.AutomaticAsync(false, default)); // Fresh empty bookshelf.
        await factory.Library!.ImportAsync(Document("original"));
        var first = await service.AutomaticAsync(false, default); Assert.NotNull(first);
        clock.Utc = clock.Utc.AddMinutes(1); await factory.Library.UpdateAsync("a", new() { Notes = "later" });
        Assert.Null(await service.AutomaticAsync(false, default));
        var last = await service.AutomaticAsync(true, default); Assert.NotNull(last); Assert.Equal("later", Codec.Decode(await store.ReadSnapshotAsync(last!.Record.Id, default))[0].Notes);
        clock.Utc = clock.Utc.AddHours(2); Assert.Null(await service.AutomaticAsync(false, default));
        await service.SaveSettingsAsync(false, default); await factory.Library.UpdateAsync("a", new() { Notes = "final" });
        Assert.Null(await service.AutomaticAsync(true, default));
        var preview = await service.PreviewAsync(first!.Record.Id, true, LibraryRestoreMode.ReplaceLibrary, default);
        Assert.True((await service.RestoreAsync(preview, default)).IsCommitted);
        Assert.Equal("original", client.CurrentSnapshot!.Entries[0].Notes);
        var before = (await store.ReadStateAsync(default)).Records.Single(r => r.Reason == "恢复前");
        Assert.Equal("final", Codec.Decode(await store.ReadSnapshotAsync(before.Id, default))[0].Notes);
        Assert.False((await store.ReadStateAsync(default)).Settings.Automatic);
    }
    [Fact]
    public void DigestIgnoresExportTimestampButIncludesPersonalNotesAndEmbeddedCover()
    {
        var entry = Entry(); var a = Codec.Encode([entry], Clock); var b = Codec.Encode([entry], "2026-10-12T00:00:00.000Z");
        Assert.Equal(LibraryBackupService.ContentDigest(Codec, a), LibraryBackupService.ContentDigest(Codec, b));
        Assert.NotEqual(LibraryBackupService.ContentDigest(Codec, a), LibraryBackupService.ContentDigest(Codec, Document("changed")));
    }
    private sealed class Factory(NativeProfileStore profile) : ITransactionalLibraryFactory
    {
        public TransactionalLibrary? Library;
        public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Library = TransactionalLibrary.Open(Codec, profile.CreateLibraryPersistence()); return Task.FromResult(Library); }
    }
}
