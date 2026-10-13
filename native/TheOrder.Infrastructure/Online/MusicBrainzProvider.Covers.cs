using System.Net;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

public sealed partial class MusicBrainzProvider
{
    private async Task<string?> ReadCoverAsync(string releaseId, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var uri = new Uri("https://coverartarchive.org/release/" + releaseId);
        for (int hop = 0; hop < 4; hop++)
        {
            using var lease = await Rate.EnterAsync(timeout.Token).ConfigureAwait(false);
            using var request = Request(uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } returned && returned != uri) throw Invalid();
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                if (next is null || !next.IsAbsoluteUri || !ValidCoverIndex(next, releaseId)) throw Invalid();
                uri = next; continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") throw Invalid();
            using var data = JsonDocument.Parse(await ReadBytes(response.Content, timeout.Token).ConfigureAwait(false), new() { MaxDepth = 32 });
            return MapCover(releaseId, data.RootElement);
        }
        throw Invalid();
    }
    internal static bool ValidCoverIndex(Uri uri, string releaseId) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && MusicBrainzValidation.ArchiveHost(uri.Host)
        && (uri.AbsolutePath == "/download/mbid-" + releaseId + "/index.json"
            || System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/[0-9]+/items/mbid-" + releaseId + "/index\\.json$"));
    public static string? MapCover(string releaseId, JsonElement root)
    {
        var release = OptionalText(root, "release", 200);
        // The CAA index historically uses http for canonical entity/image references. Only
        // these fixed origins are upgraded; no arbitrary metadata URL is rewritten or followed.
        if (release is not null && release.StartsWith("http://musicbrainz.org/", StringComparison.Ordinal)) release = "https://" + release[7..];
        if (release != new MusicBrainzReference(MusicBrainzEntityKind.Release, releaseId).Url
            || !root.TryGetProperty("images", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256) throw Invalid();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("front", out var front) || front.ValueKind != JsonValueKind.True
                || !row.TryGetProperty("approved", out var approved) || approved.ValueKind != JsonValueKind.True
                || !row.TryGetProperty("thumbnails", out var thumbs)) continue;
            var image = OptionalText(thumbs, "500", 2000);
            if (image is not null && image.StartsWith("http://coverartarchive.org/", StringComparison.Ordinal)) image = "https://" + image[7..];
            if (MusicBrainzValidation.CoverUri(image, releaseId)) return image;
        }
        return null;
    }
}
