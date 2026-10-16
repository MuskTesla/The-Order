using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class UpdateChannelTests
{
    internal static AppUpdateRelease Release(string version, int protocol = 1) => AppUpdateTests.Release(version) with { ChannelProtocol = protocol };
    [Theory]
    [InlineData("1.1.1", "1.2.0-beta.2", 1, true)]
    [InlineData("1.1.1", "1.2.0-beta.1", 0, false)]
    [InlineData("1.2.0-beta.2", "1.1.1", 1, true)]
    [InlineData("1.2.0-beta.2", "1.1.0", 0, false)]
    [InlineData("1.2.0-beta.2", "1.2.0", 1, true)]
    [InlineData("1.2.0", "1.2.0-beta.3", 1, false)]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.9", 1, false)]
    [InlineData("1.2.0-beta.9", "1.2.0-beta.10", 1, true)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.2", 1, false)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.3", 1, true)]
    [InlineData("1.2.0-beta.3", "1.2.0-beta.2", 1, false)]
    [InlineData("1.2.0-beta.3", "1.1.1", 1, true)]
    [InlineData("1.1.1", "1.1.0", 1, false)]
    public void OnlyVerifiedExplicitChannelRollbackMayDowngrade(string current, string offered, int protocol, bool expected)
        => Assert.Equal(expected, AppUpdateVersion.CanApply(current, Release(offered, protocol)));
    [Theory]
    [InlineData("1.2.0-beta.0")] [InlineData("1.2.0-beta.01")] [InlineData("1.2.0-beta.65536")]
    [InlineData("65536.0.0")] [InlineData("1.2.0+secret")] [InlineData("1.2.0-beta.2\n")]
    [InlineData("01.2.0-beta.1")] [InlineData("1.2.0-rc.1")] [InlineData("1.2.0.1")]
    public void MalformedOrUnsupportedSemverCannotEnterChannel(string input) => Assert.False(AppUpdateVersion.TryParse(input, out _));
    [Theory]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.9")] [InlineData("1.2.0", "1.2.0-beta.65535")]
    [InlineData("1.3.0-beta.1", "1.2.99")] [InlineData("2.0.0-beta.1", "1.99.99")]
    public void VersionsSortSemantically(string higher, string lower)
    { Assert.True(AppUpdateVersion.TryParse(higher, out var a)); Assert.True(AppUpdateVersion.TryParse(lower, out var b)); Assert.True(a.CompareTo(b) > 0); }
    internal static byte[] Contract(string v, int schema = 1, string identity = WindowsUpdateInstaller.InstallerIdentity) => JsonSerializer.SerializeToUtf8Bytes(new { schema, profileContract = 1, version = v, installationIdentity = identity });
    internal static JsonObject Metadata(string v, byte[]? manifest = null, bool legacy = false)
    {
        manifest ??= Contract(v);
        var result = JsonNode.Parse(AppUpdateTests.Metadata(v, preview: v.Contains('-')))!.AsObject();
        if (!legacy) result["assets"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { name = "update-channel.json", state = "uploaded", size = manifest.Length,
            browser_download_url = $"https://github.com/Ph-Rank/The-Order/releases/download/v{v}/update-channel.json", digest = "sha256:" + Convert.ToHexString(SHA256.HashData(manifest)) }));
        return result;
    }
    private static GitHubUpdateFeed Feed(string metadata, Func<Uri, byte[]>? bytes = null, List<Uri>? calls = null)
        => new(new HttpClient(new AppUpdateTests.Handler((r, t) => {
            t.ThrowIfCancellationRequested(); calls?.Add(r.RequestUri!);
            return AppUpdateTests.Response(r.RequestUri!.Host == "api.github.com" ? AppUpdateTests.Json(metadata) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes!(r.RequestUri)) });
        })));
    [Fact]
    public async Task BetaFeedRanksVersionNotPublicationOrderAndSkipsLegacyIsolation()
    {
        var json = new JsonArray(Metadata("1.2.0-beta.9"), Metadata("1.1.1"), Metadata("1.2.0-beta.99", legacy:true), Metadata("1.2.0-beta.10")).ToJsonString();
        var calls = new List<Uri>(); using var feed = Feed(json, _ => Contract("1.2.0-beta.10"), calls);
        var result = await feed.LatestAsync(AppUpdateChannel.Beta, default);
        Assert.Equal("1.2.0-beta.10", result!.Version); Assert.Equal(1, result.ChannelProtocol);
        Assert.Equal(GitHubUpdateFeed.ChannelsUri, calls[0]); Assert.Equal(2, calls.Count);
    }
    [Fact]
    public async Task StableUsesLatestOnlyAndRejectsBetaFlag()
    {
        var calls = new List<Uri>(); using var feed = Feed(Metadata("1.2.0-beta.2").ToJsonString(), _ => throw new Exception(), calls);
        Assert.Null(await feed.LatestAsync(AppUpdateChannel.Stable, default));
        Assert.Equal(GitHubUpdateFeed.LatestUri, Assert.Single(calls));
    }
    [Theory]
    [InlineData("hash")] [InlineData("version")] [InlineData("schema")] [InlineData("identity")] [InlineData("duplicate")] [InlineData("foreign")]
    public async Task UnverifiedOrForeignContractsNeverGrantDowngrade(string fault)
    {
        var contract = Contract(fault == "version" ? "1.1.0" : "1.1.1", fault == "schema" ? 99 : 1, fault == "identity" ? "legacy-beta" : WindowsUpdateInstaller.InstallerIdentity);
        var meta = Metadata("1.1.1", contract);
        if (fault == "duplicate") meta["assets"]!.AsArray().Add(meta["assets"]![1]!.DeepClone());
        if (fault == "foreign") meta["assets"]![1]!["browser_download_url"] = "https://example.invalid/update-channel.json";
        using var feed = Feed(meta.ToJsonString(), _ => fault == "hash" ? Contract("2.0.0") : contract);
        await Assert.ThrowsAsync<AppUpdateException>(() => feed.LatestAsync(AppUpdateChannel.Stable, default));
    }
    [Fact]
    public async Task DuplicateVersionCannotBeChosenByResponseOrder()
    {
        using var feed = Feed(new JsonArray(Metadata("1.1.1"), Metadata("1.1.1")).ToJsonString());
        await Assert.ThrowsAsync<AppUpdateException>(() => feed.LatestAsync(AppUpdateChannel.Beta, default));
    }
    [Theory] [InlineData(429)] [InlineData(500)]
    public async Task ProviderFailureLeavesNoRelease(int status)
    {
        using var feed = new GitHubUpdateFeed(new HttpClient(new AppUpdateTests.Handler((_,_) => AppUpdateTests.Response(new((HttpStatusCode)status)))));
        await Assert.ThrowsAsync<AppUpdateException>(() => feed.LatestAsync(AppUpdateChannel.Beta, default));
    }
    [Fact]
    public async Task PreferenceSurvivesRestartWithoutLibraryOrCacheDependency()
    {
        using var f = new ProfileFixture(); Guid id; byte[] library;
        using (var p = f.Open()) { id = p.Manifest.ProfileId; library = p.ReadComponent("library.json")!;
            var store = new UpdateChannelPreferencesStore(p.Configuration()); Assert.Null(await store.LoadAsync(default));
            await store.SaveAsync(AppUpdateChannel.Beta, default); Assert.Equal(library, p.ReadComponent("library.json")); }
        using var reopened = f.Open(); Assert.Equal(id, reopened.Manifest.ProfileId);
        Assert.Equal(AppUpdateChannel.Beta, await new UpdateChannelPreferencesStore(reopened.Configuration()).LoadAsync(default));
        Assert.Equal(library, reopened.ReadComponent("library.json"));
    }
    [Theory] [InlineData("foreign")] [InlineData("future")] [InlineData("corrupt")]
    public async Task UnknownOrForeignPreferenceIsPreserved(string kind)
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var path = Path.Combine(p.Root, UpdateChannelPreferencesStore.FileName);
        File.WriteAllText(path, kind == "corrupt" ? "{" : JsonSerializer.Serialize(new { Version = kind == "future" ? 99 : 1,
            ProfileId = kind == "foreign" ? Guid.NewGuid() : p.Manifest.ProfileId, StoreId = p.Manifest.StoreId, Channel = 1 }));
        var bytes = File.ReadAllBytes(path); var store = new UpdateChannelPreferencesStore(p.Configuration());
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(AppUpdateChannel.Stable, default)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task CanceledPreferenceWriteDoesNotChangeExistingChoice()
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var store = new UpdateChannelPreferencesStore(p.Configuration());
        await store.SaveAsync(AppUpdateChannel.Beta, default); using var c = new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(AppUpdateChannel.Stable, c.Token));
        Assert.Equal(AppUpdateChannel.Beta, await store.LoadAsync(default));
    }
    [Fact]
    public async Task SuccessfulBetaInstallationReceiptIsReadable()
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var store = new WindowsUpdatePackages(p.Configuration());
        Directory.CreateDirectory(store.Root);File.WriteAllText(Path.Combine(store.Root,"last-result.json"),"{\"schema\":1,\"version\":\"1.2.0-beta.2\",\"success\":true}");
        var receipt=await store.ReadReceiptAsync(default);Assert.True(receipt?.Success);Assert.Equal("1.2.0-beta.2",receipt!.Version);
    }
    internal sealed class Fake : IAppUpdateChannelFeed, IAppUpdateChannelStore, IAppUpdateInstaller, IAppUpdatePackages
    {
        internal AppUpdateChannel? Saved; internal int Downloads; internal bool SaveFails, LoadFails;
        internal Func<AppUpdateChannel, CancellationToken, Task<AppUpdateRelease?>> Lookup = (ch, _) => Task.FromResult<AppUpdateRelease?>(Release(ch == AppUpdateChannel.Stable ? "1.1.1" : "1.2.0-beta.2"));
        public Task<AppUpdateRelease?> LatestAsync(CancellationToken t) => LatestAsync(AppUpdateChannel.Stable, t);
        public Task<AppUpdateRelease?> LatestAsync(AppUpdateChannel c, CancellationToken t) => Lookup(c,t);
        public Task<AppUpdateChannel?> LoadAsync(CancellationToken t) => LoadFails ? Task.FromException<AppUpdateChannel?>(new IOException()) : Task.FromResult(Saved);
        public Task SaveAsync(AppUpdateChannel c, CancellationToken t) { t.ThrowIfCancellationRequested(); if (SaveFails) throw new IOException(); Saved=c;return Task.CompletedTask; }
        public AppUpdateAvailability Availability => new(true, "synthetic");
        public Task<IAppUpdateHandoff> PrepareAsync(VerifiedAppUpdate p, CancellationToken t) => throw new NotSupportedException();
        public Task<VerifiedAppUpdate> DownloadAsync(IAppUpdateFeed f, AppUpdateRelease r, IProgress<AppUpdateTransfer> p, CancellationToken t) { Downloads++; t.ThrowIfCancellationRequested();return Task.FromResult(new VerifiedAppUpdate(r,"synthetic/setup.exe")); }
        public Task<AppUpdateReceipt?> ReadReceiptAsync(CancellationToken t) => Task.FromResult<AppUpdateReceipt?>(null);
        public Task CopyInstallerAsync(AppUpdateRelease r, Stream s, IProgress<AppUpdateTransfer> p, CancellationToken t) => throw new NotSupportedException();
    }
    [Fact]
    public async Task BetaCanReturnToStableAndOptInAgainWithoutAutomaticInstall()
    {
        var fake = new Fake(); using var vm = new AppUpdateViewModel(fake,fake,fake,"1.2.0-beta.2",fake);
        await vm.ChangeChannelAsync(AppUpdateChannel.Stable); await vm.CheckAutomaticallyAsync();
        Assert.Equal("1.1.1", vm.Latest?.Version); Assert.Equal("回到正式版",vm.InstallText); Assert.True(vm.CanInstall); Assert.Equal(0,fake.Downloads);
        Assert.Equal(AppUpdateChannel.Stable,fake.Saved);
        using var stable = new AppUpdateViewModel(fake,fake,fake,"1.1.1",fake); await stable.CheckAsync(); Assert.False(stable.HasUpdate);
        await stable.ChangeChannelAsync(AppUpdateChannel.Beta); await stable.CheckAsync(); Assert.Equal("1.2.0-beta.2",stable.Latest?.Version);
        Assert.Equal("切换到 Beta",stable.InstallText); Assert.Equal(0,fake.Downloads);
    }
    [Fact]
    public async Task ChangingChannelInvalidatesLateQuietCheck()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource<AppUpdateRelease?>();
        var fake = new Fake { Lookup = async (_,_) => { entered.SetResult(); return await finish.Task; } };
        using var vm = new AppUpdateViewModel(fake,fake,fake,"1.1.1",fake); var pending = vm.CheckAutomaticallyAsync(); await entered.Task;
        await vm.ChangeChannelAsync(AppUpdateChannel.Beta); fake.Lookup = (ch,_)=>Task.FromResult<AppUpdateRelease?>(Release("1.2.0-beta.2"));
        await vm.CheckAsync(); finish.SetResult(Release("1.1.9")); await pending;
        Assert.Equal("1.2.0-beta.2",vm.Latest?.Version); Assert.Equal(AppUpdateChannel.Beta,vm.SelectedChannel); Assert.Equal(0,fake.Downloads);
    }
    [Fact]
    public async Task FailedPreferenceLoadCanBeRetriedAndSaveFailureKeepsChoice()
    {
        var fake = new Fake { LoadFails=true }; using var vm = new AppUpdateViewModel(fake,fake,fake,"1.1.1",fake);
        await vm.CheckAsync(); Assert.False(vm.HasUpdate); fake.LoadFails=false;
        await vm.ChangeChannelAsync(AppUpdateChannel.Beta); Assert.Equal(AppUpdateChannel.Beta, vm.SelectedChannel);
        fake.SaveFails=true; await vm.ChangeChannelAsync(AppUpdateChannel.Stable); Assert.Equal(AppUpdateChannel.Beta,vm.SelectedChannel); Assert.True(vm.CanChangeChannel);
    }
    [Fact]
    public void TestProfileBootstrapCannotProbeFormalProfiles()
    {
        var request = new NativeProfileRequest(NativeProfileKind.Test, @"C:\synthetic-never-open");
        Assert.Same(request, NativeChannelProfiles.Resolve(request, @"C:\must-not-exist", () => throw new Exception()));
        var unauthorized = new NativeProfileRequest(NativeProfileKind.Production, NativeProfilePaths.ProductionRoot);
        Assert.Same(unauthorized, NativeChannelProfiles.Resolve(unauthorized, @"C:\must-not-exist", () => throw new Exception()));
    }
    [Fact]
    public void LegacyBetaChoiceIsExplicitAndNoDataIsCopiedOrMerged()
    {
        using var f = new ProfileFixture(); var betaRoot=Path.Combine(f.Parent,"legacy-beta"); var install=Path.Combine(f.Parent,"installation");Directory.CreateDirectory(install);
        var request=new NativeProfileRequest(NativeProfileKind.Production,f.Root); Guid id;
        using(var beta=NativeProfileStore.Open(new(NativeProfileKind.Beta,betaRoot),NativeProfileTests.Codec)) id=beta.Manifest.ProfileId;
        var before=ProfileFixture.Hashes(betaRoot);
        Assert.Throws<NativeProfileException>(()=>NativeChannelProfiles.ResolveAt(request,install,f.Root,betaRoot,()=>null));
        Assert.Same(request,NativeChannelProfiles.ResolveAt(request,install,f.Root,betaRoot,()=>false));
        var selected=NativeChannelProfiles.ResolveAt(request,install,f.Root,betaRoot,()=>true);Assert.Equal(id,selected.ExpectedProfileId);Assert.Equal(betaRoot,selected.Root);
        Assert.False(Directory.Exists(f.Root)); Assert.Equal(before,ProfileFixture.Hashes(betaRoot));
        using var opened=NativeProfileStore.Open(selected,NativeProfileTests.Codec);Assert.Equal(id,opened.Manifest.ProfileId);
    }
    [Fact]
    public void BoundProfileChoiceSurvivesFormattingButRejectsChangedStoreOrRoot()
    {
        using var f=new ProfileFixture();using var profile=f.Open();var install=Path.Combine(f.Parent,"installation");Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install,"installer-identity.ini"),"[Installer]\nIdentity="+WindowsUpdateInstaller.InstallerIdentity);File.WriteAllBytes(Path.Combine(install,"unins000.exe"),[77,90]);
        NativeChannelProfiles.Remember(profile,install);
        var path=Path.Combine(install,NativeChannelProfiles.FileName);var node=JsonNode.Parse(File.ReadAllText(path))!;
        File.WriteAllText(path,node.ToJsonString(new(){WriteIndented=true}));NativeChannelProfiles.Remember(profile,install);
        var choice=NativeChannelProfiles.ResolveAt(f.Request,install,f.Root,Path.Combine(f.Parent,"beta"),()=>throw new Exception());Assert.Equal(profile.Manifest.StoreId,choice.ExpectedStoreId);
        node["StoreId"]=Guid.NewGuid();File.WriteAllText(path,node.ToJsonString());Assert.Throws<NativeProfileException>(()=>NativeChannelProfiles.Remember(profile,install));
        node["Kind"]=99;File.WriteAllText(path,node.ToJsonString());Assert.Throws<NativeProfileException>(()=>NativeChannelProfiles.ResolveAt(f.Request,install,f.Root,Path.Combine(f.Parent,"beta"),()=>true));
    }
}
