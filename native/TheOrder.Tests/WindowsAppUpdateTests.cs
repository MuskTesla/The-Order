using System.Net;
using System.Security.Cryptography;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class WindowsAppUpdateTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Parent = Path.Combine(Path.GetTempPath(), "the-order-update-" + Guid.NewGuid().ToString("N"));
        internal readonly NativeProfileStore Profile;
        internal Fixture() => Profile = NativeProfileStore.Open(new(NativeProfileKind.Test,Path.Combine(Parent,"profile")),new NativeLibraryCodec());
        public void Dispose()
        {
            Profile.Dispose();
            var root=Path.GetFullPath(Parent);
            if(root.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(root).StartsWith("the-order-update-",StringComparison.Ordinal))Directory.Delete(root,true);
        }
    }
    private static GitHubUpdateFeed Feed(byte[] bytes) => new(new HttpClient(new AppUpdateTests.Handler((_,_)=>AppUpdateTests.Response(new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}))));
    [Fact]
    public async Task OnlyVerifiedInstallerIsCommittedAndNoLibraryOrSecretChanges()
    {
        using var fixture=new Fixture(); var before=File.ReadAllBytes(Path.Combine(fixture.Profile.Root,"library.json"));
        var credentialBefore=File.ReadAllBytes(Path.Combine(fixture.Profile.Root,"native-online-key.dpapi"));
        var store=new WindowsUpdatePackages(fixture.Profile.Configuration());using var feed=Feed(AppUpdateTests.InstallerBytes);
        var result=await store.DownloadAsync(feed,AppUpdateTests.Release(),new Progress<AppUpdateTransfer>(),default);
        Assert.Equal(AppUpdateTests.InstallerBytes,File.ReadAllBytes(result.InstallerPath));
        Assert.Contains("ZoneId=3",File.ReadAllText(result.InstallerPath+":Zone.Identifier"));
        Assert.Equal(before,File.ReadAllBytes(Path.Combine(fixture.Profile.Root,"library.json")));
        Assert.Equal(credentialBefore,File.ReadAllBytes(Path.Combine(fixture.Profile.Root,"native-online-key.dpapi")));
        Assert.Empty(Directory.GetFiles(store.Root,"*.part",SearchOption.AllDirectories));
        var installer=new WindowsUpdateInstaller(fixture.Profile.Configuration(),store);Assert.False(installer.Availability.CanInstall);
        await Assert.ThrowsAsync<AppUpdateException>(()=>installer.PrepareAsync(result,default));
    }
    [Fact]
    public async Task CorruptAndCanceledDownloadsLeaveNoExecutableOrPartial()
    {
        using var fixture=new Fixture();var store=new WindowsUpdatePackages(fixture.Profile.Configuration());using var feed=Feed([77,90,0,0,0,0]);
        await Assert.ThrowsAsync<AppUpdateException>(()=>store.DownloadAsync(feed,AppUpdateTests.Release(),new Progress<AppUpdateTransfer>(),default));
        Assert.Empty(Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories));
        using var token=new CancellationTokenSource();token.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>store.DownloadAsync(feed,AppUpdateTests.Release(),new Progress<AppUpdateTransfer>(),token.Token));
    }
    [Fact]
    public async Task CacheDoesNotTrustWrongFormatEvenWithMatchingChecksum()
    {
        using var fixture=new Fixture();var store=new WindowsUpdatePackages(fixture.Profile.Configuration());byte[] invalid=[1,2,3,4,5,6];using var feed=Feed(invalid);
        var release=AppUpdateTests.Release() with {Sha256=Convert.ToHexString(SHA256.HashData(invalid))};
        await Assert.ThrowsAsync<AppUpdateException>(()=>store.DownloadAsync(feed,release,new Progress<AppUpdateTransfer>(),default));
        Assert.Empty(Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories));
    }
    private sealed class HeldDownload(TaskCompletionSource entered):IAppUpdateFeed
    {
        public Task<AppUpdateRelease?> LatestAsync(CancellationToken t)=>Task.FromResult<AppUpdateRelease?>(AppUpdateTests.Release());
        public async Task CopyInstallerAsync(AppUpdateRelease r,Stream output,IProgress<AppUpdateTransfer> p,CancellationToken t)
        {await output.WriteAsync(new byte[]{77,90},t);entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);}
    }
    [Fact]
    public async Task CancelAfterBytesArriveDeletesIncompleteDownload()
    {
        using var fixture=new Fixture();var store=new WindowsUpdatePackages(fixture.Profile.Configuration());
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var cancellation=new CancellationTokenSource();
        var work=store.DownloadAsync(new HeldDownload(entered),AppUpdateTests.Release(),new Progress<AppUpdateTransfer>(),cancellation.Token);
        await entered.Task;cancellation.Cancel();await Assert.ThrowsAsync<TaskCanceledException>(()=>work);
        Assert.Empty(Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories));
    }
    [Fact]
    public async Task TamperedCompletedPackageIsRejectedAgainAtHandoff()
    {
        using var fixture=new Fixture();var store=new WindowsUpdatePackages(fixture.Profile.Configuration());using var feed=Feed(AppUpdateTests.InstallerBytes);
        var result=await store.DownloadAsync(feed,AppUpdateTests.Release(),new Progress<AppUpdateTransfer>(),default);
        File.WriteAllBytes(result.InstallerPath,[77,90,0,0,0,0]);
        await Assert.ThrowsAsync<AppUpdateException>(()=>WindowsUpdatePackages.ValidateAsync(result.InstallerPath,result.Release,default));
    }
    [Fact]
    public async Task OptionalReceiptReportsFailureWithoutAffectingProfileStartup()
    {
        using var fixture=new Fixture();var store=new WindowsUpdatePackages(fixture.Profile.Configuration());Directory.CreateDirectory(store.Root);
        var path=Path.Combine(store.Root,"last-result.json");File.WriteAllText(path,"{");Assert.Null(await store.ReadReceiptAsync(default));
        File.WriteAllText(path,"{\"schema\":1,\"version\":\"1.0.3\",\"success\":false}");var receipt=await store.ReadReceiptAsync(default);
        Assert.NotNull(receipt);Assert.False(receipt.Success);Assert.Contains("没有完成",receipt.Message);
        File.WriteAllText(path,"{\"schema\":99,\"version\":\"1.0.3\",\"success\":true}");Assert.Null(await store.ReadReceiptAsync(default));
    }
}
