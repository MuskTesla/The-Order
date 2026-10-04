using System.Net;
using System.Text;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.WinUI.Startup;

// Explicit DIAGNOSTICS + ONLINE_FIXTURE opt-in only. The normal client never installs this handler.
// Exercises the real transport/parser/VM/image decoder against synthetic HTTP responses, without networking.
public sealed class SyntheticOnline : HttpMessageHandler, ICredentialProvider, ICredentialSettings
{
    public HttpClient Client { get; }
    private readonly bool slowConnection;
    public int DetailRequests { get; private set; }
    public SyntheticOnline(bool slowConnection = false) { this.slowConnection = slowConnection; Client = new(this, false) { Timeout = Timeout.InfiniteTimeSpan }; }
    protected override void Dispose(bool disposing) { if (disposing) Client.Dispose(); base.Dispose(disposing); }
    private bool saved = true;
    public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult<string?>(saved ? "synthetic-only" : null);
    public Task<bool> HasCredentialAsync(CancellationToken cancellation) => Task.FromResult(saved);
    public Task SaveAsync(string secret, CancellationToken cancellation) { saved = true; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellation) { saved = false; return Task.CompletedTask; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var uri = request.RequestUri!;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split('=', 2)).ToDictionary(v => Uri.UnescapeDataString(v[0]), v => v.Length > 1 ? Uri.UnescapeDataString(v[1]) : "");
        if (uri.Host == "images.example")
        {
            var image = uri.AbsolutePath.Contains("invalid", StringComparison.Ordinal) ? new byte[] { 1, 2, 3 } : Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACgAAAA8CAIAAACb22+3AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABUSURBVFhH7c2hFQAgDMTQA1HWY1gGRIPmBZOo9sxvMyuVVDJyHNf7eu/5lDCWMJYwljCWMJYwljCWMJYwljCWMJYwljCWMJYwljCWMJYwljDWN3gDzZMCdFL7NuoAAAAASUVORK5CYII=");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) }; response.Content.Headers.ContentType = new("image/png"); return response;
        }
        query.TryGetValue("q", out var q);
        if (q == "offline") throw new HttpRequestException("synthetic unavailable");
        if (q == "unauthorized") return Json(new { code = "EXPIRED_API_KEY" }, 401);
        if (q == "rate") { var response = Json(new { code = "RATE_LIMITED" }, 429); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); return response; }
        // Leave enough time for observable desktop interaction, still below the shared 45 s deadline.
        if (q is "slow" or "A") await Task.Delay(40000, cancellation);
        if (uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
        {
            string[] ids = q == "empty" ? [] : q == "invalid-cover" ? ["99"] : q == "mismatched-detail" ? ["86"] : q == "slow-detail" ? ["87"] : query.ContainsKey("cursor") ? ["2", "3"] : ["1", "2"];
            return Json(new { @object = "list", items = ids.Select(id => new { @object = "search_result", target_object = "work", id }), next_cursor = query.ContainsKey("cursor") || q is "empty" or "invalid-cover" ? null : "cur_page2" });
        }
        if (query.TryGetValue("ids", out var joined)) return Json(new { @object = "list", items = joined.Split(',').Reverse().Select(Work) });
        if (query.ContainsKey("limit")) { if (slowConnection) await Task.Delay(40000, cancellation); return Json(new { @object = "list", items = Array.Empty<object>() }); }
        DetailRequests++;
        var id = uri.Segments.Last();
        if (id == "87") await Task.Delay(40000, cancellation);
        return Json(Work(id == "86" ? "90" : id));
    }
    private static object Work(string id) => new { @object = "work", id, display_name = "合成在线作品 " + id, cover = new { url = "https://images.example/" + (id == "99" ? "invalid" : "cover") + ".png" },
        intros = new[] { new { lang = "zh-Hans", value = "这是隔离验收使用的虚构作品。资料按需加载；加入愿望单后可离线查看与编辑。" } }, companies = new[] { new { attribution_role = "developer", display_name = "Synthetic Studio" } } };
    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
