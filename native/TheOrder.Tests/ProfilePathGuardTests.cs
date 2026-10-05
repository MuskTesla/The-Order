using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class ProfilePathGuardTests(ITestOutputHelper output)
{
    // Strings only: these pretend formal roots are never created or queried.
    private static readonly ProfilePathGuard.KnownRoots Roots = new(@"C:\synthetic-guard\Electron", @"C:\synthetic-guard\Profiles\Production", @"C:\synthetic-guard\Profiles\Development");
    private static readonly string[] Entries = ["normal-start", "fixture-start", "explicit-test", "explicit-development", "open", "library-source", "library-factory", "persistence", "credential-read", "credential-write", "preferences-read", "preferences-write", "session-raw", "session-config", "positions", "reviews", "thumbnail", "backup", "restore", "open-existing"];
    public static IEnumerable<object[]> Aliases()
    {
        var p = Roots.Production;
        string[] paths = [p, p + "\\", p.ToLowerInvariant(), @"\\?\" + p, p + @"\child", p + @"\native-profile.json",
            p + @"\.\", p + @"\child\..", @"\\?\" + p + @"\child\..\library.json", p.Replace('\\', '/'), Path.GetDirectoryName(p)!,
            Roots.Development, @"\\?\" + Roots.Development, Roots.Legacy, @"\\?\" + Roots.Legacy];
        foreach (var path in paths)
            foreach (var entry in Entries)
                // The normal development capability is intentionally allowed its exact default root.
                if (!(entry == "explicit-development" && ProfilePathGuard.Canonicalize(path) == Roots.Development)) yield return [entry, path];
    }
    [Theory][MemberData(nameof(Aliases))]
    public async Task EveryEntryRejectsForbiddenAliasesBeforeAnyFilesystemAccess(string entry, string path)
    {
        var count = 0;
        ProfilePathGuard.Probe.Value = new(Roots, () => { count++; throw new InvalidOperationException("Filesystem boundary reached"); });
        try
        {
            var error = await Record.ExceptionAsync(() => Enter(entry, path));
            if (entry.StartsWith("credential-", StringComparison.Ordinal))
                Assert.Equal(OnlineFailure.CredentialStorage, Assert.IsType<OnlineException>(error).Failure);
            else Assert.True(error is NativeProfileException or DataIsolationException, error?.ToString() ?? "Entry unexpectedly allowed");
            Assert.Equal(0, count);
            output.WriteLine($"entry={entry}; alias={path}; RejectedBeforeIo=true; FilesystemQueryCount={count}");
        }
        finally { ProfilePathGuard.Probe.Value = null; }
    }
    private static async Task Enter(string entry, string path)
    {
        var configuration = new NativeConfiguration(path);
        switch (entry)
        {
            case "normal-start": NativeInstanceIdentity.ForNativeProfile(NativeProfilePaths.Select(NativeProfileKind.Production, null, path)); break;
            case "fixture-start": NativeInstanceIdentity.ForProfile(path); break;
            case "explicit-test": NativeProfilePaths.Validate(new(NativeProfileKind.Test, path)); break;
            case "explicit-development": NativeProfilePaths.Validate(new(NativeProfileKind.Development, path)); break;
            case "library-source": using (new IsolatedLibrarySource().OpenRead(path)) { } break;
            case "library-factory": using (await new IsolatedLibraryFactory(NativeProfileTests.Codec, configuration).OpenAsync(default)) { } break;
            case "persistence": using (new IsolatedLibraryPersistence(path)) { } break;
            case "credential-read": await new IsolatedCredentialStore(configuration).ReadAsync(default); break;
            case "credential-write": await new IsolatedCredentialStore(configuration).SaveAsync("synthetic", default); break;
            case "preferences-read": await new IsolatedUiPreferencesStore(configuration).LoadAsync(default); break;
            case "preferences-write": await new IsolatedUiPreferencesStore(configuration).SaveAsync(new(), default); break;
            case "session-raw": _ = new WindowsSessionRecoveryTokens(path); break;
            case "session-config": _ = new WindowsSessionRecoveryTokens(configuration); break;
            case "positions": await new IsolatedLaunchPositionStore(configuration).ReadAsync(default); break;
            case "reviews": await new IsolatedRecognitionReviewStorage(configuration).ReadAsync(default); break;
            case "thumbnail": await new IsolatedThumbnailCache(configuration).ReadAsync(new string('A', 64) + "-128", default); break;
            default:
                using (var profile = NativeProfileStore.Open(new(NativeProfileKind.Production, path), NativeProfileTests.Codec))
                {
                    if (entry == "backup") profile.CheckpointBackups();
                    else if (entry == "restore") profile.Restore("library.json");
                    else if (entry == "open-existing") using (profile.CreateLibraryPersistence()) { }
                }
                break;
        }
    }
    [Theory]
    [InlineData(@"\\server\share\profile")][InlineData(@"\\?\UNC\server\share\profile")]
    [InlineData(@"\\.\C:\profile")][InlineData(@"\??\C:\profile")][InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\profile")]
    [InlineData(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\profile")][InlineData(@"C:profile")]
    [InlineData(@"C:\profile:stream")][InlineData(@"C:\profile.\child")][InlineData(@"C:\PROFIL~1")]
    [InlineData(@"C:\NUL\profile")][InlineData(@"C:\COM1.txt\profile")]
    public async Task UnsupportedNamespacesAndAmbiguousNamesRejectBeforeIo(string path)
    {
        foreach (var entry in Entries) await EveryEntryRejectsForbiddenAliasesBeforeAnyFilesystemAccess(entry, path);
    }
    [Fact]
    public void PureAuthorizationSupportsOnlyExactAuthorizedProductionAndExactDevelopment()
    {
        var calls = 0; ProfilePathGuard.Probe.Value = new(Roots, () => { calls++; throw new IOException("boundary"); });
        try
        {
            Assert.Equal(Roots.Production, ProfilePathGuard.Validate(@"\\?\" + Roots.Production, NativeProfileKind.Production, true));
            Assert.Equal(Roots.Development, ProfilePathGuard.Validate(Roots.Development, NativeProfileKind.Development));
            Assert.Throws<NativeProfileException>(() => ProfilePathGuard.Validate(Roots.Production + @"\child", NativeProfileKind.Production, true));
            Assert.Throws<NativeProfileException>(() => ProfilePathGuard.Validate(Roots.Production, NativeProfileKind.Test, true));
            Assert.Equal(0, calls);
            Assert.Throws<IOException>(() => NativeProfilePaths.Validate(new(NativeProfileKind.Test, @"C:\synthetic-isolated")));
            Assert.Equal(1, calls); // Positive control: seam actually intercepts the first filesystem boundary.
        }
        finally { ProfilePathGuard.Probe.Value = null; }
    }
    [Fact]
    public async Task IsolatedExtendedDosRootWorksForRawAndProfileAdapters()
    {
        using var f = new ProfileFixture();
        var raw = Path.Combine(f.Parent, "raw"); Directory.CreateDirectory(raw);
        File.WriteAllText(Path.Combine(raw, IsolatedLibrarySource.MarkerName), IsolatedLibrarySource.MarkerValue);
        File.WriteAllBytes(Path.Combine(raw, "library.json"), NativeProfileTests.Codec.Encode([], SessionTestRig.Date));
        var extended = @"\\?\" + raw;
        Assert.Equal(NativeInstanceIdentity.ForProfile(raw), NativeInstanceIdentity.ForProfile(extended));
        using (var library = await new IsolatedLibraryFactory(NativeProfileTests.Codec, new(extended)).OpenAsync(default)) Assert.Empty(library.Snapshot.Entries);
        await new IsolatedUiPreferencesStore(new(extended)).SaveAsync(new(NativeTheme.Dark), default);
        Assert.Equal(NativeTheme.Dark, (await new IsolatedUiPreferencesStore(new(raw)).LoadAsync(default)).Theme);
        using var profile = NativeProfileStore.Open(f.Request with { Root = @"\\?\" + f.Root }, NativeProfileTests.Codec);
        Assert.Equal(f.Root, profile.Root);
        using var writer = profile.CreateLibraryPersistence(); Assert.NotNull(writer.ReadMain());
    }
    [Theory]
    [InlineData("borrow")][InlineData("backup")][InlineData("restore")][InlineData("read")]
    [InlineData("credentials")][InlineData("preferences")][InlineData("session")][InlineData("writer-read")][InlineData("writer-save")]
    public async Task ExistingCapabilityRechecksAuthorizationBeforeRecoveryOrComponentIo(string operation)
    {
        using var f = new ProfileFixture(); using var owner = f.Open();
        using var writer = operation.StartsWith("writer-", StringComparison.Ordinal) ? owner.CreateLibraryPersistence() : null;
        var calls = 0;
        ProfilePathGuard.Probe.Value = new(Roots with { Production = f.Root }, () => { calls++; throw new IOException("boundary"); });
        try
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                switch (operation)
                {
                    case "borrow": using (owner.CreateLibraryPersistence()) { } break;
                    case "backup": owner.CheckpointBackups(); break;
                    case "restore": owner.Restore("library.json"); break;
                    case "read": owner.ReadComponent("library.json"); break;
                    case "credentials": await new IsolatedCredentialStore(owner.Configuration()).ReadAsync(default); break;
                    case "preferences": await new IsolatedUiPreferencesStore(owner.Configuration()).LoadAsync(default); break;
                    case "session": _ = new WindowsSessionRecoveryTokens(owner.Configuration()); break;
                    case "writer-read": writer!.ReadBackup(); break;
                    case "writer-save": await writer!.SaveAsync(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty); break;
                }
            });
            Assert.Equal("ProductionAuthorizationRequired", Assert.IsType<NativeProfileException>(exception).Code);
            Assert.Equal(0, calls); output.WriteLine($"capability={operation}; RejectedBeforeIo=true; FilesystemQueryCount=0");
        }
        finally { ProfilePathGuard.Probe.Value = null; }
    }
}
