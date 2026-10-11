using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class OnlineTransportTests
{
    internal sealed class Credential(string? value = "synthetic-only") : ICredentialProvider, ICredentialSettings
    {
        public string? Value = value;
        public Task<string?> ReadAsync(CancellationToken c) { c.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
        public Task<bool> HasCredentialAsync(CancellationToken c) => Task.FromResult(Value is not null);
        public Task SaveAsync(string secret, CancellationToken c) { Value = secret; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken c) { Value = null; return Task.CompletedTask; }
    }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    internal static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static JsonDocument Oracle() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/online-oracle.json")));
    public static IEnumerable<object[]> Cases() { using var doc = Oracle(); return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! }).ToArray(); }
    [Theory, MemberData(nameof(Cases)), Trait("Category", "OnlineDifferential")]
    public async Task CapturedElectronContractWithDocumentedNativePresentationChanges(string name)
    {
        using var oracle = Oracle(); var vector = oracle.RootElement.GetProperty("cases").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
        var calls = new List<object>(); var waits = new List<double>(); int index = 0;
        using var http = new HttpClient(new Handler((request, cancellation) =>
        {
            var uri = request.RequestUri!;
            calls.Add(new { path = uri.AbsolutePath, query = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1])), authorizationPresent = request.Headers.Authorization is not null });
            var response = vector.GetProperty("responses")[index++];
            if (response.TryGetProperty("drop", out _)) throw new HttpRequestException("untrusted synthetic exception");
            int status = response.GetProperty("status").GetInt32();
            var body = status == 204 ? "" : response.TryGetProperty("oversized", out _) ? new string('x', CatalogTransport.MaximumBytes + 1) : response.TryGetProperty("raw", out var raw) ? raw.GetString()! : response.GetProperty("body").GetRawText();
            var result = Json(body, status);
            if (response.TryGetProperty("headers", out var headers)) foreach (var header in headers.EnumerateObject()) result.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            return Task.FromResult(result);
        }));
        var transport = new CatalogTransport(http, new Credential(), new("https://api.nextmoe.dev/v2/"), delay: (duration, _) => { waits.Add(duration.TotalMilliseconds); return Task.CompletedTask; });
        var input = vector.GetProperty("input");
        var client = new NextMoeCatalog(transport, !input.TryGetProperty("includeExplicit", out var explicitValue) || explicitValue.GetBoolean(), () => DateTimeOffset.Parse(oracle.RootElement.GetProperty("fixed").GetString()!));
        var expected = vector.GetProperty("expected");
        async Task<object> Run() => vector.GetProperty("operation").GetString() == "search"
            ? await client.SearchAsync(input.GetProperty("query").GetString()!, input.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null, default)
            : await client.DetailsAsync(input.TryGetProperty("id", out var id) ? id.GetString()! : "86", true, default);
        if (expected.TryGetProperty("error", out var error))
        {
            var failure = await Assert.ThrowsAsync<OnlineException>(Run);
            Assert.Equal(error.GetString() switch { "INVALID_RESPONSE" or "INVALID_JSON" or "HTTP_ERROR" => OnlineFailure.InvalidResponse, "RESPONSE_TOO_LARGE" => OnlineFailure.ResponseTooLarge,
                "NETWORK_ERROR" => OnlineFailure.Offline, "MERGE_LOOP" => OnlineFailure.MergeLoop, "MERGE_LIMIT" => OnlineFailure.MergeLimit, "REDIRECT_BLOCKED" => OnlineFailure.RedirectBlocked,
                "INVALID_API_KEY" => OnlineFailure.InvalidCredential, "RATE_LIMITED" or "QUOTA_EXCEEDED" => OnlineFailure.RateLimited,
                _ => name == "status-400" ? OnlineFailure.InvalidRequest : name == "status-403" ? OnlineFailure.Forbidden : name == "status-404" ? OnlineFailure.NotFound : name == "status-408" ? OnlineFailure.Timeout : OnlineFailure.Server }, failure.Failure);
        }
        else
        {
            var actual = JsonSerializer.SerializeToNode(await Run(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            var wanted = JsonNode.Parse(expected.GetProperty("value").GetRawText())!;
            ProjectNativeLinks(wanted);
            // v21 no longer substitutes an arbitrary gallery scan when the
            // declared primary is filtered. Keep the Electron capture immutable.
            if (name == "detail-explicit-filter") wanted["coverUrl"] = null;
            RemoveOptional(actual, wanted); Assert.True(JsonNode.DeepEquals(wanted, actual), $"Mapping mismatch: {name}\nExpected: {wanted}\nActual: {actual}");
        }
        // Preserve the captured Electron oracle. Native details deliberately request the additional
        // presentation blocks; pagination, auth, ordering, retries and all other parameters stay exact.
        var expectedCalls = JsonNode.Parse(vector.GetProperty("calls").GetRawText())!.AsArray();
        foreach (var call in expectedCalls.OfType<JsonObject>())
            if (call["path"]!.GetValue<string>().StartsWith("/v2/catalog/works/", StringComparison.Ordinal))
                call["query"]!["include"] = "titles,refs,relations,links,intros,covers,screenshots,companies,ratings,tags,characters,credits,engines,series";
        Assert.True(JsonNode.DeepEquals(expectedCalls, JsonSerializer.SerializeToNode(calls)), $"Request mismatch: {name}");
        Assert.Equal(vector.GetProperty("delays").EnumerateArray().Select(v => v.GetDouble()), waits);
    }
    // KunGal now routes every catalog work by its own ID, even without a claim. This is
    // a mapping projection over the immutable historical oracle. All stored facts remain compared.
    private static void ProjectNativeLinks(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("originalTitle") && obj["id"] is JsonValue id)
                obj["webUrl"] = "https://www.kungal.com/galgame/" + id.GetValue<string>();
            foreach (var pair in obj) ProjectNativeLinks(pair.Value);
        }
        else if (node is JsonArray array) foreach (var item in array) ProjectNativeLinks(item);
    }
    private static void RemoveOptional(JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonObject a && expected is JsonObject e) { foreach (var key in a.Select(k => k.Key).ToArray()) { if (!e.ContainsKey(key) && a[key] is null) a.Remove(key); else RemoveOptional(a[key], e[key]); } }
        else if (actual is JsonArray aa && expected is JsonArray ee) for (int i = 0; i < Math.Min(aa.Count, ee.Count); i++) RemoveOptional(aa[i], ee[i]);
    }
    [Fact] public void OracleIsFresh()
    {
        using var doc = Oracle(); var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "electron/nextmoe.ts"))) root = root.Parent;
        Assert.NotNull(root); Assert.Equal(doc.RootElement.GetProperty("sourceSha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root!.FullName, "electron/nextmoe.ts")))).ToLowerInvariant());
    }
    [Theory][InlineData(null, OnlineFailure.NoCredential)][InlineData("unsafe\nheader", OnlineFailure.InvalidCredential)]
    public async Task CredentialErrorsNeverSend(string? secret, OnlineFailure expected)
    {
        int sent = 0; using var http = new HttpClient(new Handler((_, _) => { sent++; return Task.FromResult(Json("{}")); }));
        var client = new NextMoeCatalog(new(http, new Credential(secret), new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(expected, (await Assert.ThrowsAsync<OnlineException>(() => client.TestConnectionAsync(default))).Failure); Assert.Equal(0, sent);
    }
    [Fact] public async Task DeadlineCoversSearchAndRetryAndUserCancellationRemainsCancellation()
    {
        int sent = 0;
        using var http = new HttpClient(new Handler(async (_, token) => { sent++; await Task.Delay(80, token); return Json(sent == 1 ? "{\"object\":\"list\",\"items\":[{\"object\":\"search_result\",\"target_object\":\"work\",\"id\":\"86\"}]}" : "{\"object\":\"list\",\"items\":[]}"); }));
        var client = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/"), timeout: TimeSpan.FromMilliseconds(120)));
        Assert.Equal(OnlineFailure.Timeout, (await Assert.ThrowsAsync<OnlineException>(() => client.SearchAsync("x", null, default))).Failure);
        using var cancel = new CancellationTokenSource(10); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync("x", null, cancel.Token));
    }
    [Fact] public async Task SlowStreamingAndOversizedStreamingAreBoundedAndDisposed()
    {
        var slow = new ProbeStream(true); using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(slow) })));
        var client = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/"), timeout: TimeSpan.FromMilliseconds(50)));
        Assert.Equal(OnlineFailure.Timeout, (await Assert.ThrowsAsync<OnlineException>(() => client.DetailsAsync("86", true, default))).Failure); Assert.True(slow.Disposed);
        var huge = new ProbeStream(false); using var content = new StreamContent(huge);
        Assert.Equal(OnlineFailure.ResponseTooLarge, (await Assert.ThrowsAsync<OnlineException>(() => CatalogTransport.ReadBoundedAsync(content, 1000, default))).Failure);
        Assert.InRange(huge.ReadBytes, 1001, 1001); Assert.True(huge.Disposed);
    }
    internal sealed class ProbeStream(bool slow) : Stream
    {
        public bool Disposed; public int ReadBytes;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) { if (slow) await Task.Delay(Timeout.Infinite, cancellation); buffer.Span.Fill(32); ReadBytes += buffer.Length; return buffer.Length; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    [Fact] public async Task RedactionDoesNotEchoCredentialPayloadQueryOrException()
    {
        const string secret = "synthetic-secret-marker"; var events = new List<NetworkEvent>();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"detail\":\"synthetic-secret-marker\",\"code\":\"synthetic-secret-marker\"}", 401))));
        var client = new NextMoeCatalog(new(http, new Credential(secret), new("https://api.nextmoe.dev/v2/"), events.Add));
        var error = await Assert.ThrowsAsync<OnlineException>(() => client.SearchAsync(secret, null, default));
        Assert.DoesNotContain(secret, error.ToString()); Assert.DoesNotContain(secret, JsonSerializer.Serialize(events)); Assert.Single(events);
    }
    [Fact] public async Task DpapiCredentialRoundtripClearAndCorruptionStayIsolated()
    {
        using var profile = new TransactionProfile(); var store = new IsolatedCredentialStore(new(profile.Root));
        const string secret = "only-a-synthetic-dpapi-test";
        Assert.False(await store.HasCredentialAsync(default)); await store.SaveAsync(secret, default); Assert.Equal(secret, await store.ReadAsync(default));
        var path = Path.Combine(profile.Root, "native-online-key.dpapi"); Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        File.WriteAllBytes(path, [1, 2, 3]); Assert.Equal(OnlineFailure.CredentialStorageCorrupt, (await Assert.ThrowsAsync<OnlineException>(() => store.ReadAsync(default))).Failure);
        await store.ClearAsync(default); Assert.Null(await store.ReadAsync(default)); Assert.False(File.Exists(Path.Combine(profile.Root, "settings.json")));
    }
    [Fact] public async Task OlderDetailsCannotPoisonCacheAfterNewerRefreshOrClear()
    {
        var waiting = new TaskCompletionSource<HttpResponseMessage>(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => ++calls == 1 ? waiting.Task : Task.FromResult(Json("{\"object\":\"work\",\"id\":\"86\",\"display_name\":\"new\"}"))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        var old = catalog.DetailsAsync("86", true, default); await catalog.DetailsAsync("86", true, default);
        waiting.SetResult(Json("{\"object\":\"work\",\"id\":\"86\",\"display_name\":\"old\"}")); await old;
        Assert.Equal("new", (await catalog.DetailsAsync("86", false, default)).Title); Assert.Equal(2, calls);
        catalog.ClearCache(); await catalog.DetailsAsync("86", false, default); Assert.Equal(3, calls);
    }
    [Fact] public async Task DetailsCacheIsBoundedExpiresAndSearchNeverFetchesEachDetail()
    {
        int calls = 0; var clock = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler((request, _) => { calls++; return Task.FromResult(Json("{\"object\":\"work\",\"id\":\"" + request.RequestUri!.Segments.Last() + "\"}")); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")), clock: () => clock);
        for (int i = 1; i <= 33; i++) await catalog.DetailsAsync(i.ToString(), false, default);
        await catalog.DetailsAsync("33", false, default); Assert.Equal(33, calls);
        await catalog.DetailsAsync("1", false, default); Assert.Equal(34, calls);
        clock = clock.AddMinutes(6); await catalog.DetailsAsync("33", false, default); Assert.Equal(35, calls);
    }
    [Theory][InlineData("0.75", 750)][InlineData("600", -1)]
    public async Task RetryAfterUsesSharedBudgetAndFractionalSeconds(string value, int expectedWait)
    {
        int calls = 0; var waits = new List<double>();
        using var http = new HttpClient(new Handler((_, _) => { calls++; var response = calls == 1 ? Json("{\"code\":\"RATE_LIMITED\"}", 429) : Json("{\"object\":\"list\",\"items\":[]}"); response.Headers.TryAddWithoutValidation("Retry-After", value); return Task.FromResult(response); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/"), delay: (time, _) => { waits.Add(time.TotalMilliseconds); return Task.CompletedTask; }));
        if (expectedWait < 0) { Assert.Equal(OnlineFailure.RateLimited, (await Assert.ThrowsAsync<OnlineException>(() => catalog.TestConnectionAsync(default))).Failure); Assert.Empty(waits); Assert.Equal(1, calls); }
        else { await catalog.TestConnectionAsync(default); Assert.Equal(expectedWait, Assert.Single(waits)); Assert.Equal(2, calls); }
    }
    [Fact] public async Task InvalidUtf8AndMalformedJsonNeverRetry()
    {
        int calls = 0; using var http = new HttpClient(new Handler((_, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([123, 34, 97, 34, 58, 34, 255, 34, 125]) }; response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response); }));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidResponse, (await Assert.ThrowsAsync<OnlineException>(() => catalog.TestConnectionAsync(default))).Failure); Assert.Equal(1, calls);
    }
}
