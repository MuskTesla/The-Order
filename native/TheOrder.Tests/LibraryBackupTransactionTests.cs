using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class LibraryBackupTransactionTests
{
    private static readonly NativeLibraryCodec Codec = new();
    private static byte[] Document(params LibraryEntry[] entries) => Codec.Encode([.. entries], Clock);
    private static TransactionalLibrary Open(SessionMemoryDisk disk) => TransactionalLibrary.Open(Codec, new SessionMemoryPersistence(disk));
    [Fact]
    public async Task AppendPreviewCountsDuplicatesAndPreservesCurrentPersonalRecord()
    {
        var original = Entry(folder: @"C:\synthetic\first") with { Metadata = Work(), Notes = "current", Name = "personal", NameIsCustom = true };
        var disk = new SessionMemoryDisk { Main = Document(original) }; await using var library = Open(disk);
        var incoming = Document(original with { Notes = "old", Installations = [], PreferredInstallationId = null }, Entry("same work") with { Id = "b", Metadata = Work() },
            Entry("same folder", @"C:\synthetic\FIRST") with { Id = "c" }, Entry("new") with { Id = "d" });
        var preview = await library.PreviewRestoreAsync(incoming, LibraryRestoreMode.AddMissing);
        Assert.Equal(1, preview.Added); Assert.Equal(3, preview.Skipped); Assert.Equal(2, preview.ResultCount);
        byte[]? checkpoint = null;
        await library.RestoreAsync(preview, (bytes, count, _) => { Assert.Equal(1, count); checkpoint = bytes.ToArray(); return Task.CompletedTask; });
        Assert.Equal("current", library.Snapshot.Entries[0].Notes); Assert.Equal("personal", library.Snapshot.Entries[0].Name);
        Assert.Equal("d", library.Snapshot.Entries[1].Id); Assert.Equal("current", Codec.Decode(checkpoint!)[0].Notes); Assert.Equal(1, disk.Writes);
    }
    [Fact]
    public async Task ReplacementRestoresPersonalHistoryAndInvalidatesSameIdRequests()
    {
        var disk = new SessionMemoryDisk { Main = Document(Entry() with { Metadata = Work(), Notes = "new" }) }; await using var library = Open(disk);
        var oldDetails = await library.DetailsAsync("a"); var oldLaunch = await library.CaptureLaunchAsync("a");
        var oldRecognition = await library.CaptureRecognitionAsync("a"); var request = await library.BeginRequestAsync("a");
        var restored = Entry() with { Metadata = Work(), Notes = "backup", Favorite = true, PersonalRating = 8,
            Categories = ["weekend"], CustomTags = ["personal"], Sessions = [new("history", Clock, Clock, 60)], TotalPlaySeconds = 120, PlayCount = 2 };
        var preview = await library.PreviewRestoreAsync(Document(restored), LibraryRestoreMode.ReplaceLibrary);
        await library.RestoreAsync(preview, (_, _, _) => Task.CompletedTask);
        var current = Assert.Single(library.Snapshot.Entries);
        Assert.Equal("backup", current.Notes); Assert.True(current.Favorite); Assert.Equal(8, current.PersonalRating);
        Assert.Equal(120, current.TotalPlaySeconds); Assert.Equal("history", Assert.Single(current.Sessions).Id);
        Assert.NotEqual(oldDetails.Token.Generation, (await library.DetailsAsync("a")).Token.Generation);
        Assert.NotEqual(oldLaunch.Identity, (await library.CaptureLaunchAsync("a")).Identity);
        Assert.False(await library.IsRecognitionCurrentAsync(oldRecognition));
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.CommitLinkAsync("a", Work("201"), request));
        Assert.Equal("200", current.Metadata!.Id);
    }
    [Fact]
    public async Task PreviewDoesNotAuthorizeOverwriteAfterLaterEditOrWriterReopen()
    {
        var disk = new SessionMemoryDisk { Main = Document(Entry()) }; var called = false;
        LibraryRestorePreview preview;
        await using (var library = Open(disk))
        {
            preview = await library.PreviewRestoreAsync(Document(Entry() with { Notes = "backup" }), LibraryRestoreMode.ReplaceLibrary);
            await library.UpdateAsync("a", new() { Notes = "later" });
            await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.RestoreAsync(preview, (_, _, _) => { called = true; return Task.CompletedTask; }));
            Assert.False(called); Assert.Equal("later", library.Snapshot.Entries[0].Notes);
        }
        await using var reopened = Open(disk);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => reopened.RestoreAsync(preview, (_, _, _) => Task.CompletedTask));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackFailureOrCancellationCannotSwitchPrimary(bool cancel)
    {
        var disk = new SessionMemoryDisk { Main = Document(Entry()) }; var original = disk.Main.ToArray();
        await using var library = Open(disk); using var cancellation = new CancellationTokenSource();
        var preview = await library.PreviewRestoreAsync(Document(Entry("replacement")), LibraryRestoreMode.ReplaceLibrary);
        var error = await Record.ExceptionAsync(() => library.RestoreAsync(preview, (_, _, _) =>
        { if (cancel) { cancellation.Cancel(); return Task.CompletedTask; } throw new IOException("checkpoint unavailable"); }, cancellation.Token));
        Assert.IsAssignableFrom<Exception>(error); Assert.Equal(cancel, error is OperationCanceledException);
        Assert.Equal(original, disk.Main); Assert.Equal(0, disk.Writes); Assert.Equal("星空の航路", library.Snapshot.Entries[0].Name);
    }
    [Fact]
    public async Task PrimaryFailureKeepsCurrentMemoryAndVerifiedCheckpoint()
    {
        var disk = new SessionMemoryDisk { Main = Document(Entry()), Fail = true }; var original = disk.Main.ToArray();
        await using var library = Open(disk); bool rollbackSaved = false;
        var preview = await library.PreviewRestoreAsync(Document(Entry("replacement")), LibraryRestoreMode.ReplaceLibrary);
        await Assert.ThrowsAsync<IOException>(() => library.RestoreAsync(preview, (bytes, _, _) => { Assert.Equal("星空の航路", Codec.Decode(bytes)[0].Name); rollbackSaved = true; return Task.CompletedTask; }));
        Assert.True(rollbackSaved); Assert.Equal(original, disk.Main); Assert.Equal("星空の航路", library.Snapshot.Entries[0].Name);
    }
    [Fact]
    public async Task RunningSessionRefusesReplacementButPortableBackupNeverGrantsOwnership()
    {
        await using var rig = new SessionTestRig(); await rig.Start(); var original = rig.Disk.Main!.ToArray(); bool rollback = false;
        var preview = await rig.Library.PreviewRestoreAsync(Document(Entry()), LibraryRestoreMode.ReplaceLibrary);
        await Assert.ThrowsAsync<SessionConflictException>(() => rig.Library.RestoreAsync(preview, (_, _, _) => { rollback = true; return Task.CompletedTask; }));
        Assert.False(rollback); Assert.Equal(original, rig.Disk.Main);
        var portable = await rig.Library.ExportPortableAsync(); var session = Assert.Single(Codec.Decode(portable)[0].Sessions);
        Assert.True(session.Native!.IsTerminal); Assert.Null(session.Native.RecoveryToken); Assert.Equal(SessionReason.ImportedSnapshot, session.Native.Reason);
        var disk = new SessionMemoryDisk { Main = Document() }; await using var other = Open(disk);
        var import = await other.PreviewRestoreAsync(portable, LibraryRestoreMode.ReplaceLibrary);
        await other.RestoreAsync(import, (_, _, _) => Task.CompletedTask);
        Assert.True(other.Snapshot.Entries[0].Sessions[0].Native!.IsTerminal);
    }
    [Fact]
    public async Task InvalidBackupIsRejectedBeforeConsentAndWrite()
    {
        var disk = new SessionMemoryDisk { Main = Document(Entry()) }; await using var library = Open(disk);
        await Assert.ThrowsAsync<InvalidDataException>(() => library.PreviewRestoreAsync(new byte[] { 0, 1 }, LibraryRestoreMode.ReplaceLibrary));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.PreviewRestoreAsync(Document(), (LibraryRestoreMode)999));
        Assert.Equal(0, disk.Writes);
    }
}
