using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiEpisodeExpiryTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store(BangumiCredential value) : IBangumiCredentialStore
    {
        public Task<BangumiCredential?> ReadCredentialAsync(CancellationToken token) => Task.FromResult<BangumiCredential?>(value);
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult<string?>(value.WithSecrets((access, _) => access));
        public Task SaveAsync(string input, CancellationToken token) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Account : IBangumiAccountProvider
    { public Task<BangumiAccount> ValidateAsync(string token, CancellationToken cancellation) => Task.FromResult(new BangumiAccount("1", "test", "Test")); }
    private sealed class Provider(Clock clock, TaskCompletionSource gate) : IBangumiEpisodesProvider
    {
        public int Calls;
        public async Task<BangumiEpisodePage> FetchEpisodesAsync(string id, int offset, CancellationToken token)
        { Calls++; if (Calls == 1) await gate.Task; return new(id, offset, 0, [], clock.Now); }
    }
    [Fact] public async Task ExpiryDuringReadCannotPublishAuthorizedPageEvenWhenNoOtherRequestCapturedAccess()
    {
        var clock = new Clock(); var access = new BangumiAccessContext();
        var credential = BangumiCredential.OAuth("Synthetic_Episodes_Access_123456789", "Synthetic_Episodes_Refresh_123456789", clock.Now.AddHours(1), new("https://auth.example/"), "1");
        var auth = new BangumiAuthorization(new Store(credential), new Account(), access, clock: clock);
        await auth.InitializeAsync(default); Assert.True(access.Capture().Authorized);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var provider = new Provider(clock, gate);
        var reader = new BangumiEpisodesService(provider, () => clock.Now, access);
        var pending = reader.LoadAsync("51", 0, false, default);
        clock.Now = clock.Now.AddHours(2); gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False((await reader.LoadAsync("51", 0, false, default)).FromCache); Assert.Equal(2, provider.Calls);
    }
}
