using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Online;

// Optional public artwork supplement. Uses its own unauthenticated client so the
// NextMoe key can never leave the catalog transport. Identity facts stay in KunGal.
public sealed partial class VndbArtworkSource(HttpClient http, Func<Stream>? openHints = null) : IPublicArtworkSource
{
    private readonly SemaphoreSlim requests = new(1);
    private DateTimeOffset nextRequest;
    private readonly Lazy<Dictionary<string, string[]>> hints = new(() =>
    {
        try { using var stream = openHints?.Invoke(); return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    });
    public async Task<ArtworkSupplement> ReadAsync(WorkMetadata work, CancellationToken cancellation)
    {
        var ids = work.References.Where(r => r.Source == "vndb" && WorkId().IsMatch(r.Id)).Select(r => r.Id).Distinct().Take(2).ToArray();
        if (ids.Length != 1) return new([], true);
        await requests.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1600);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn")
            {
                Content = JsonContent.Create(new { filters = new[] { "id", "=", ids[0] }, fields = "image.id,image.url,image.dims,image.sexual,screenshots.id,screenshots.url,screenshots.dims,screenshots.sexual,screenshots.violence", results = 1 })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { if ((int)response.StatusCode == 429) nextRequest = DateTimeOffset.UtcNow.AddMinutes(1); return new([], false); }
            var bytes = await CatalogTransport.ReadBoundedAsync(response.Content, 512 * 1024, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            return Parse(doc.RootElement, ids[0], hints.Value.GetValueOrDefault(ids[0]) ?? []);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new([], false); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OnlineException) { return new([], false); }
        finally { requests.Release(); }
    }
    public static ArtworkSupplement Parse(JsonElement data, string id, string[] preferred)
    {
        var rows = RemoteWorkDto.Rows(data, "results").Take(2).ToArray();
        if (rows.Length != 1 || RemoteWorkDto.Text(rows[0], "id") != id) return new([], false);
        var art = new List<CatalogArt>();
        foreach (var image in RemoteWorkDto.Rows(rows[0], "screenshots").Take(64))
        {
            var dims = RemoteWorkDto.Field(image, "dims");
            var sexual = RemoteWorkDto.Field(image, "sexual"); var violence = RemoteWorkDto.Field(image, "violence");
            if (!SafeRating(sexual) || !SafeRating(violence) || !Dimensions(dims, out var width, out var height) || width < 640 || height < 180) continue;
            var url = RemoteWorkDto.Text(image, "url"); var screenshotId = RemoteWorkDto.Text(image, "id");
            if (!ScreenshotId().IsMatch(screenshotId) || !ImageUri(url, screenshotId, "sf")) continue;
            // A historical hint is used only if this exact work still lists the image as safe.
            art.Add(new(url, width, height, preferred.Contains(screenshotId) ? "vndb-curated" : "vndb-screenshot", "VNDB " + id));
        }
        var cover = RemoteWorkDto.Field(rows[0], "image");
        var coverUrl = RemoteWorkDto.Text(cover, "url"); var coverId = RemoteWorkDto.Text(cover, "id");
        ImmutableArray<WorkCover> covers = [];
        if (CoverId().IsMatch(coverId) && ImageUri(coverUrl, coverId, "cv") && SafeRating(RemoteWorkDto.Field(cover, "sexual")) && Dimensions(RemoteWorkDto.Field(cover, "dims"), out var cw, out var ch))
            covers = [new("vndb:" + coverId, coverUrl, cw, ch, null, "safe")];
        return new([.. art.DistinctBy(a => a.Url)], true, covers);
    }
    private static bool SafeRating(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n is >= 0 and < 1;
    private static bool Dimensions(JsonElement dims, out int width, out int height)
    {
        width = height = 0;
        return dims.ValueKind == JsonValueKind.Array && dims.GetArrayLength() == 2 && dims[0].ValueKind == JsonValueKind.Number && dims[1].ValueKind == JsonValueKind.Number &&
            dims[0].TryGetInt32(out width) && dims[1].TryGetInt32(out height) && width is > 0 and <= 65535 && height is > 0 and <= 65535 && (long)width * height <= 40_000_000;
    }
    private static bool ImageUri(string url, string id, string directory)
    {
        var number = id[2..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "t.vndb.org" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath == "/" + directory + "/" + number.PadLeft(2, '0')[^2..] + "/" + number + ".jpg";
    }
    internal static bool OriginalImageUri(Uri uri)
    {
        var match = OriginalImagePath().Match(uri.AbsolutePath);
        return match.Success && ImageUri(uri.AbsoluteUri, match.Groups[1].Value + match.Groups[2].Value, match.Groups[1].Value);
    }
    [GeneratedRegex("^/(cv|sf)/[0-9]{2}/([0-9]{1,12})\\.jpg$", RegexOptions.CultureInvariant)] private static partial Regex OriginalImagePath();
    [GeneratedRegex("^v[0-9]{1,12}$", RegexOptions.CultureInvariant)] private static partial Regex WorkId();
    [GeneratedRegex("^sf[0-9]{1,12}$", RegexOptions.CultureInvariant)] private static partial Regex ScreenshotId();
    [GeneratedRegex("^cv[0-9]{1,12}$", RegexOptions.CultureInvariant)] private static partial Regex CoverId();
}
