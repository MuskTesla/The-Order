using System.Collections.Concurrent;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

public sealed class GlobalLaunchPolicyTests
{
    private const string Locale = @"C:\synthetic-global-tools\LEProc.exe";
    private const string Magpie = @"C:\synthetic-global-tools\Magpie.exe";
    private static Installation Game(LaunchAuxiliaries? auxiliaries = null, LaunchCompatibility? compatibility = null)
        => new("installation", @"C:\synthetic-game", @"C:\synthetic-game\game.exe", ["two words", "日本語"], @"C:\synthetic-game")
        {
            SaveFolder = @"C:\synthetic-save",
            Compatibility = compatibility ?? new() { Auxiliaries = auxiliaries ?? new() }
        };
    private static GlobalLaunchDefaults Defaults(bool enabled) => new(enabled, Locale, enabled, Magpie, enabled);

    public static IEnumerable<object[]> Overrides()
    {
        foreach (var global in new[] { false, true })
        foreach (var locale in Enum.GetValues<LaunchOverride>())
        foreach (var magpie in Enum.GetValues<LaunchOverride>())
        foreach (var mute in Enum.GetValues<LaunchOverride>())
            yield return [global, locale, magpie, mute];
    }

    [Fact]
    public void NewProfileStartsWithAllAuxiliaryDefaultsOffAndNoToolPaths()
    {
        var policy = new LaunchPolicy();
        Assert.Equal(new GlobalLaunchDefaults(false, null, false, null, false), policy.Current);
        var source = Game();
        Assert.Null(policy.Resolve(source).Installation.Compatibility);
        Assert.False(policy.BackgroundMute(source));
        Assert.NotNull(source.Compatibility!.Auxiliaries);
    }

    [Theory]
    [MemberData(nameof(Overrides))]
    public void EachOverrideWinsIndependentlyOverTheCurrentGlobalDefault(bool global,
        LaunchOverride locale, LaunchOverride magpie, LaunchOverride mute)
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(global));
        var original = Game(new(locale, magpie, mute));
        var resolved = policy.Resolve(original).Installation;
        Assert.Equal(Expected(locale, global) ? Locale : null, resolved.Compatibility?.LocaleEmulator);
        Assert.Equal(Expected(magpie, global) ? Magpie : null, resolved.Compatibility?.ScalingTool);
        Assert.Equal(Expected(mute, global), resolved.Compatibility?.MuteInBackground == true);
        Assert.Equal(Expected(mute, global), policy.BackgroundMute(original));
        Assert.Null(resolved.Compatibility?.Auxiliaries);
        Assert.Equal(new LaunchAuxiliaries(locale, magpie, mute), original.Compatibility!.Auxiliaries);
        Assert.Equal(original.Id, resolved.Id);
        Assert.Equal(original.Folder, resolved.Folder);
        Assert.Equal(original.Executable, resolved.Executable);
        Assert.Equal(original.WorkingDirectory, resolved.WorkingDirectory);
        Assert.Equal(original.SaveFolder, resolved.SaveFolder);
        Assert.Equal(original.Arguments, resolved.Arguments);
        static bool Expected(LaunchOverride mode, bool inherited) => mode == LaunchOverride.Enabled || mode == LaunchOverride.Inherit && inherited;
    }

    [Fact]
    public void ExistingInheritedInstallationUsesNewDefaultsWithoutPersistingASnapshot()
    {
        var policy = new LaunchPolicy();
        var source = Game();
        var before = policy.Resolve(source);
        policy.Apply(Defaults(true));
        var enabled = policy.Resolve(source);
        policy.Apply(new(false, @"D:\replacement\LEProc.exe", false, @"D:\replacement\Magpie.exe", false));
        var disabled = policy.Resolve(source);
        Assert.Null(before.Installation.Compatibility);
        Assert.Equal(Locale, enabled.Installation.Compatibility!.LocaleEmulator);
        Assert.Equal(Magpie, enabled.Installation.Compatibility.ScalingTool);
        Assert.True(enabled.Installation.Compatibility.MuteInBackground);
        Assert.Null(disabled.Installation.Compatibility);
        Assert.True(before.Revision < enabled.Revision && enabled.Revision < disabled.Revision);
        Assert.Equal(new LaunchAuxiliaries(), source.Compatibility!.Auxiliaries);
        Assert.Null(source.Compatibility.LocaleEmulator);
        Assert.Null(source.Compatibility.ScalingTool);
        Assert.False(source.Compatibility.MuteInBackground);
    }

    [Fact]
    public void ForcedSelectionsSurviveGlobalChangesWhileInheritedToolsUseTheNewPath()
    {
        var policy = new LaunchPolicy();
        var source = Game(new(LaunchOverride.Disabled, LaunchOverride.Enabled, LaunchOverride.Enabled));
        policy.Apply(Defaults(false));
        var first = policy.Resolve(source).Installation;
        policy.Apply(new(true, @"D:\new\LEProc.exe", true, @"D:\new\Magpie.exe", false));
        var second = policy.Resolve(source).Installation;
        Assert.Null(first.Compatibility!.LocaleEmulator);
        Assert.Null(second.Compatibility!.LocaleEmulator);
        Assert.Equal(Magpie, first.Compatibility.ScalingTool);
        Assert.Equal(@"D:\new\Magpie.exe", second.Compatibility.ScalingTool);
        Assert.True(first.Compatibility.MuteInBackground);
        Assert.True(second.Compatibility.MuteInBackground);
    }

    [Theory]
    [InlineData(LaunchOverride.Enabled)]
    [InlineData(LaunchOverride.Disabled)]
    public void ForcedOnOrOffRemainsUnchangedWhenOnlyTheGlobalEnabledDefaultsChange(LaunchOverride mode)
    {
        var policy = new LaunchPolicy();
        var source = Game(new(mode, mode, mode));
        policy.Apply(Defaults(false));
        var first = policy.Resolve(source).Installation;
        policy.Apply(Defaults(true));
        var second = policy.Resolve(source).Installation;
        Assert.Equal(first.Compatibility, second.Compatibility);
        Assert.Equal(mode == LaunchOverride.Enabled, policy.BackgroundMute(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAbsentOrExplicitlyDisabledCompatibilityNeverAdoptsEnabledGlobalTools(bool explicitCompatibility)
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(true));
        var source = Game() with { Compatibility = explicitCompatibility ? new LaunchCompatibility() : null };
        Assert.Same(source, policy.Resolve(source).Installation);
        Assert.False(policy.BackgroundMute(source));
        Assert.Equal(new LaunchAuxiliaries(LaunchOverride.Disabled, LaunchOverride.Disabled, LaunchOverride.Disabled),
            LaunchPolicy.LegacyOverrides(source.Compatibility));
    }

    [Fact]
    public void LegacySettingsAndDifferentInstalledToolPathsRemainIndependent()
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(false));
        var first = Game(compatibility: new(LocaleEmulator: @"D:\old-one\LEProc.exe", ScalingTool: @"D:\old-one\Magpie.exe", MuteInBackground: true));
        var second = Game(compatibility: new(HighDpi: true, LocaleEmulator: @"E:\old-two\LEProc.exe")) with { Id = "other-installation" };
        policy.Apply(Defaults(true));
        Assert.Same(first, policy.Resolve(first).Installation);
        Assert.Same(second, policy.Resolve(second).Installation);
        Assert.Equal(@"D:\old-one\LEProc.exe", policy.Resolve(first).Installation.Compatibility!.LocaleEmulator);
        Assert.Equal(@"E:\old-two\LEProc.exe", policy.Resolve(second).Installation.Compatibility!.LocaleEmulator);
        Assert.True(policy.BackgroundMute(first));
        Assert.False(policy.BackgroundMute(second));
    }

    [Fact]
    public void ConvertingALegacyConfigurationToExplicitOverridesPreservesItsEffectiveBehavior()
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(false));
        var original = new LaunchCompatibility(HighDpi: true, LocaleEmulator: @"D:\legacy\LEProc.exe", MuteInBackground: true);
        var source = Game(compatibility: original with { Auxiliaries = LaunchPolicy.LegacyOverrides(original) });
        Assert.Equal(original, policy.Resolve(source).Installation.Compatibility);
        policy.Apply(Defaults(true));
        Assert.Equal(original, policy.Resolve(source).Installation.Compatibility);
    }

    [Fact]
    public void InheritDoesNotReuseAShadowedLegacyToolPath()
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(true));
        var source = Game(compatibility: new(LocaleEmulator: @"D:\legacy\LEProc.exe", ScalingTool: @"D:\legacy\Magpie.exe", MuteInBackground: true)
            { Auxiliaries = new() });
        var effective = policy.Resolve(source).Installation.Compatibility!;
        Assert.Equal(Locale, effective.LocaleEmulator);
        Assert.Equal(Magpie, effective.ScalingTool);
        policy.Apply(Defaults(false));
        Assert.Null(policy.Resolve(source).Installation.Compatibility);
    }

    [Fact]
    public void DisabledOverridesIgnoreUnconfiguredOrUnreachableToolPaths()
    {
        var policy = new LaunchPolicy();
        var source = Game(new(LaunchOverride.Disabled, LaunchOverride.Disabled, LaunchOverride.Disabled));
        policy.Apply(new(true, null, true, null, true));
        Assert.Null(policy.Resolve(source).Installation.Compatibility);
        policy.Apply(new(true, @"Z:\synthetic-unavailable-drive\LEProc.exe", true, @"Z:\synthetic-unavailable-drive\Magpie.exe", true));
        Assert.Null(policy.Resolve(source).Installation.Compatibility);
        Assert.False(policy.BackgroundMute(source));
    }

    [Theory]
    [InlineData(true, LaunchOverride.Inherit)]
    [InlineData(false, LaunchOverride.Enabled)]
    public void EnabledLocaleWithoutAConfiguredPathFailsBeforeAdmission(bool global, LaunchOverride mode)
    {
        var policy = new LaunchPolicy();
        policy.Apply(new(LocaleEmulatorEnabled: global));
        var error = Assert.Throws<LaunchValidationException>(() => policy.Resolve(Game(new(mode, LaunchOverride.Disabled, LaunchOverride.Disabled))));
        Assert.Equal(LaunchOutcome.ExecutableMissing, error.Outcome);
        Assert.Contains("Locale Emulator", error.Message);
    }

    [Theory]
    [InlineData(true, LaunchOverride.Inherit)]
    [InlineData(false, LaunchOverride.Enabled)]
    public void EnabledMagpieWithoutAConfiguredPathFailsBeforeAdmission(bool global, LaunchOverride mode)
    {
        var policy = new LaunchPolicy();
        policy.Apply(new(MagpieEnabled: global));
        var error = Assert.Throws<LaunchValidationException>(() => policy.Resolve(Game(new(LaunchOverride.Disabled, mode, LaunchOverride.Disabled))));
        Assert.Equal(LaunchOutcome.ExecutableMissing, error.Outcome);
        Assert.Contains("Magpie", error.Message);
    }

    [Fact]
    public void GameSpecificUnrelatedCompatibilityAndSteamIdentityArePreserved()
    {
        var policy = new LaunchPolicy();
        policy.Apply(new(BackgroundMuteEnabled: true));
        var source = Game(compatibility: new(UseSteam: true) { Auxiliaries = new() }) with { SteamAppId = "480" };
        var result = policy.Resolve(source).Installation;
        Assert.Equal("480", result.SteamAppId);
        Assert.True(result.Compatibility!.UseSteam);
        Assert.True(result.Compatibility.MuteInBackground);
        Assert.False(result.Compatibility.Administrator);
        Assert.False(result.Compatibility.HighDpi);
        var dpi = Game(compatibility: new(HighDpi: true) { Auxiliaries = new() });
        Assert.True(policy.Resolve(dpi).Installation.Compatibility!.HighDpi);
    }

    [Fact]
    public void UnreadableGlobalSettingsFailClosedForInheritedInstallsButKeepLegacyBehavior()
    {
        var policy = new LaunchPolicy();
        var inherited = Game();
        var legacy = Game(compatibility: new(MuteInBackground: true));
        policy.Apply(Defaults(true), usable: false);
        var error = Assert.Throws<LaunchValidationException>(() => policy.Resolve(inherited));
        Assert.Equal(LaunchOutcome.LaunchFailed, error.Outcome);
        Assert.False(policy.BackgroundMute(inherited));
        Assert.Same(legacy, policy.Resolve(legacy).Installation);
        Assert.True(policy.BackgroundMute(legacy));
        policy.Apply(Defaults(false));
        Assert.Null(policy.Resolve(inherited).Installation.Compatibility);
    }

    [Fact]
    public void ChangingGlobalSettingsRejectsAPreviouslyPreparedCreationWithoutCallingIt()
    {
        var policy = new LaunchPolicy();
        var prepared = policy.Resolve(Game());
        policy.Apply(Defaults(true));
        var calls = 0;
        var error = Assert.Throws<LaunchValidationException>(() => policy.Admit(prepared.Revision, () => ++calls));
        Assert.Equal(LaunchOutcome.StaleInstallation, error.Outcome);
        Assert.Equal(0, calls);
        var current = policy.Resolve(Game());
        Assert.Equal(1, policy.Admit(current.Revision, () => ++calls));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UpdatingGlobalSettingsCannotInterleaveAnAdmittedSynchronousCreation()
    {
        var policy = new LaunchPolicy();
        var prepared = policy.Resolve(Game());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        var creation = Task.Run(() => policy.Admit(prepared.Revision, () =>
        {
            timeline.Enqueue("creation began");
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic creation was not released.");
            timeline.Enqueue("creation completed");
            return "created";
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var update = Task.Run(() =>
        {
            attempted.SetResult();
            policy.Apply(Defaults(true));
            timeline.Enqueue("defaults changed");
        });
        try { await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.Set(); }
        Assert.Equal("created", await creation.WaitAsync(TimeSpan.FromSeconds(10)));
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "creation began", "creation completed", "defaults changed" }, timeline.ToArray());
        Assert.Throws<LaunchValidationException>(() => policy.Admit(prepared.Revision, () => "late creation"));
    }

    [Fact]
    public void FailedCreationDoesNotRetryOrChangeDefaultsAndLeavesAdmissionUsable()
    {
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(true));
        var prepared = policy.Resolve(Game());
        var calls = 0;
        Assert.Throws<IOException>(() => policy.Admit<bool>(prepared.Revision, () =>
        {
            calls++;
            throw new IOException("Synthetic CreateProcess failure.");
        }));
        Assert.Equal(1, calls);
        Assert.Equal(Defaults(true), policy.Current);
        Assert.Equal(prepared.Revision, policy.Revision);
        Assert.Equal(2, policy.Admit(prepared.Revision, () => ++calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnknownOverrideValuesCannotBecomeEnabledOrInherited(int field)
    {
        var unknown = (LaunchOverride)12345;
        var mode = field switch { 0 => new LaunchAuxiliaries(unknown), 1 => new(Magpie: unknown), _ => new(BackgroundMute: unknown) };
        var policy = new LaunchPolicy();
        policy.Apply(Defaults(true));
        Assert.Throws<InvalidDataException>(() => policy.Resolve(Game(mode)));
        Assert.Throws<InvalidDataException>(() => LaunchPolicy.Enabled(unknown, false));
        Assert.Throws<InvalidDataException>(() => LaunchPolicy.Enabled(unknown, true));
    }

    [Theory]
    [InlineData("relative\\LEProc.exe", null)]
    [InlineData(@"C:\tools\not-LEProc.exe", null)]
    [InlineData(null, @"C:\tools\not-Magpie.exe")]
    [InlineData("C:\\tools\\LEProc.exe\0", null)]
    public void MalformedGlobalToolSettingsAreRejectedWithoutReplacingThePreviousPolicy(string? locale, string? magpie)
    {
        var policy = new LaunchPolicy();
        var revision = policy.Revision;
        Assert.Throws<InvalidDataException>(() => policy.Apply(new(LocaleEmulatorPath: locale, MagpiePath: magpie)));
        Assert.Equal(new GlobalLaunchDefaults(), policy.Current);
        Assert.Equal(revision, policy.Revision);
    }
}
