using System.Net;
using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class BangumiEpisodeAuthorizationTests
{
    private sealed class Store : IBangumiCredentialStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>("Synthetic_Episode_Authorization_123456789");
        public Task SaveAsync(string token, CancellationToken cancellation) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellation) => Task.CompletedTask;
    }
    private sealed class Accounts : IBangumiAccountProvider
    {
        public Task<BangumiAccount> ValidateAsync(string token, CancellationToken cancellation) => Task.FromResult(new BangumiAccount("1", "test", "Test"));
    }
    [Theory]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=0")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=876&limit=50&offset=10000")]
    public async Task OnlyCanonicalBoundedEpisodePagingCanCarryCredentials(string url)
    {
        var access = new BangumiAccessContext(); var auth = new BangumiAuthorization(new Store(), new Accounts(), access);
        await auth.InitializeAsync(default);
        using var request = new HttpRequestMessage(HttpMethod.Get, url); access.Capture().Authorize(request);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
    }
    [Theory]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=200&offset=0")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=1")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=10050")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=00")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&subject_id=876&limit=50&offset=0")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=0&token=anything")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=%35%31&limit=50&offset=0")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=0&limit=50&offset=0")]
    [InlineData("https://api.bgm.tv/v0/subjects/51?subject_id=51&limit=50&offset=0")]
    [InlineData("https://api.bgm.tv/v0/episodes?subject_id=51&limit=50&offset=0#fragment")]
    public async Task UnexpectedQueryNeverReceivesToken(string url)
    {
        var access = new BangumiAccessContext(); var auth = new BangumiAuthorization(new Store(), new Accounts(), access);
        await auth.InitializeAsync(default);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Assert.Throws<BangumiAuthorizationException>(() => access.Capture().Authorize(request));
        Assert.Null(request.Headers.Authorization);
    }
}
