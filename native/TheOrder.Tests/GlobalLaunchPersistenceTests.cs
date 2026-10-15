using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class GlobalLaunchPersistenceTests
{
    private const string Locale = @"C:\synthetic-global\LEProc.exe";
    private const string Magpie = @"C:\synthetic-global\Magpie.exe";
    private static readonly NativeLibraryCodec Codec = new();
    private static GlobalLaunchDefaults Enabled => new(true, Locale, true, Magpie, true);
    private static Installation Installation(LaunchCompatibility? compatibility = null) =>
        new("i", @"C:\synthetic-game", @"C:\synthetic-game\game.exe", ["two words", "日本語"], @"C:\synthetic-game")
        { SaveFolder = @"C:\synthetic-save", Compatibility = compatibility };
    private static LibraryEntry Entry(LaunchCompatibility? compatibility = null) =>
        RecognitionBoundaryTests.Entry(folder: @"C:\synthetic-game") with { Installations = [Installation(compatibility)] };
    private static InstallationRow Row(Installation installation) => new(installation, PreflightStatus.Valid, "synthetic", true);

    [Fact]
    public async Task MissingOptionalSettingsStartAllOffWithoutWritingAnyProfileComponent()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        var before = f.Hashes();
        await using var store = new GlobalLaunchDefaultsStore(p.Configuration());
        var loaded = await store.LoadAsync();
        Assert.Equal(new GlobalLaunchDefaults(), loaded.Value);
        Assert.False(loaded.ReadOnly);
        Assert.Null(loaded.Warning);
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        Assert.True(settings.CanChange);
        Assert.False(settings.HasChanges);
        Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        Assert.False(File.Exists(Path.Combine(f.Root, GlobalLaunchDefaultsStore.FileName)));
        Assert.Equal(before, f.Hashes());
    }

    [Fact]
    public async Task GlobalDefaultsSurviveRestartWithTheSameProfileAndOtherFilesUnchanged()
    {
        using var f = new ProfileFixture();
        Dictionary<string, byte[]> before;
        Guid profileId, storeId;
        using (var p = f.Open())
        {
            profileId = p.Manifest.ProfileId;
            storeId = p.Manifest.StoreId;
            before = Directory.GetFiles(f.Root).Where(x => Path.GetFileName(x) != ".the-order-writer.lock")
                .ToDictionary(x => Path.GetFileName(x), x => File.ReadAllBytes(x));
            await using var store = new GlobalLaunchDefaultsStore(p.Configuration());
            await store.SaveAsync(Enabled);
        }
        using var reopened = f.Open();
        Assert.Equal(profileId, reopened.Manifest.ProfileId);
        Assert.Equal(storeId, reopened.Manifest.StoreId);
        await using var restored = new GlobalLaunchDefaultsStore(reopened.Configuration());
        var loaded = await restored.LoadAsync();
        Assert.Equal(Enabled, loaded.Value);
        Assert.False(loaded.ReadOnly);
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Root, name)));
    }

    [Theory]
    [InlineData(PersistenceStage.BeforeTempWrite)]
    [InlineData(PersistenceStage.DuringTempWrite)]
    [InlineData(PersistenceStage.AfterTempWrite)]
    [InlineData(PersistenceStage.BeforeReplace)]
    [InlineData(PersistenceStage.Replace)]
    public async Task InterruptedAtomicWriteRetainsThePreviousGlobalDefaults(PersistenceStage stage)
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        await using var good = new GlobalLaunchDefaultsStore(p.Configuration());
        await good.SaveAsync(Enabled);
        var path = Path.Combine(f.Root, GlobalLaunchDefaultsStore.FileName);
        var original = File.ReadAllBytes(path);
        await using var interrupted = new GlobalLaunchDefaultsStore(p.Configuration(), x =>
            x == stage ? throw new IOException("Synthetic interrupted global setting write.") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => interrupted.SaveAsync(new()));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(Enabled, (await good.LoadAsync()).Value);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }

    [Fact]
    public async Task AnErrorAfterAtomicReplaceDoesNotClaimTheCommittedDefaultsWereRolledBack()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        await using var store = new GlobalLaunchDefaultsStore(p.Configuration(), stage =>
            stage == PersistenceStage.AfterReplace ? throw new IOException("Synthetic postcommit observation failure.") : Task.CompletedTask);
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        settings.BackgroundMuteEnabled = true;
        await settings.SaveAsync();
        Assert.True(settings.Saved.BackgroundMuteEnabled);
        Assert.True(policy.Current.BackgroundMuteEnabled);
        Assert.True((await store.LoadAsync()).Value.BackgroundMuteEnabled);
        Assert.False(settings.HasChanges);
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("ProfileId")]
    [InlineData("StoreId")]
    [InlineData("Future")]
    [InlineData("InvalidPath")]
    [InlineData("NullDefaults")]
    [InlineData("Corrupt")]
    [InlineData("DuplicateEnvelope")]
    [InlineData("DuplicateDefaults")]
    [InlineData("Oversize")]
    public async Task UnreadableOrForeignSettingsAreReadOnlyAndNeverOverwritten(string kind)
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        await using var store = new GlobalLaunchDefaultsStore(p.Configuration());
        await store.SaveAsync(Enabled);
        var path = Path.Combine(f.Root, GlobalLaunchDefaultsStore.FileName);
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (kind)
        {
            case "Version": json["Version"] = 2; break;
            case "ProfileId": json["ProfileId"] = Guid.NewGuid(); break;
            case "StoreId": json["StoreId"] = Guid.NewGuid(); break;
            case "Future": json["Future"] = "preserve this value"; break;
            case "InvalidPath": json["Defaults"]!["LocaleEmulatorPath"] = "relative/LEProc.exe"; break;
            case "NullDefaults": json["Defaults"] = null; break;
        }
        var text = kind switch
        {
            "Corrupt" => "{not valid JSON",
            "DuplicateEnvelope" => json.ToJsonString().Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal),
            "DuplicateDefaults" => json.ToJsonString().Replace("\"MagpieEnabled\":true", "\"MagpieEnabled\":true,\"MagpieEnabled\":false", StringComparison.Ordinal),
            "Oversize" => new string('x', 131073),
            _ => json.ToJsonString()
        };
        File.WriteAllText(path, text);
        var before = File.ReadAllBytes(path);
        var read = await store.LoadAsync();
        Assert.True(read.ReadOnly);
        Assert.NotNull(read.Warning);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()));
        Assert.Equal(before, File.ReadAllBytes(path));
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        Assert.False(settings.CanChange);
        Assert.Throws<LaunchValidationException>(() => policy.Resolve(Installation(new() { Auxiliaries = new() })));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CopyingSettingsToAnotherSyntheticProfileDoesNotTransferTheirOwnership()
    {
        using var first = new ProfileFixture();
        using var second = new ProfileFixture();
        using (var source = first.Open())
        {
            await using var sourceStore = new GlobalLaunchDefaultsStore(source.Configuration());
            await sourceStore.SaveAsync(Enabled);
        }
        using var destination = second.Open();
        var path = Path.Combine(second.Root, GlobalLaunchDefaultsStore.FileName);
        File.Copy(Path.Combine(first.Root, GlobalLaunchDefaultsStore.FileName), path);
        var before = File.ReadAllBytes(path);
        await using var destinationStore = new GlobalLaunchDefaultsStore(destination.Configuration());
        Assert.True((await destinationStore.LoadAsync()).ReadOnly);
        await Assert.ThrowsAsync<InvalidDataException>(() => destinationStore.SaveAsync(new()));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PreCancelledAndDisposedStoreOperationsDoNotWrite()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        var before = f.Hashes();
        var store = new GlobalLaunchDefaultsStore(p.Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Enabled, new(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(new(true)));
        Assert.Equal(before, f.Hashes());
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(Enabled));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync());
        Assert.Equal(before, f.Hashes());
    }

    [Fact]
    public async Task QueuedCancellationDoesNotCancelAnAlreadyAdmittedAtomicWrite()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        var entered = Gate();
        var release = Gate();
        await using var store = new GlobalLaunchDefaultsStore(p.Configuration(), async stage =>
        {
            if (stage != PersistenceStage.BeforeTempWrite) return;
            entered.TrySetResult();
            await release.Task;
        });
        var first = store.SaveAsync(Enabled);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var second = store.SaveAsync(new(), cancellation.Token);
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); }
        finally { release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Enabled, (await store.LoadAsync()).Value);
    }

    [Fact]
    public async Task StoreDisposeWaitsForTheAtomicWriteToFinishAndRejectsLaterAccess()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        var entered = Gate();
        var release = Gate();
        var store = new GlobalLaunchDefaultsStore(p.Configuration(), async stage =>
        {
            if (stage != PersistenceStage.BeforeReplace) return;
            entered.TrySetResult();
            await release.Task;
        });
        var save = store.SaveAsync(Enabled);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = store.DisposeAsync().AsTask();
        try { Assert.False(close.IsCompleted); }
        finally { release.TrySetResult(); }
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync());
        await using var reopened = new GlobalLaunchDefaultsStore(p.Configuration());
        Assert.Equal(Enabled, (await reopened.LoadAsync()).Value);
    }

    [Fact]
    public async Task SavingGlobalDefaultsDoesNotMigrateOrRewriteLegacyInstallations()
    {
        using var f = new ProfileFixture();
        using var p = f.Open();
        var first = Entry(new(LocaleEmulator: @"D:\first-tools\LEProc.exe", MuteInBackground: true));
        var second = Entry(new(ScalingTool: @"E:\second-tools\Magpie.exe")) with
        {
            Id = "other-game",
            Installations = [Installation(new(ScalingTool: @"E:\second-tools\Magpie.exe")) with
                { Id = "other-installation", Folder = @"C:\other-synthetic-game", Executable = @"C:\other-synthetic-game\game.exe", WorkingDirectory = null }],
            PreferredInstallationId = "other-installation"
        };
        await using var library = TransactionalLibrary.Open(Codec, p.CreateLibraryPersistence());
        await library.ImportAsync(Codec.Encode([first, second], RecognitionBoundaryTests.Clock));
        var before = File.ReadAllBytes(Path.Combine(f.Root, "library.json"));
        await using var store = new GlobalLaunchDefaultsStore(p.Configuration());
        await store.SaveAsync(Enabled);
        await store.SaveAsync(new());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(f.Root, "library.json")));
        Assert.Equal(@"D:\first-tools\LEProc.exe", library.Snapshot.Entries.Single(x => x.Id == first.Id).Installations[0].Compatibility!.LocaleEmulator);
        Assert.Equal(@"E:\second-tools\Magpie.exe", library.Snapshot.Entries.Single(x => x.Id == second.Id).Installations[0].Compatibility!.ScalingTool);
        Assert.All(library.Snapshot.Entries, e => Assert.Null(e.Installations[0].Compatibility!.Auxiliaries));
    }

    [Fact]
    public void LegacyExtrasRemainV1WithoutAnUnknownAuxiliariesProperty()
    {
        var compatibility = new LaunchCompatibility(LocaleEmulator: @"D:\legacy\LEProc.exe", ScalingTool: @"D:\legacy\Magpie.exe", MuteInBackground: true);
        var bytes = Codec.Encode([Entry(compatibility)], RecognitionBoundaryTests.Clock);
        using var json = JsonDocument.Parse(bytes);
        var extras = json.RootElement.GetProperty("games")[0].GetProperty("nativeExtras");
        Assert.Equal(1, extras.GetProperty("version").GetInt32());
        var oldCompatibility = extras.GetProperty("installations")[0].GetProperty("compatibility");
        Assert.False(oldCompatibility.TryGetProperty("auxiliaries", out _));
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        var oldRead = oldCompatibility.Deserialize<OldCompatibility>(options)!;
        Assert.Equal(compatibility.LocaleEmulator, oldRead.LocaleEmulator);
        Assert.Equal(compatibility.ScalingTool, oldRead.ScalingTool);
        Assert.True(oldRead.MuteInBackground);
        Assert.Equal(compatibility, Assert.Single(Codec.Decode(bytes)).Installations[0].Compatibility);
    }

    [Theory]
    [InlineData(LaunchOverride.Inherit)]
    [InlineData(LaunchOverride.Enabled)]
    [InlineData(LaunchOverride.Disabled)]
    public void ExplicitAuxiliaryOverridesUseV2AndRoundtripWithoutChangingOtherInstallationData(LaunchOverride mode)
    {
        var compatibility = new LaunchCompatibility(HighDpi: true)
            { Auxiliaries = new(mode, LaunchOverride.Disabled, LaunchOverride.Enabled) };
        var original = Entry(compatibility);
        var bytes = Codec.Encode([original], RecognitionBoundaryTests.Clock);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(2, json.RootElement.GetProperty("games")[0].GetProperty("nativeExtras").GetProperty("version").GetInt32());
        var decoded = Assert.Single(Codec.Decode(bytes)).Installations[0];
        Assert.Equal(compatibility, decoded.Compatibility);
        Assert.Equal(original.Installations[0].Arguments.ToArray(), decoded.Arguments.ToArray());
        Assert.Equal(original.Installations[0].WorkingDirectory, decoded.WorkingDirectory);
        Assert.Equal(original.Installations[0].SaveFolder, decoded.SaveFolder);
    }

    [Fact]
    public void ANewOverrideCannotMasqueradeAsLegacyV1AndAFutureSchemaIsReadOnly()
    {
        var json = JsonNode.Parse(Codec.Encode([Entry(new() { Auxiliaries = new() })], RecognitionBoundaryTests.Clock))!;
        json["games"]![0]!["nativeExtras"]!["version"] = 1;
        Assert.Equal(LibraryAccess.InvalidInput, Codec.Inspect(Encoding.UTF8.GetBytes(json.ToJsonString()), RecognitionBoundaryTests.Clock).Access);
        json["games"]![0]!["nativeExtras"]!["version"] = int.MaxValue;
        Assert.Equal(LibraryAccess.ValidReadOnly, Codec.Inspect(Encoding.UTF8.GetBytes(json.ToJsonString()), RecognitionBoundaryTests.Clock).Access);
    }

    [Fact]
    public async Task SettingsCancelDoesNotApplyOrPersistAnUnsavedGlobalDraft()
    {
        var store = new Store();
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        var revision = policy.Revision;
        settings.LocaleEmulatorEnabled = true;
        settings.LocaleEmulatorPath = Locale;
        settings.MagpieEnabled = true;
        settings.MagpiePath = Magpie;
        settings.BackgroundMuteEnabled = true;
        Assert.True(settings.CanSave);
        Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        settings.Cancel();
        Assert.False(settings.HasChanges);
        Assert.Equal(new GlobalLaunchDefaults(), settings.Saved);
        Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        Assert.Equal(revision, policy.Revision);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSaveLeavesThePreviousEffectiveDefaultsAndDraftForRetry(bool cancelled)
    {
        var store = new Store { Failure = cancelled ? new OperationCanceledException("synthetic cancellation") : new IOException("synthetic failure") };
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        var revision = policy.Revision;
        settings.BackgroundMuteEnabled = true;
        await settings.SaveAsync();
        Assert.Equal(new GlobalLaunchDefaults(), settings.Saved);
        Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        Assert.Equal(revision, policy.Revision);
        Assert.True(settings.BackgroundMuteEnabled);
        Assert.True(settings.CanSave);
        Assert.Contains("未能保存", settings.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("reparse")]
    public async Task EnabledInvalidToolsCannotBeSavedOrReportedAsEnabled(string mode)
    {
        var store = new Store();
        var filesystem = new Filesystem { Mode = mode };
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, filesystem);
        await settings.InitializeAsync();
        settings.LocaleEmulatorEnabled = true;
        settings.LocaleEmulatorPath = Locale;
        await settings.SaveAsync();
        Assert.Equal(0, store.Saves);
        Assert.False(policy.Current.LocaleEmulatorEnabled);
        Assert.True(settings.CanSave);
        Assert.Contains("LEProc.exe", settings.Message);
    }

    [Fact]
    public async Task DisabledToolsCanRetainTheirConfiguredPathsWithoutInspectingOrLaunchingThem()
    {
        var store = new Store();
        var filesystem = new Filesystem { Mode = "missing" };
        var policy = new LaunchPolicy();
        await using var settings = new GlobalLaunchSettings(store, policy, filesystem);
        await settings.InitializeAsync();
        settings.LocaleEmulatorPath = Locale;
        settings.MagpiePath = Magpie;
        await settings.SaveAsync();
        Assert.Equal(0, filesystem.Inspections);
        Assert.Equal(new(false, Locale, false, Magpie, false), policy.Current);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task SettingsDisposeWaitsForSaveAndDoesNotApplyADifferentLateDraft()
    {
        var store = new Store { Held = Gate() };
        var policy = new LaunchPolicy();
        var settings = new GlobalLaunchSettings(store, policy, new Filesystem());
        await settings.InitializeAsync();
        settings.BackgroundMuteEnabled = true;
        var save = settings.SaveAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        settings.BackgroundMuteEnabled = false;
        var close = settings.DisposeAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted);
            Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        }
        finally { store.Held.TrySetResult(); }
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.Value.BackgroundMuteEnabled);
        Assert.True(settings.Saved.BackgroundMuteEnabled);
        Assert.True(policy.Current.BackgroundMuteEnabled);
        Assert.Equal(1, store.Saves);
        Assert.False(settings.CanChange);
    }

    [Fact]
    public void NewGameDraftInheritsAndLegacyDraftPreservesExplicitPreviousChoices()
    {
        var fresh = new InstallationDraft();
        Assert.True(fresh.UsesGlobal);
        Assert.Equal(new LaunchAuxiliaries(), fresh.Input.Compatibility!.Auxiliaries);
        Assert.Null(fresh.Input.Compatibility.LocaleEmulator);
        Assert.Null(fresh.Input.Compatibility.ScalingTool);
        var oldOff = new InstallationDraft(Row(Installation()));
        Assert.False(oldOff.UsesGlobal);
        Assert.Equal(new LaunchAuxiliaries(LaunchOverride.Disabled, LaunchOverride.Disabled, LaunchOverride.Disabled), oldOff.Input.Compatibility!.Auxiliaries);
        var oldOn = new InstallationDraft(Row(Installation(new(LocaleEmulator: Locale, ScalingTool: Magpie, MuteInBackground: true))));
        Assert.Equal(new LaunchAuxiliaries(LaunchOverride.Enabled, LaunchOverride.Enabled, LaunchOverride.Enabled), oldOn.Input.Compatibility!.Auxiliaries);
        Assert.Equal(Locale, oldOn.Input.Compatibility.LocaleEmulator);
        Assert.Equal(Magpie, oldOn.Input.Compatibility.ScalingTool);
    }

    [Fact]
    public void ResetToGlobalClearsOnlyAuxiliaryOverridesAndPreservesGameSpecificLaunchData()
    {
        var installation = Installation(new(HighDpi: true, LocaleEmulator: Locale, ScalingTool: Magpie, MuteInBackground: true));
        var draft = new InstallationDraft(Row(installation));
        draft.ResetAuxiliaries();
        var input = draft.Input;
        Assert.True(draft.UsesGlobal);
        Assert.Equal(new LaunchAuxiliaries(), input.Compatibility!.Auxiliaries);
        Assert.Null(input.Compatibility.LocaleEmulator);
        Assert.Null(input.Compatibility.ScalingTool);
        Assert.True(input.Compatibility.HighDpi);
        Assert.Equal(installation.SaveFolder, input.SaveFolder);
        Assert.Equal(installation.WorkingDirectory, input.WorkingDirectory);
        Assert.Equal(installation.Arguments.ToArray(), input.Arguments.ToArray());
        Assert.Equal(Locale, installation.Compatibility!.LocaleEmulator);
    }

    [Theory]
    [InlineData(LaunchOverride.Inherit)]
    [InlineData(LaunchOverride.Enabled)]
    [InlineData(LaunchOverride.Disabled)]
    public void EditingAnOverrideDoesNotCopyTheCurrentGlobalPathsIntoTheGameDraft(LaunchOverride mode)
    {
        var policy = new LaunchPolicy();
        policy.Apply(Enabled);
        var draft = new InstallationDraft { LocaleMode = (int)mode, MagpieMode = (int)mode, MuteMode = (int)mode };
        var input = draft.Input;
        Assert.Null(input.Compatibility!.LocaleEmulator);
        Assert.Null(input.Compatibility.ScalingTool);
        Assert.Equal(new LaunchAuxiliaries(mode, mode, mode), input.Compatibility.Auxiliaries);
        var resolved = policy.Resolve(Installation(input.Compatibility)).Installation;
        Assert.Equal(mode == LaunchOverride.Disabled ? null : Locale, resolved.Compatibility?.LocaleEmulator);
        Assert.Equal(mode == LaunchOverride.Disabled ? null : Magpie, resolved.Compatibility?.ScalingTool);
    }

    [Fact]
    public async Task CancellingAGameDraftDiscardsItsResetAndOverridesWithoutCallingSave()
    {
        var files = new Files(Installation(new(LocaleEmulator: Locale, MuteInBackground: true)));
        await using var workspace = new WorkspaceViewModel(new Library(), new Preferences(), new("synthetic"), new LegacyClientTextRules());
        await workspace.InitializeAsync();
        await using var view = new FilesViewModel(files, workspace);
        await view.LoadInstallationsAsync("a");
        view.Edit(Row(files.Value));
        var discarded = view.Draft!;
        discarded.ResetAuxiliaries();
        discarded.MagpieMode = (int)LaunchOverride.Enabled;
        view.CancelDraft();
        Assert.Null(view.Draft);
        Assert.Equal(0, files.Saves);
        view.Edit(Row(files.Value));
        Assert.NotSame(discarded, view.Draft);
        Assert.Equal((int)LaunchOverride.Enabled, view.Draft!.LocaleMode);
        Assert.Equal((int)LaunchOverride.Disabled, view.Draft.MagpieMode);
        Assert.Equal((int)LaunchOverride.Enabled, view.Draft.MuteMode);
        Assert.Equal(Locale, view.Draft.Input.Compatibility!.LocaleEmulator);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record OldCompatibility(bool Administrator = false, bool HighDpi = false, string? LocaleEmulator = null,
        string? ScalingTool = null, bool UseSteam = false, bool MuteInBackground = false);
    private sealed class Store : IGlobalLaunchDefaultsStore
    {
        public GlobalLaunchDefaults Value = new();
        public Exception? Failure;
        public int Saves;
        public TaskCompletionSource? Held;
        public readonly TaskCompletionSource Entered = Gate();
        public Task<GlobalLaunchDefaultsRead> LoadAsync(CancellationToken cancellation = default) => Task.FromResult(new GlobalLaunchDefaultsRead(Value));
        public async Task SaveAsync(GlobalLaunchDefaults value, CancellationToken cancellation = default)
        {
            Saves++;
            Entered.TrySetResult();
            if (Held is not null) await Held.Task;
            if (Failure is not null) throw Failure;
            Value = value;
        }
    }
    private sealed class Filesystem : IFilesystemIdentity
    {
        public string Mode = "valid";
        public int Inspections;
        public PathPreflight Inspect(string path, bool requireDirectory = true)
        {
            Interlocked.Increment(ref Inspections);
            return Mode == "missing" ? new(PreflightStatus.NotFound, null, "synthetic missing tool")
                : new(PreflightStatus.Valid, new(path, path, path, new("synthetic-volume", path), Mode == "directory", Mode == "reparse", 0), "synthetic tool");
        }
    }
    private sealed class Files(Installation value) : IOfflineFilesClient
    {
        public Installation Value = value;
        public int Saves;
        public Task<InstallationView> InstallationsAsync(string id, CancellationToken cancellation) => Task.FromResult(new InstallationView(
            new(new(Guid.Empty, id, Guid.Empty, [Value], Value.Id), [Value], Value.Id), [Row(Value)]));
        public Task<ClientResult> SaveAsync(InstallationDetails details, InstallationInput input, CancellationToken cancellation)
        { Saves++; return Task.FromResult(new ClientResult(ClientOutcome.Committed, "synthetic saved")); }
        public Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> ImportAsync(ImmutableArray<ScanCandidate> candidates, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> RemoveAsync(InstallationDetails details, string id, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> PreferAsync(InstallationDetails details, string id, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> CoverAsync(GameDetails details, string? path, CancellationToken cancellation) => throw new NotSupportedException();
    }
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences value, CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Library : IOfflineLibraryClient
    {
        public LibraryCapability Capability => new(true, true, "synthetic");
        public Task<ClientResult> LoadAsync(CancellationToken cancellation) => Task.FromResult(new ClientResult(ClientOutcome.Success, "synthetic"));
        public Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation) => Task.FromResult(new LibraryQueryResult([], 0, [], []));
        public Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
