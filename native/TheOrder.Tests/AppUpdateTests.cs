using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class AppUpdateTests
{
    internal static byte[] InstallerBytes => [77,90,5,6,7,8];
    internal static AppUpdateRelease Release(string version = "1.0.3") => new(version, "测试更新说明", new($"https://github.com/Ph-Rank/The-Order/releases/download/v{version}/{AppUpdateIdentity.InstallerName(version)}"), InstallerBytes.Length, Convert.ToHexString(SHA256.HashData(InstallerBytes)));
    internal static string Metadata(string version = "1.0.3", string? digest = "valid", string? url = null, string? name = null, bool preview = false) => JsonSerializer.Serialize(new {
        tag_name="v"+version, draft=false, prerelease=preview, body="测试更新说明", assets=new[] { new {
            name=name ?? AppUpdateIdentity.InstallerName(version), state="uploaded", size=InstallerBytes.Length,
            browser_download_url=url ?? Release(version).DownloadUri.AbsoluteUri, digest=digest == "valid" ? "sha256:"+Release(version).Sha256 : digest
        } }
    });
    [Theory]
    [InlineData("1.0.10","1.0.9",true)] [InlineData("1.1.0","1.0.99",true)] [InlineData("2.0.0","1.9.9",true)]
    [InlineData("1.1.0","1.0.1",true)] [InlineData("1.1.1","1.1.0",true)] [InlineData("1.0.3","1.1.0",false)]
    [InlineData("1.0.1","1.0.2",false)] [InlineData("1.0.2","1.0.2",false)] [InlineData("1.0.3-beta","1.0.2",false)]
    [InlineData("1.0.3","bad",false)] [InlineData("65536.0.0","1.0.2",false)] [InlineData("01.0.3","1.0.2",false)]
    public void VersionsAreNumericAndNeverDowngrade(string a,string b,bool expected) => Assert.Equal(expected,AppUpdateIdentity.IsNewer(a,b));
    [Theory]
    [InlineData("https://example.com/Ph-Rank/The-Order/releases/download/v1.0.3/The-Order-1.0.3-Setup-x64.exe")]
    [InlineData("https://github.com/other/The-Order/releases/download/v1.0.3/The-Order-1.0.3-Setup-x64.exe")]
    [InlineData("http://github.com/Ph-Rank/The-Order/releases/download/v1.0.3/The-Order-1.0.3-Setup-x64.exe")]
    [InlineData("https://user@github.com/Ph-Rank/The-Order/releases/download/v1.0.3/The-Order-1.0.3-Setup-x64.exe")]
    [InlineData("https://github.com/Ph-Rank/The-Order/releases/download/v1.0.3/The-Order-1.0.3-Setup-x64.exe?changed=1")]
    public void ForeignOrUntrustedInstallersAreRejected(string url) => Assert.False(AppUpdateIdentity.ValidDownload(new(url),"1.0.3"));
    internal sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        { Assert.Null(request.Headers.Authorization); Assert.Equal("The-Order-Updater/1.0",request.Headers.UserAgent.ToString()); Requests.Add(request.RequestUri!); return send(request,cancellation); }
    }
    internal static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content=new StringContent(text,Encoding.UTF8,"application/json") };
    internal static Task<HttpResponseMessage> Response(HttpResponseMessage response) => Task.FromResult(response);
    [Fact]
    public async Task PublicFeedUsesFixedRepositoryDigestAndPlainNotes()
    {
        using var handler=new Handler((r,_)=>Response(Json(Metadata()))); using var feed=new GitHubUpdateFeed(new HttpClient(handler));
        var release=await feed.LatestAsync(default); Assert.Equal(Release(),release); Assert.Equal(GitHubUpdateFeed.LatestUri,Assert.Single(handler.Requests));
    }
    [Theory]
    [InlineData("preview")] [InlineData("source")] [InlineData("foreign")] [InlineData("bad-digest")] [InlineData("missing-hash")] [InlineData("invalid-json")]
    public async Task UnusableReleaseDoesNotBecomeAnInstall(string kind)
    {
        var text=kind switch { "preview"=>Metadata(preview:true), "source"=>Metadata(name:"source.zip"),"foreign"=>Metadata(url:"https://example.org/setup.exe"),
            "bad-digest"=>Metadata(digest:"sha256:bad"),"missing-hash"=>Metadata(digest:null),_=>"{" };
        using var handler=new Handler((_,_)=>Response(Json(text))); using var feed=new GitHubUpdateFeed(new HttpClient(handler));
        await Assert.ThrowsAsync<AppUpdateException>(()=>feed.LatestAsync(default));
    }
    [Fact]
    public async Task ChecksumFallbackMatchesExactlyAndRejectsDuplicates()
    {
        var release=Release(); var text=Metadata(digest:null); var node=System.Text.Json.Nodes.JsonNode.Parse(text)!;
        node["assets"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new {name="SHA256SUMS.txt",state="uploaded",browser_download_url="https://github.com/Ph-Rank/The-Order/releases/download/v1.0.3/SHA256SUMS.txt"})));
        using var handler=new Handler((r,_)=>Response(r.RequestUri==GitHubUpdateFeed.LatestUri?Json(node.ToJsonString()):new(HttpStatusCode.OK){Content=new StringContent(release.Sha256+"  "+AppUpdateIdentity.InstallerName(release.Version))}));
        using var feed=new GitHubUpdateFeed(new HttpClient(handler)); Assert.Equal(release,await feed.LatestAsync(default));
        Assert.Throws<AppUpdateException>(()=>GitHubUpdateFeed.ParseChecksum(release.Sha256+"  setup.exe\n"+release.Sha256+"  setup.exe","setup.exe"));
    }
    [Theory]
    [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task NetworkErrorsAreNotLatestVersion(int status)
    { using var feed=new GitHubUpdateFeed(new HttpClient(new Handler((_,_)=>Response(new((HttpStatusCode)status))))); await Assert.ThrowsAsync<AppUpdateException>(()=>feed.LatestAsync(default)); }
    [Fact]
    public async Task OfficialRedirectDownloadIsHashChecked()
    {
        var count=0; using var handler=new Handler((r,_)=> { count++; return Response(count==1?new(HttpStatusCode.Redirect){Headers={Location=new("https://release-assets.githubusercontent.com/test/package?token=synthetic")}}:new(HttpStatusCode.OK){Content=new ByteArrayContent(InstallerBytes)}); });
        using var feed=new GitHubUpdateFeed(new HttpClient(handler)); using var output=new MemoryStream();
        await feed.CopyInstallerAsync(Release(),output,new Progress<AppUpdateTransfer>(),default); Assert.Equal(InstallerBytes,output.ToArray()); Assert.Equal(2,handler.Requests.Count);
    }
    [Theory]
    [InlineData("foreign-redirect")] [InlineData("wrong-hash")] [InlineData("truncated")] [InlineData("oversize")]
    public async Task BadDownloadNeverReportsVerified(string kind)
    {
        using var feed=new GitHubUpdateFeed(new HttpClient(new Handler((_,_)=>Response(kind=="foreign-redirect"?new(HttpStatusCode.Redirect){Headers={Location=new("https://evil.example/setup.exe")}}:new(HttpStatusCode.OK){Content=new ByteArrayContent(kind=="truncated"?InstallerBytes[..3]:kind=="oversize"?[..InstallerBytes,9]:[77,90,0,0,0,0])}))));
        using var output=new MemoryStream(); await Assert.ThrowsAsync<AppUpdateException>(()=>feed.CopyInstallerAsync(Release(),output,new Progress<AppUpdateTransfer>(),default));
    }
    internal sealed class Fake : IAppUpdateFeed,IAppUpdatePackages,IAppUpdateInstaller
    {
        public AppUpdateRelease? Next=Release(); public Exception? Error; public Func<CancellationToken,Task>? Wait;
        public int Checks,Downloads; public AppUpdateAvailability Availability {get;set;}=new(true,"");
        public async Task<AppUpdateRelease?> LatestAsync(CancellationToken token){Checks++;if(Wait is not null)await Wait(token);if(Error is not null)throw Error;return Next;}
        public Task CopyInstallerAsync(AppUpdateRelease r,Stream s,IProgress<AppUpdateTransfer> p,CancellationToken t)=>Task.CompletedTask;
        public async Task<VerifiedAppUpdate> DownloadAsync(IAppUpdateFeed f,AppUpdateRelease r,IProgress<AppUpdateTransfer> p,CancellationToken t){Downloads++;if(Wait is not null)await Wait(t);if(Error is not null)throw Error;p.Report(new(3,6));return new(r,"synthetic/setup.exe");}
        public Task<AppUpdateReceipt?> ReadReceiptAsync(CancellationToken t)=>Task.FromResult<AppUpdateReceipt?>(null);
        public Task<IAppUpdateHandoff> PrepareAsync(VerifiedAppUpdate p,CancellationToken t)=>throw new NotSupportedException();
    }
    [Fact]
    public async Task SettingsCheckThenInstallCanRetryWithoutAnotherDownload()
    {
        var fake=new Fake(); using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2"); await vm.CheckAsync(); Assert.True(vm.CanInstall);
        var calls=0; await vm.InstallAsync(_=>{calls++;return Task.FromResult(false);}); Assert.True(vm.CanInstall); Assert.Equal(1,fake.Downloads);
        await vm.InstallAsync(_=>{calls++;return Task.FromResult(true);}); Assert.Equal(1,fake.Downloads); Assert.Equal(2,calls);
    }
    [Fact]
    public async Task OlderPublicReleaseCannotDowngradeUnpublishedCandidate()
    {var fake=new Fake{Next=Release("1.0.1")};using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");await vm.CheckAsync();Assert.False(vm.HasUpdate);await vm.InstallAsync(_=>throw new Exception());Assert.Equal(0,fake.Downloads);}
    [Fact]
    public async Task DuplicateClicksCancelAndFailureLeaveUsableSettings()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var fake=new Fake{Wait=async t=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);}};
        using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");var checking=vm.CheckAsync();await entered.Task;await vm.CheckAsync();Assert.Equal(1,fake.Checks);Assert.True(vm.CanCancel);vm.Cancel();await checking;Assert.True(vm.CanCheck);Assert.Contains("取消",vm.Message);
        fake.Wait=null;fake.Error=new AppUpdateException("网络失败");await vm.CheckAsync();Assert.Equal("网络失败",vm.Message);Assert.False(vm.CanInstall);Assert.True(vm.CanCheck);
    }
    [Fact]
    public async Task CanceledDownloadDoesNotCallShutdown()
    {
        var fake=new Fake();using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");await vm.CheckAsync();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);fake.Wait=async t=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);};
        var calls=0;var work=vm.InstallAsync(_=>{calls++;return Task.FromResult(true);});await entered.Task;vm.Cancel();await work;Assert.Equal(0,calls);Assert.True(vm.CanInstall);Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task DisposedSettingsIgnoreLateNetworkCompletion()
    {
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var wait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake=new Fake{Wait=async _=>{ready.TrySetResult();await wait.Task;}};var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");
        var checking=vm.CheckAsync();await ready.Task;vm.Dispose();wait.SetResult();await checking;Assert.Null(vm.Latest);Assert.False(vm.CanCheck);Assert.False(vm.CanInstall);
    }
    [Fact]
    public async Task IsolatedProfileCannotOfferInstallation()
    {var fake=new Fake{Availability=new(false,"独立资料")};using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");await vm.CheckAsync();Assert.True(vm.HasUpdate);Assert.False(vm.CanInstall);await vm.InstallAsync(_=>throw new Exception());Assert.Equal(0,fake.Downloads);}
    [Fact]
    public async Task AutomaticDiscoveryIsQuietAndNeverDownloads()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake=new Fake{Wait=async _=>{entered.TrySetResult();await finish.Task;}};
        using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");var before=vm.Message;
        var check=vm.CheckAutomaticallyAsync();await entered.Task;
        Assert.False(vm.IsBusy);Assert.True(vm.CanCheck);Assert.False(vm.CanCancel);Assert.Equal(before,vm.Message);
        await vm.CheckAutomaticallyAsync();Assert.Equal(1,fake.Checks);
        finish.SetResult();await check;
        Assert.True(vm.HasUpdate);Assert.True(vm.CanInstall);Assert.Equal(0,fake.Downloads);Assert.NotEmpty(vm.Highlights);
    }
    [Fact]
    public async Task AutomaticFailurePreservesKnownUpdateAndManualStatus()
    {
        var fake=new Fake();using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");await vm.CheckAsync();
        fake.Error=new AppUpdateException("网络失败");await vm.CheckAsync();var status=vm.Message;
        await vm.CheckAutomaticallyAsync();Assert.Equal(status,vm.Message);Assert.True(vm.HasUpdate);Assert.True(vm.CanInstall);Assert.Equal(0,fake.Downloads);
        fake.Error=null;fake.Next=Release("1.0.2");await vm.CheckAutomaticallyAsync();Assert.False(vm.HasUpdate);Assert.Contains("最新可用版本",vm.Message);
    }
    [Fact]
    public async Task ManualCheckSupersedesLateAutomaticResponse()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake=new Fake{Wait=async _=>{entered.TrySetResult();await finish.Task;}};
        using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");var background=vm.CheckAutomaticallyAsync();await entered.Task;
        fake.Wait=null;fake.Next=Release("1.0.4");await vm.CheckAsync();Assert.Equal("1.0.4",vm.Latest?.Version);
        fake.Next=Release("1.0.3");finish.SetResult();await background;
        Assert.Equal("1.0.4",vm.Latest?.Version);Assert.True(vm.CanCheck);Assert.True(vm.CanInstall);Assert.Equal(0,fake.Downloads);
    }
    [Fact]
    public async Task BackgroundCheckDoesNotInterruptExplicitDownload()
    {
        var fake=new Fake();using var vm=new AppUpdateViewModel(fake,fake,fake,"1.0.2");await vm.CheckAsync();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Wait=async t=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,t);};
        var install=vm.InstallAsync(_=>throw new Exception());await entered.Task;await vm.CheckAutomaticallyAsync();
        Assert.Equal(1,fake.Checks);Assert.True(vm.Downloading);vm.Cancel();await install;Assert.False(vm.IsBusy);
    }
    [Fact]
    public void MainChangesPreferMaintainerSectionAndExcludeDownloadInstructions()
    {
        var notes="# Version 1.0.3\n- [下载](https://example.test/setup.exe)\n## 更新内容\n- **识别**修复\n- 通过 `设置` 更新\n### 其他改动\n- [布局](https://example.test)改进\n## 安装与升级\n- 先下载 Setup\n";
        Assert.Equal("• 识别修复\n• 通过 设置 更新\n• 布局改进",AppUpdateNotes.Highlights(notes));
    }
    [Fact]
    public void MainChangesAreBoundedAndIgnoreFencedExamples()
    {
        var notes="## What's new\n```shell\n- fake command\n```\n"+string.Join("\n",Enumerable.Range(1,10).Select(n=>"- "+n+new string('x',300)));
        var lines=AppUpdateNotes.Highlights(notes).Split('\n');Assert.Equal(6,lines.Length);Assert.All(lines,line=>Assert.Equal(242,line.Length));Assert.DoesNotContain("fake",string.Join('\n',lines));
    }
    [Theory]
    [InlineData("", "这个版本没有提供主要改动说明。")]
    [InlineData("说明第一行\n说明第二行\n第三行", "说明第一行\n说明第二行")]
    [InlineData("~~~\n- 示例\n~~~", "这个版本没有提供主要改动说明。")]
    [InlineData("## Changelog\n1. First fix\n2) Second fix", "• First fix\n• Second fix")]
    public void MainChangesHandleMissingOrUnstructuredNotes(string notes,string expected)=>Assert.Equal(expected,AppUpdateNotes.Highlights(notes));
    private sealed class Handoff(List<string> events):IAppUpdateHandoff
    { public bool Committed;public void Commit(){Committed=true;events.Add("commit");}public void Dispose()=>events.Add(Committed?"release":"cancel"); }
    [Theory]
    [InlineData("ok")] [InlineData("edit-canceled")] [InlineData("prepare-failed")] [InlineData("save-failed")] [InlineData("drain-failed")]
    public async Task ShutdownCommitsOnlyAfterAllSavesAndNeverClosesOnFailure(string scenario)
    {
        var events=new List<string>();var handoff=new Handoff(events);var closed=false;
        async Task<bool> Run()=>await AppUpdateShutdown.RunAsync(()=>Task.FromResult(scenario!="edit-canceled"),
            ()=>{events.Add("prepare");if(scenario=="prepare-failed")throw new IOException();return Task.FromResult<IAppUpdateHandoff?>(handoff);},
            ()=>{events.Add("save");return Task.FromResult(scenario!="save-failed");},
            ()=>{events.Add("drain");if(scenario=="drain-failed")throw new IOException();return Task.CompletedTask;},()=>{events.Add("close");closed=true;});
        if(scenario is "prepare-failed" or "drain-failed")await Assert.ThrowsAsync<IOException>(Run);else Assert.Equal(scenario=="ok",await Run());
        Assert.Equal(scenario=="ok",closed);Assert.Equal(scenario=="ok",handoff.Committed);
        if(scenario=="ok")Assert.Equal(new[]{"prepare","save","drain","commit","close","release"},events);
        if(scenario is "save-failed" or "drain-failed")Assert.Equal("cancel",events[^1]);
    }
}
