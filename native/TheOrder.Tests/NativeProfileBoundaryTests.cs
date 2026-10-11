using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class NativeProfileBoundaryTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void MissingExpectedIdentityCannotBootstrapAnotherStore(bool profileIdentity)
    {
        using var f = new ProfileFixture();
        var request = f.Request with { ExpectedProfileId = profileIdentity ? Guid.NewGuid() : null, ExpectedStoreId = profileIdentity ? null : Guid.NewGuid() };
        Assert.Throws<NativeProfileException>(() => NativeProfileStore.Open(request, NativeProfileTests.Codec));
        Assert.False(File.Exists(Path.Combine(f.Root, NativeProfileStore.ManifestName)));
        Assert.False(File.Exists(Path.Combine(f.Root, NativeProfileStore.BootstrapName)));
        Assert.False(File.Exists(Path.Combine(f.Root, "library.json")));
    }
    [Fact]
    public async Task NativeReviewRoundTripAndFutureInnerSchemaAreGated()
    {
        using var f = new ProfileFixture(); var codec = new RecognitionReviewCodec();
        var bytes = codec.Write([new(Guid.NewGuid(), "g", "Synthetic", DateTimeOffset.UnixEpoch, RecognitionReviewStatus.NeedsRecheck, "test", null, [], @"C:\synthetic", null, "review")]);
        using (var profile = f.Open())
        {
            var adapter = new IsolatedRecognitionReviewStorage(profile.Configuration());
            await adapter.WriteAsync(bytes, default); Assert.Single(codec.Read(await adapter.ReadAsync(default)));
            var future = JsonNode.Parse(bytes)!; future["future"] = true;
            await Assert.ThrowsAsync<NativeProfileException>(() => adapter.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(future), default));
            Assert.Equal(bytes, profile.ReadComponent("native-recognition.json"));
        }
    }
    [Fact]
    public void DirectoryAliasIsRejectedBeforeCreatingAStoreInItsTarget()
    {
        using var f = new ProfileFixture(); var target = Path.Combine(f.Parent, "target"); Directory.CreateDirectory(target);
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{f.Root.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!; Assert.True(process.WaitForExit(15000)); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Empty(Directory.GetFiles(target)); }
        finally { Directory.Delete(f.Root); }
    }
    [Theory]
    [InlineData("library.json")][InlineData("native-ui.json")][InlineData("native-online-key.dpapi")]
    [InlineData("native-recognition.json")][InlineData("native-launch-positions.dpapi")]
    public void ForeignComponentCannotEnterAnotherProfile(string name)
    {
        using var a = new ProfileFixture(); using var b = new ProfileFixture(); using (a.Open()) { } using (b.Open()) { }
        File.Copy(Path.Combine(a.Root, name), Path.Combine(b.Root, name), true); var before = b.Hashes();
        Assert.Throws<NativeProfileException>(() => b.Open()); Assert.Equal(before, b.Hashes());
    }
    [Theory]
    [InlineData("library.json")][InlineData("native-ui.json")][InlineData("native-online-key.dpapi")]
    [InlineData("native-recognition.json")][InlineData("native-launch-positions.dpapi")]
    public void FutureBackupCannotBeUsedForCorruptMain(string name)
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        File.WriteAllText(Path.Combine(f.Root, name), "{broken"); var path = Path.Combine(f.Root, name + ".bak");
        var json = JsonNode.Parse(File.ReadAllBytes(path))!; json["Version"] = 2; File.WriteAllText(path, json.ToJsonString());
        var before = f.Hashes(); Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ValidMainCannotOverwriteFutureOrForeignBackup(bool foreign)
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        var path = Path.Combine(f.Root, "library.json.bak"); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        if (foreign) json["StoreId"] = Guid.NewGuid(); else json["Version"] = 2;
        File.WriteAllText(path, json.ToJsonString()); var before = f.Hashes();
        Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Fact]
    public void PhysicalRootCreateFailureDoesNotCreateAnyStore()
    {
        using var f = new ProfileFixture(); File.WriteAllText(f.Root, "existing file");
        Assert.Throws<IOException>(() => f.Open()); Assert.Equal("existing file", File.ReadAllText(f.Root));
        File.Delete(f.Root);
    }
    [Fact]
    public async Task MissingReadyManifestCannotResetUserDataOrFillMissingCompanion()
    {
        using var f = new ProfileFixture();
        using (var profile = f.Open()) await new IsolatedUiPreferencesStore(profile.Configuration()).SaveAsync(new(NativeTheme.Dark), default);
        File.Delete(Path.Combine(f.Root, NativeProfileStore.ManifestName)); File.Delete(Path.Combine(f.Root, "library.json"));
        var before = f.Hashes(); Assert.Throws<NativeProfileException>(() => f.Open()); Assert.Equal(before, f.Hashes());
    }
    [Fact]
    public void HardLinkedComponentIsRejectedBeforeReadingOrWriting()
    {
        using var f = new ProfileFixture(); using (f.Open()) { }
        var path = Path.Combine(f.Root, "library.json"); var link = Path.Combine(f.Parent, "alias");
        Assert.True(CreateHardLink(link, path, IntPtr.Zero)); Assert.Throws<DataIsolationException>(() => f.Open());
        File.Delete(link);
    }
    [Fact]
    public void IdentityFilesAndDirectoryCannotBeReplacedWhileWriterOwnsProfile()
    {
        using var f = new ProfileFixture(); using var profile = f.Open();
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(f.Root, NativeProfileStore.ManifestName), "{}"));
        Assert.Throws<IOException>(() => Directory.Move(f.Root, f.Root + "-moved"));
        using var library = profile.CreateLibraryPersistence();
        Assert.Throws<NativeProfileException>(() => profile.CreateLibraryPersistence());
        Assert.Throws<NativeProfileException>(() => profile.Dispose());
    }
    [Fact]
    public async Task ExplicitRestoredMainRetainsBackupRecoverySemanticsAcrossAnotherWriteAndRestart()
    {
        using var f = new ProfileFixture(); var platform = new SessionFakePlatform(); var files = new SessionFakeFilesystem(); var positions = new SessionFakePositions(); var tokens = new SessionFakeTokens(); var clock = new SessionFakeClock();
        using (var profile = f.Open())
        {
            await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
            {
                await library.ImportAsync(NativeProfileTests.Codec.Encode([SessionTestRig.Entry(total: 0, count: 0)], SessionTestRig.Date));
                await using var engine = new SessionEngine(library, files, platform, positions, clock, tokens, autoPoll: false);
                await engine.InitializeAsync(); var preview = await engine.PrepareAsync("g"); await engine.ConfirmPositionAsync(preview.Snapshot!);
                Assert.True((await engine.LaunchAsync("g")).ProcessCreated);
                Assert.True(await engine.CheckpointForExitAsync());
            }
            profile.CheckpointBackups();
        }
        File.WriteAllText(Path.Combine(f.Root, "library.json"), "{broken");
        using (var profile = f.Open()) profile.Restore("library.json");
        using (var profile = f.Open())
        await using (var library = TransactionalLibrary.Open(NativeProfileTests.Codec, profile.CreateLibraryPersistence()))
        {
            Assert.Equal(LibraryRecovery.Backup, library.Snapshot.Recovery);
            await library.UpdateAsync("g", new() { Notes = "ordinary edit does not grant recovery" });
        }
        var attachments = platform.Attachments;
        using var reopened = f.Open();
        await using var restored = TransactionalLibrary.Open(NativeProfileTests.Codec, reopened.CreateLibraryPersistence());
        Assert.Equal(LibraryRecovery.Backup, restored.Snapshot.Recovery);
        await using var recovery = new SessionEngine(restored, files, platform, positions, clock, tokens, autoPoll: false);
        await recovery.InitializeAsync();
        Assert.Equal(attachments, platform.Attachments); var session = Assert.Single(restored.Snapshot.Entries[0].Sessions);
        Assert.Equal(SessionReason.BackupRecovery, session.Native!.Reason); Assert.Null(session.Native.RecoveryToken); Assert.True(session.Native.IsTerminal);
        Assert.Equal(1, restored.Snapshot.Entries[0].PlayCount); Assert.Equal(1, platform.Starts); Assert.Equal(ProcessTrackingState.RunningConfirmed, platform.Processes[71].State);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string existing, IntPtr attributes);
}
