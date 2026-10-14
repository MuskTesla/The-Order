using System.Net;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiWorkerClientTests
{
    private const string Access = "synthetic_worker_access_123456789", Refresh = "synthetic_worker_refresh_123456789";
    private static readonly Uri Service = BangumiOAuthDefaults.Service!;
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T08:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IBangumiCredentialStore
    {
        public BangumiCredential? Value;
        public int Writes;
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(Value?.WithSecrets((access, _) => access));
        public Task<BangumiCredential?> ReadCredentialAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(string value, CancellationToken token) => SaveCredentialAsync(BangumiCredential.Manual(value), token);
        public Task SaveCredentialAsync(BangumiCredential value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Value = value; Writes++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Value = null; Writes++; return Task.CompletedTask; }
    }
    private sealed class Accounts : IBangumiAccountProvider
    {
        public string Id = "123";
        public int Reads;
        public Task<BangumiAccount> ValidateAsync(string value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(new BangumiAccount(Id, "worker-fixture", "合成账号")); }
    }
    private sealed class OAuth(Clock clock) : IBangumiOAuth
    {
        public bool Configured => true;
        public Uri? Service => BangumiWorkerClientTests.Service;
        public int Refreshes, SignIns;
        public BangumiAuthorizationFailure? Failure;
        public Func<CancellationToken, Task>? Wait;
        public async Task<BangumiCredential> RefreshAsync(BangumiCredential value, CancellationToken token)
        { Refreshes++; if (Wait is not null) await Wait(token); if (Failure is { } failure) throw new BangumiAuthorizationException(failure); return New(); }
        public Task<BangumiCredential> SignInAsync(CancellationToken token) { SignIns++; return Task.FromResult(New()); }
        private BangumiCredential New() => BangumiCredential.OAuth(Access, Refresh, clock.Now.AddHours(1), Service!, "123");
    }
    private static (BangumiAuthorization Auth, Clock Clock, Store Store, OAuth OAuth, Accounts Accounts, BangumiAccessContext Access) Fixture()
    {
        var clock = new Clock(); var store = new Store(); var oauth = new OAuth(clock); var accounts = new Accounts(); var access = new BangumiAccessContext();
        return (new(store, accounts, access, oauth, clock), clock, store, oauth, accounts, access);
    }
    [Fact] public void CurrentClientIsBoundToTheExistingWorkersRootAndContainsNoAppSecret()
    { Assert.Equal("https://the-order-bangumi-auth.zyx2251721565.workers.dev/", Service.AbsoluteUri); Assert.True(BangumiOAuthProtocol.ValidService(Service)); }

    [Fact] public async Task ExpiredReaderBeforeTimerDoesNotPermanentlyDisableAutomaticRefresh()
    {
        var f = Fixture(); using var model = new BangumiSettingsViewModel(f.Auth);
        await model.InitializeAsync(); await model.SignInAsync(); f.Clock.Now += TimeSpan.FromHours(2);
        Assert.False(f.Access.Capture().Authorized); Assert.True(f.Access.Rejected);
        await model.RefreshIfDueAsync(); Assert.Equal(1, f.OAuth.Refreshes); Assert.True(f.Access.Capture().Authorized);
        Assert.False(f.Access.Rejected); Assert.Contains("worker-fixture", model.Status);
    }
    [Fact] public async Task RevokedRefreshTokenStopsRepeatedAutomaticAttemptsUntilReconnect()
    {
        var f = Fixture(); using var model = new BangumiSettingsViewModel(f.Auth);
        await model.SignInAsync(); var previous = f.Store.Value; f.Clock.Now += TimeSpan.FromMinutes(56);
        f.OAuth.Failure = BangumiAuthorizationFailure.Unauthorized;
        await model.RefreshIfDueAsync(); await model.RefreshIfDueAsync();
        Assert.Equal(1, f.OAuth.Refreshes); Assert.Same(previous, f.Store.Value); Assert.True(f.Auth.ReauthorizationRequired);
        Assert.False(f.Auth.NeedsRefresh); Assert.Contains("重新连接", model.Status); Assert.Equal("重新连接 Bangumi", model.ConnectionAction);
        f.OAuth.Failure = null; await model.SignInAsync(); Assert.False(f.Auth.ReauthorizationRequired);
        Assert.True(f.Access.Capture().Authorized); Assert.Contains("@worker-fixture", model.Status);
    }
    [Fact] public async Task RestartRestoresAccountIdentityWithoutRewritingCredentials()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); var before = f.Store.Value; var writes = f.Store.Writes;
        var restored = new BangumiAuthorization(f.Store, f.Accounts, new(), f.OAuth, f.Clock);
        await restored.InitializeAsync(default); Assert.Same(before, f.Store.Value); Assert.Equal(writes, f.Store.Writes);
        Assert.Equal("123", restored.Account!.Id); Assert.Equal("worker-fixture", restored.Account.Username); Assert.Equal(0, f.OAuth.Refreshes);
    }
    [Fact] public async Task WrongAccountOnRestartCannotBePublishedOrRepeatedlyRefreshed()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); var previous = f.Store.Value; f.Accounts.Id = "999";
        var access = new BangumiAccessContext(); var restored = new BangumiAuthorization(f.Store, f.Accounts, access, f.OAuth, f.Clock);
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => restored.InitializeAsync(default));
        Assert.False(access.Capture().Authorized); Assert.True(restored.ReauthorizationRequired); Assert.False(restored.NeedsRefresh);
        Assert.Same(previous, f.Store.Value);
    }
    [Fact] public async Task ParallelRefreshesUseOneRotatingTokenExchange()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Clock.Now += TimeSpan.FromMinutes(56);
        await Task.WhenAll(f.Auth.RefreshAsync(default), f.Auth.RefreshAsync(default), f.Auth.RefreshAsync(default));
        Assert.Equal(1, f.OAuth.Refreshes); Assert.True(f.Access.Capture().Authorized);
    }
    [Fact] public async Task DisconnectQueuedBehindLateRefreshFinishesAnonymousWithNoSavedCredential()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Clock.Now += TimeSpan.FromMinutes(56);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OAuth.Wait = _ => { entered.SetResult(); return release.Task; };
        var refreshing = f.Auth.RefreshAsync(default); await entered.Task; var clearing = f.Auth.ClearAsync(default);
        release.SetResult(); await Task.WhenAll(refreshing, clearing);
        Assert.Null(f.Store.Value); Assert.False(f.Auth.HasSavedToken); Assert.False(f.Access.Capture().Authorized);
    }
    [Fact] public async Task CancelledLateLoginCannotReplaceTheOldManualAccount()
    {
        var f = Fixture(); await f.Auth.SaveAsync(Access, default); var previous = f.Store.Value;
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Auth.SignInAsync(stop.Token));
        Assert.Same(previous, f.Store.Value); Assert.True(f.Access.Capture().Authorized);
    }
    [Fact] public async Task RepeatedUiClicksOpenOneLoginAndCancelDrainsWithoutPublishing()
    {
        var f = Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new WaitingOAuth(entered); using var model = new BangumiSettingsViewModel(new(f.Store, f.Accounts, f.Access, waiting, f.Clock));
        var first = model.SignInAsync(); await entered.Task; await model.SignInAsync(); await model.DrainAsync(); await first;
        Assert.Equal(1, waiting.Calls); Assert.Null(f.Store.Value); Assert.False(model.Busy); Assert.Contains("取消", model.Message);
    }
    private sealed class WaitingOAuth(TaskCompletionSource entered) : IBangumiOAuth
    {
        public int Calls;
        public bool Configured => true; public Uri? Service => BangumiWorkerClientTests.Service;
        public async Task<BangumiCredential> SignInAsync(CancellationToken token)
        { Calls++; entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
        public Task<BangumiCredential> RefreshAsync(BangumiCredential value, CancellationToken token) => throw new InvalidOperationException();
    }
    [Fact] public async Task TransientServiceFailureRetainsValidAccountAndCanRetry()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); var previous = f.Store.Value; f.Clock.Now += TimeSpan.FromMinutes(56);
        f.OAuth.Failure = BangumiAuthorizationFailure.ServiceUnavailable;
        await Assert.ThrowsAsync<BangumiAuthorizationException>(() => f.Auth.RefreshAsync(default));
        Assert.Same(previous, f.Store.Value); Assert.True(f.Access.Capture().Authorized); Assert.False(f.Auth.ReauthorizationRequired);
        f.OAuth.Failure = null; await f.Auth.RefreshAsync(default); Assert.True(f.Access.Capture().Authorized);
    }
    [Theory]
    [InlineData(BangumiAuthorizationFailure.Offline)]
    [InlineData(BangumiAuthorizationFailure.Timeout)]
    [InlineData(BangumiAuthorizationFailure.ServiceUnavailable)]
    public async Task ExpiredOAuthWithTransientFailureWaitsForRenewalInsteadOfDemandingNewConsent(BangumiAuthorizationFailure failure)
    {
        var f = Fixture(); using var model = new BangumiSettingsViewModel(f.Auth);
        await model.SignInAsync(); var previous = f.Store.Value; f.Clock.Now += TimeSpan.FromHours(2);
        Assert.False(f.Access.Capture().Authorized); f.OAuth.Failure = failure;
        await model.RefreshIfDueAsync();
        Assert.Same(previous, f.Store.Value); Assert.True(f.Auth.NeedsRefresh); Assert.False(f.Auth.ReauthorizationRequired);
        Assert.Contains("自动续期", model.Status); Assert.DoesNotContain("重新连接", model.Status);
        if (failure == BangumiAuthorizationFailure.ServiceUnavailable) Assert.DoesNotContain("重新连接", model.Message);
        f.OAuth.Failure = null; await model.RefreshIfDueAsync();
        Assert.True(f.Access.Capture().Authorized); Assert.Contains("@worker-fixture", model.Status);
    }
    [Fact] public async Task RejectedManualTokenStillClearlyRequiresNewAuthorization()
    {
        var f = Fixture(); using var model = new BangumiSettingsViewModel(f.Auth);
        await model.SaveAsync(Access); f.Access.Reject(f.Access.Capture());
        Assert.Contains("重新连接", model.Status); Assert.False(f.Auth.NeedsRefresh);
    }
    [Fact] public async Task MetadataRejectionTriggersOAuthRenewalEvenBeforeScheduledExpiry()
    {
        var f = Fixture(); await f.Auth.SignInAsync(default); f.Access.Reject(f.Access.Capture());
        Assert.True(f.Auth.NeedsRefresh); await f.Auth.RefreshAsync(default);
        Assert.Equal(1, f.OAuth.Refreshes); Assert.True(f.Access.Capture().Authorized);
    }
    [Fact] public async Task WorkerUnavailableErrorIsExplicitAndNeverDisplaysTheReturnedBody()
    {
        using var client = new HttpClient(new Unavailable());
        var provider = new BangumiOAuthProvider(client, new NeverBrowser(), Service);
        var error = await Assert.ThrowsAsync<BangumiAuthorizationException>(() => provider.SignInAsync(default));
        Assert.Equal(BangumiAuthorizationFailure.ServiceUnavailable, error.Failure); Assert.Contains("授权服务", error.Message);
        Assert.DoesNotContain(Access, error.ToString()); Assert.DoesNotContain(Refresh, error.ToString());
    }
    [Fact] public async Task InvalidAuthorizationCodeIsNotReportedAsUserDenial()
    {
        using var client = new HttpClient(new InvalidCode());
        var provider = new BangumiOAuthProvider(client, new NeverBrowser(), Service);
        var error = await Assert.ThrowsAsync<BangumiAuthorizationException>(() => provider.SignInAsync(default));
        Assert.Equal(BangumiAuthorizationFailure.Unauthorized, error.Failure); Assert.Contains("无效", error.Message);
    }
    private sealed class NeverBrowser : IBangumiOAuthBrowser
    { public Task OpenAsync(Uri uri, CancellationToken token) => throw new InvalidOperationException(); }
    private sealed class Unavailable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(Access+Refresh, Encoding.UTF8, "application/json") });
    }
    private sealed class InvalidCode : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
}
