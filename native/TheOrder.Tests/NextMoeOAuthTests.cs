using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

public sealed class NextMoeOAuthTests
{
    private sealed class Browser(Func<Uri, Task> open) : IOAuthSystemBrowser
    {
        internal Task Submitted = Task.CompletedTask;
        internal Uri? Authorization;
        public Task OpenAsync(Uri uri, CancellationToken cancellation) { Authorization = uri; Submitted = open(uri); return Task.CompletedTask; }
    }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]), StringComparer.Ordinal);
    [Theory] [InlineData("state")] [InlineData("duplicate")] [InlineData("path")] [InlineData("method")] [InlineData("host")] [InlineData("issuer")]
    public async Task InvalidCallbackCannotConsumeValidCode(string kind)
    {
        int rejected = 0;
        var browser = new Browser(async uri =>
        {
            var query = Query(uri); var redirect = new Uri(query["redirect_uri"]);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var badState = kind == "state" ? "wrong-state" : query["state"];
            var path = kind == "path" ? "/other" : "/callback";
            var queryPart = "?state=" + Uri.EscapeDataString(badState) + "&code=synthetic-code" + (kind == "duplicate" ? "&state=duplicate" : kind == "issuer" ? "&iss=https%3A%2F%2Fexample.com" : "");
            using var bad = new HttpRequestMessage(kind == "method" ? HttpMethod.Post : HttpMethod.Get, new Uri(redirect, path + queryPart));
            if (kind == "host") bad.Headers.Host = "localhost:" + redirect.Port;
            using var rejectedResponse = await client.SendAsync(bad); Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode); rejected++;
            using var accepted = await client.GetAsync(redirect + "?state=" + Uri.EscapeDataString(query["state"]) + "&code=synthetic-valid-code");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); Assert.DoesNotContain("synthetic-valid-code", await accepted.Content.ReadAsStringAsync());
        });
        var login = new WindowsOAuthLogin(browser, TimeSpan.FromSeconds(10));
        var code = await login.AuthorizeAsync(NextMoeOAuthIdentity.ClientId, NextMoeOAuthIdentity.CatalogScopes, default);
        await browser.Submitted; Assert.Equal(1, rejected); Assert.Equal("synthetic-valid-code", code.Code);
        var parameters = Query(browser.Authorization!);
        Assert.Equal("127.0.0.1", code.Redirect.Host); Assert.Equal("/callback", code.Redirect.AbsolutePath); Assert.NotEqual(80, code.Redirect.Port);
        Assert.InRange(code.Verifier.Length, 43, 128); Assert.Equal("S256", parameters["code_challenge_method"]);
        Assert.Equal(WindowsOAuthLogin.UrlSafe(SHA256.HashData(Encoding.ASCII.GetBytes(code.Verifier))), parameters["code_challenge"]);
        Assert.Equal(code.Redirect.AbsoluteUri, parameters["redirect_uri"]); Assert.Equal("openid profile catalog:read", parameters["scope"]);
        Assert.DoesNotContain(code.Code, code.ToString()); Assert.DoesNotContain(code.Verifier, code.ToString());
    }
    [Fact] public async Task DeniedAuthorizationClosesLoopbackListener()
    {
        var browser = new Browser(async uri => { var q = Query(uri); using var client = new HttpClient(); using var result = await client.GetAsync(q["redirect_uri"] + "?state=" + q["state"] + "&error=access_denied"); Assert.Equal(HttpStatusCode.OK, result.StatusCode); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsOAuthLogin(browser).AuthorizeAsync(NextMoeOAuthIdentity.ClientId, NextMoeOAuthIdentity.CatalogScopes, default));
        await browser.Submitted;
        await AssertClosedPort(new Uri(Query(browser.Authorization!)["redirect_uri"]));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancelOrTimeoutClosesListener(bool cancel)
    {
        var browser = new Browser(_ => Task.CompletedTask); using var stop = new CancellationTokenSource();
        var task = new WindowsOAuthLogin(browser, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50)).AuthorizeAsync(NextMoeOAuthIdentity.ClientId, NextMoeOAuthIdentity.CatalogScopes, stop.Token);
        if (cancel) stop.Cancel();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Equal(OnlineFailure.Timeout, (await Assert.ThrowsAsync<OnlineException>(() => task)).Failure);
        await AssertClosedPort(new Uri(Query(browser.Authorization!)["redirect_uri"]));
    }
    private static async Task AssertClosedPort(Uri redirect)
    {
        using var socket = new TcpClient(AddressFamily.InterNetwork);
        var error = await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, redirect.Port).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
    }
    [Fact] public async Task EachAuthorizationUsesIndependentRandomStateAndVerifier()
    {
        var one = new Browser(_ => Task.CompletedTask); var two = new Browser(_ => Task.CompletedTask);
        using var stop = new CancellationTokenSource(); var a = new WindowsOAuthLogin(one).AuthorizeAsync(NextMoeOAuthIdentity.ClientId, NextMoeOAuthIdentity.CatalogScopes, stop.Token);
        var b = new WindowsOAuthLogin(two).AuthorizeAsync(NextMoeOAuthIdentity.ClientId, NextMoeOAuthIdentity.CatalogScopes, stop.Token);
        Assert.NotEqual(Query(one.Authorization!)["state"], Query(two.Authorization!)["state"]); Assert.NotEqual(Query(one.Authorization!)["code_challenge"], Query(two.Authorization!)["code_challenge"]);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExchangeAndRefreshPinEndpointsAndParseUserinfo(bool refresh)
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            calls++; Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("account.nextmoe.com", request.RequestUri.Host);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/oauth/token", request.RequestUri.AbsolutePath); Assert.Null(request.Headers.Authorization);
                var form = await request.Content!.ReadAsStringAsync(); Assert.DoesNotContain("client_secret", form);
                Assert.Contains(refresh ? "grant_type=refresh_token" : "grant_type=authorization_code", form);
                if (!refresh) { Assert.Contains("code_verifier=synthetic-verifier", form); Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A12345%2Fcallback", form); }
                return Json("{\"token_type\":\"Bearer\",\"access_token\":\"synthetic-access\",\"refresh_token\":\"synthetic-refresh\",\"expires_in\":900,\"scope\":\"openid profile catalog:read\"}");
            }
            Assert.Equal("/api/v1/oauth/userinfo", request.RequestUri.AbsolutePath); Assert.Equal("synthetic-access", request.Headers.Authorization!.Parameter);
            return Json("{\"sub\":\"42\",\"name\":\"合成用户\"}");
        }));
        var oauth = new NextMoeOAuth(client);
        var value = refresh ? await oauth.RefreshAsync(NextMoeOAuthIdentity.ClientId, "synthetic-old-refresh", default)
            : await oauth.ExchangeAsync(NextMoeOAuthIdentity.ClientId, new("synthetic-code", "synthetic-verifier", new("http://127.0.0.1:12345/callback")), default);
        Assert.Equal(2, calls); Assert.Equal("42", value.Account.Id); Assert.DoesNotContain(value.AccessToken, value.ToString());
    }
    [Theory] [InlineData(400, OnlineFailure.LoginRequired)] [InlineData(401, OnlineFailure.LoginRequired)] [InlineData(302, OnlineFailure.RedirectBlocked)] [InlineData(500, OnlineFailure.Server)]
    public async Task TokenErrorsAndRedirectsNeverLeakDetailsOrFollow(int status, OnlineFailure failure)
    {
        int calls = 0; using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{\"error_description\":\"synthetic-secret-diagnostic\"}", status)); }));
        var error = await Assert.ThrowsAsync<OnlineException>(() => new NextMoeOAuth(client).RefreshAsync(NextMoeOAuthIdentity.ClientId, "synthetic-refresh", default));
        Assert.Equal(failure, error.Failure); Assert.DoesNotContain("synthetic-secret", error.ToString()); Assert.Equal(1, calls);
    }
}
