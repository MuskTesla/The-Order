using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

public sealed partial class NextMoeCatalog
{
    private sealed record CachedReleasePage(CatalogReleasesPage Page, DateTimeOffset Until, int Bytes, long AuthGeneration);
    private readonly object releaseGate = new();
    private readonly Dictionary<(string Work, string Cursor), CachedReleasePage> releasePages = [];
    private readonly Dictionary<(string Work, string Cursor), long> releaseLoads = [];
    private long releaseGeneration;

    public async Task<CatalogReleasesPage> LoadReleasesAsync(string workId, string? cursor, bool refresh, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!CatalogReleaseFacts.Id(workId) || !CatalogReleaseFacts.Cursor(cursor)) throw new OnlineException(OnlineFailure.InvalidRequest);
        var key = (workId, cursor ?? ""); long generation = 0;
        try { return await transport.Operation(async scope =>
        {
            var cached = transport.ReadCurrent<CatalogReleasesPage?>(scope, () =>
            {
                lock (releaseGate)
                {
                    if (refresh && cursor is null)
                    {
                        foreach (var prior in releasePages.Keys.Where(k => k.Work == workId).ToArray()) releasePages.Remove(prior);
                        // A refresh invalidates later pages and admissions still in flight for this work.
                        foreach (var prior in releaseLoads.Keys.Where(k => k.Work == workId).ToArray()) releaseLoads.Remove(prior);
                    }
                    if (!refresh && releasePages.TryGetValue(key, out var page) && page.Until > Now && page.AuthGeneration == scope.Auth.Generation)
                        return page.Page;
                    generation = ++releaseGeneration; releaseLoads[key] = generation;
                    return null;
                }
            });
            if (cached is not null) return CopyPage(cached);
            {
                var parameters = Parameters(); parameters["limit"] = "100"; if (cursor is not null) parameters["cursor"] = cursor;
                using var body = await transport.GetAsync("catalog/works/" + workId + "/releases", parameters, scope).ConfigureAwait(false);
                RequireList(body.RootElement, 100);
                var cursorField = Field(body.RootElement, "next_cursor");
                string? next = cursorField.ValueKind == JsonValueKind.Undefined ? null
                    : cursorField.ValueKind == JsonValueKind.String ? cursorField.GetString() : throw new OnlineException(OnlineFailure.InvalidResponse);
                if (!CatalogReleaseFacts.Cursor(next) || next is not null && next == cursor) throw new OnlineException(OnlineFailure.InvalidResponse);
                var rows = Rows(body.RootElement, "items").Select(row => MapDisplayRelease(row, workId)).ToArray();
                if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
                    throw new OnlineException(OnlineFailure.InvalidResponse);
                var result = new CatalogReleasesPage(workId, rows, next, Now);
                scope.Checkpoint(OnlineAcceptanceStage.Mapped);
                var bytes = System.Text.Encoding.UTF8.GetByteCount(body.RootElement.GetRawText());
                scope.DeferCache(() =>
                {
                    lock (releaseGate)
                    {
                        scope.Check();
                        if (!releaseLoads.TryGetValue(key, out var current) || current != generation) return null;
                        releasePages.Remove(key, out var previous);
                        while (releasePages.Count >= 32 || releasePages.Values.Sum(v => (long)v.Bytes) + bytes > CatalogTransport.MaximumBytes)
                        {
                            if (releasePages.Count == 0) break;
                            releasePages.Remove(releasePages.OrderBy(v => v.Value.Until).First().Key);
                        }
                        var accepted = new CachedReleasePage(CopyPage(result), Now.AddMinutes(5), bytes, scope.Auth.Generation);
                        releasePages[key] = accepted;
                        return () => { lock (releaseGate) { if (releasePages.TryGetValue(key, out var entry) && ReferenceEquals(entry, accepted))
                            { releasePages.Remove(key); if (previous is not null) releasePages[key] = previous; } } };
                    }
                });
                return result;
            }
        }, cancellation).ConfigureAwait(false); }
        finally { lock (releaseGate) if (generation != 0 && releaseLoads.TryGetValue(key, out var current) && current == generation) releaseLoads.Remove(key); }
    }
    // The cache owns copies: callers cannot mutate a page that another request will reuse.
    private static CatalogReleasesPage CopyPage(CatalogReleasesPage page) => page with
    {
        Items = page.Items.Select(row => row with { Languages = [..row.Languages], Platforms = [..row.Platforms], References = [..row.References] }).ToArray()
    };
    private static CatalogReleaseEdition MapDisplayRelease(JsonElement row, string workId)
    {
        string Required(string name, int maximum)
        {
            var field = Field(row, name);
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } value || value.Length > maximum || value != value.Trim())
                throw new OnlineException(OnlineFailure.InvalidResponse);
            return value;
        }
        string? Optional(string name, int maximum)
        {
            var field = Field(row, name);
            if (field.ValueKind == JsonValueKind.Null) return null;
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } value || value.Length > maximum)
                throw new OnlineException(OnlineFailure.InvalidResponse);
            return value;
        }
        var parent = Field(row, "work_id");
        if (Text(row, "object") != "release" || parent.ValueKind != JsonValueKind.Null &&
            (parent.ValueKind != JsonValueKind.String || parent.GetString() != workId)) throw new OnlineException(OnlineFailure.InvalidResponse);
        var language = Required("lang", 32); var platform = Required("platform", 64);
        var platformRows = Field(row, "platforms"); var references = Field(row, "refs");
        if (platformRows.ValueKind != JsonValueKind.Array || platformRows.GetArrayLength() > 32
            || references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > 128) throw new OnlineException(OnlineFailure.InvalidResponse);
        var platforms = platformRows.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 } text || text != text.Trim())
                throw new OnlineException(OnlineFailure.InvalidResponse);
            return text;
        }).ToList();
        // Some live rows provide only platform. Do not lose that explicit value or infer others.
        if (platform.Length > 0 && !platforms.Contains(platform, StringComparer.Ordinal)) platforms.Add(platform);
        var refs = references.EnumerateArray().Select(reference =>
        {
            var source = Field(reference, "source"); var external = Field(reference, "external_id");
            if (source.ValueKind != JsonValueKind.String || external.ValueKind != JsonValueKind.String)
                throw new OnlineException(OnlineFailure.InvalidResponse);
            var s = source.GetString()!; var id = external.GetString()!;
            return new WorkReference(s, id, ReleaseReferenceUrl(s, id));
        }).ToArray();
        CatalogReleaseEngine? engine = null;
        var engineField = Field(row, "engine");
        if (engineField.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            if (engineField.ValueKind != JsonValueKind.Object || Text(engineField, "object") != "engine"
                || Field(engineField, "id").ValueKind != JsonValueKind.String
                || Field(engineField, "display_name").ValueKind != JsonValueKind.String)
                throw new OnlineException(OnlineFailure.InvalidResponse);
            engine = new(Field(engineField, "id").GetString()!, Field(engineField, "display_name").GetString()!);
        }
        var result = new CatalogReleaseEdition(Required("id", 20), workId, Optional("title", 512), null,
            Required("release_kind", 64), Optional("date", 10), language.Length == 0 ? [] : [language],
            platforms.Distinct(StringComparer.Ordinal).ToArray(), refs) { Engine = engine };
        if (!CatalogReleaseFacts.Valid(result, workId)) throw new OnlineException(OnlineFailure.InvalidResponse);
        return result;
    }
    private static string? ReleaseReferenceUrl(string source, string id)
    {
        if (source == "dlsite" && System.Text.RegularExpressions.Regex.IsMatch(id, "^(?:RJ|VJ|BJ)[0-9]{6,12}$"))
            return "https://www.dlsite.com/home/work/=/product_id/" + id + ".html";
        if (source == "getchu" && CatalogReleaseFacts.Id(id)) return "https://www.getchu.com/soft.phtml?id=" + id;
        return ReferenceUrl(source, id);
    }
}
