using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Online;

// Public, exact-ID reads only. This transport never shares NextMoe credentials, cookies or redirects.
public sealed class DirectMetadataSources(HttpClient client, BangumiAccessContext? access = null) : IDirectMetadataSource
{
    private readonly BangumiAccessContext authorization = access ?? new();
    private readonly SemaphoreSlim queue = new(1);
    private DateTimeOffset next;
    public async Task<WorkMetadata> FetchAsync(string source, string id, bool allowAdult, CancellationToken cancellation)
    {
        id = DirectMetadataIdentity.Normalize(source, id);
        var lease = source == "bangumi" ? authorization.Capture() : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lease?.Changed ?? CancellationToken.None);
        await queue.WaitAsync(linked.Token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie"))
                throw new InvalidDataException("资料源网络配置无效，未发送请求。");
            if (next > DateTimeOffset.UtcNow) await Task.Delay(next - DateTimeOffset.UtcNow, timeout.Token).ConfigureAwait(false);
            using var request = source == "bangumi" ? new HttpRequestMessage(HttpMethod.Get, "https://api.bgm.tv/v0/subjects/" + id)
                : new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn") { Content = JsonContent.Create(new { filters = new object[] { "id", "=", id }, fields = "title,alttitle,aliases,released,description,image.url,image.sexual,rating,votecount,developers.name,tags.name,tags.spoiler,va.character.name,va.character.original,va.character.image.url,va.character.image.sexual,va.staff.name", results = 1 }) };
            request.Headers.UserAgent.ParseAdd("TheOrder/1.1 (https://github.com/Ph-Rank/The-Order)");
            lease?.Authorize(request);
            next = DateTimeOffset.UtcNow.AddSeconds(2);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && lease?.Authorized == true)
            { authorization.Reject(lease); throw new BangumiRelationsException(BangumiRelationsFailure.Unauthorized, "番组计划授权已失效，请在设置中重新授权。"); }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("该编号不存在或资料源未开放匿名读取，请核对编号及访问权限。");
            if ((int)response.StatusCode == 429) { next = DateTimeOffset.UtcNow.AddSeconds(60); throw new InvalidDataException("资料源请求过于频繁，请稍后重试。"); }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("资料源暂时无法读取（HTTP " + (int)response.StatusCode + "），书架未改变。");
            if (response.Content.Headers.ContentLength > 2_097_152) throw new InvalidDataException("资料源响应超过大小限制。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using var bytes = new MemoryStream(); var buffer = new byte[8192];
            int size; while ((size = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) { if (bytes.Length + size > 2_097_152) throw new InvalidDataException("资料源响应超过大小限制。"); await bytes.WriteAsync(buffer.AsMemory(0, size), timeout.Token).ConfigureAwait(false); }
            using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            linked.Token.ThrowIfCancellationRequested();
            return Map(source, id, doc.RootElement, allowAdult);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && lease?.Changed.IsCancellationRequested != true) { throw new InvalidDataException("资料源读取超时，书架未改变，请重试。"); }
        catch (HttpRequestException) { throw new InvalidDataException("无法连接资料源，书架未改变，请检查网络。"); }
        finally { queue.Release(); }
    }
    public static WorkMetadata Map(string source, string id, JsonElement json, bool allowAdult)
    {
        id = DirectMetadataIdentity.Normalize(source, id); var value = json;
        if (source == "vndb")
        {
            var rows = Array(json, "results"); if (rows.Length != 1) throw new InvalidDataException("未找到唯一的指定作品。"); value = rows[0];
            if (Text(value, "id") != id) throw new InvalidDataException("资料源返回了不同编号。");
        }
        else if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var returned) || returned.ToString() != id || Number(value, "type") != 4) throw new InvalidDataException("该编号不是游戏条目。");
        var adult = source == "bangumi" && Bool(value, "nsfw");
        if (adult && !allowAdult) throw new InvalidDataException("该作品需要先在设置中允许 NSFW 内容。");
        var title = Text(value, source == "bangumi" ? "name_cn" : "alttitle"); var original = Text(value, source == "bangumi" ? "name" : "title"); if (title.Length == 0) title = original;
        if (title.Length == 0) throw new InvalidDataException("作品缺少标题。");
        var image = Object(value, source == "bangumi" ? "images" : "image");
        var cover = Url(Text(image, source == "bangumi" ? "large" : "url"));
        if (source == "vndb" && !allowAdult && Number(image, "sexual") > 0) cover = null;
        var date = Text(value, source == "bangumi" ? "date" : "released"); string? precision = date.Length switch { 10 => "day", 7 => "month", 4 => "year", _ => null }; if (precision is null) date = "";
        var developers = source == "vndb" ? Array(value, "developers").Select(x => Text(x, "name")) : Array(value, "infobox").Where(x => Text(x, "key") is "开发" or "开发商" or "制作").Select(x => Text(x, "value"));
        var ratings = ImmutableArray.CreateBuilder<SourceRating>(); var score = source == "bangumi" ? Number(Object(value, "rating"), "score") : Number(value, "rating");
        if (score > 0) ratings.Add(new(source, score, source == "bangumi" ? 10 : 100, source == "bangumi" ? Number(Object(value, "rating"), "total") : Number(value, "votecount")));
        var tags = Array(value, "tags").Where(x => source != "vndb" || Number(x, "spoiler") == 0).Select(x => Text(x, "name"));
        var characters = source == "vndb" ? Array(value, "va").GroupBy(x => Text(Object(x, "character"), "id")).Where(g => g.Key.Length > 0).Take(100).Select(g =>
        { var c = Object(g.First(), "character"); var artwork = Object(c, "image"); return new WorkCharacter(g.Key, Text(c, "name"), Text(c, "original"), !allowAdult && Number(artwork, "sexual") > 0 ? null : Url(Text(artwork, "url")), "", [.. g.Select(x => Text(Object(x, "staff"), "name")).Where(x => x.Length > 0).Distinct().Take(20)]); }) : [];
        var web = source == "bangumi" ? "https://bgm.tv/subject/" + id : "https://vndb.org/" + id;
        return new(source + ":" + id, title, original, [.. Array(value, "aliases").Where(x => x.ValueKind == JsonValueKind.String).Select(x => Limit(x.GetString() ?? "", 2000)).Take(100)], null, cover, null, null,
            Text(value, source == "bangumi" ? "summary" : "description", 200000), date.Length == 0 ? null : date, precision,
            [.. developers.Where(x => x.Length > 0).Distinct().Take(100)], [.. tags.Where(x => x.Length > 0).Distinct().Take(500)], ratings.ToImmutable(), [.. characters],
            [new(source, id, web)], web, adult ? "adult" : null, DateTimeOffset.UtcNow.ToString("O"));
    }
    private static string Limit(string x, int max) => x.Length > max || x.Contains('\0') ? throw new InvalidDataException("资料源字段超过限制或格式无效。") : x;
    private static string Text(JsonElement x, string key, int max = 2000) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Limit(v.GetString() ?? "", max) : "";
    private static double Number(JsonElement x, string key) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : 0;
    private static bool Bool(JsonElement x, string key) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static JsonElement Object(JsonElement x, string key) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    private static JsonElement[] Array(JsonElement x, string key) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(1001).ToArray() : [];
    private static string? Url(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Port == 443 ? value : null;
}
