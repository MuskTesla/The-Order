using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;

namespace TheOrder.Infrastructure.Online;

public sealed partial class NextMoeCatalog
{
    public Task<ImmutableArray<WorkMetadata>> ResolveReferencesAsync(ImmutableArray<string> references, CancellationToken cancellation)
    {
        if (references.IsDefaultOrEmpty || references.Length > 100 || references.Any(r => r is null || !Recognition.TitleMatching.Rx(@"^(?:vndb:v[1-9][0-9]*|(?:bangumi|steam):[1-9][0-9]*)$").IsMatch(r))) throw new OnlineException(OnlineFailure.InvalidRequest);
        var requested = references.ToHashSet(StringComparer.Ordinal); string joined = string.Join(',', requested.Order(StringComparer.Ordinal));
        if (joined.Length > 4096) throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation(async scope =>
        {
            var p = Parameters(); p["refs"] = joined; p["include"] = "refs,titles,covers,intros,companies,ratings,tags";
            using var body = await transport.GetAsync("catalog/works", p, scope).ConfigureAwait(false);
            RequireList(body.RootElement, 128);
            var missing = Field(body.RootElement, "missing");
            if (missing.ValueKind != JsonValueKind.Undefined && (missing.ValueKind != JsonValueKind.Array || missing.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || !requested.Contains(r.GetString()!)))) throw new OnlineException(OnlineFailure.InvalidResponse);
            var works = new Dictionary<string, WorkMetadata>(StringComparer.Ordinal);
            foreach (var dto in Rows(body.RootElement, "items"))
            {
                var refs = Field(dto, "refs");
                if (refs.ValueKind != JsonValueKind.Undefined && (refs.ValueKind != JsonValueKind.Array || refs.EnumerateArray().Any(r => Field(r, "source").ValueKind != JsonValueKind.String || Text(r, "source").Length == 0 || Field(r, "external_id").ValueKind != JsonValueKind.String || Text(r, "external_id").Length == 0))) throw new OnlineException(OnlineFailure.InvalidResponse);
                var work = new RemoteWorkDto(dto).Map(includeExplicit, Now);
                if (Rows(dto, "refs").Any(r => requested.Contains(Text(r, "source") + ":" + Text(r, "external_id")))) works.TryAdd(work.Id, work);
            }
            scope.Checkpoint(OnlineAcceptanceStage.Mapped);
            return works.Values.OrderBy(w => w.Id, StringComparer.Ordinal).ToImmutableArray();
        }, cancellation);
    }
    public Task<ImmutableArray<ReleaseIdentity>> ReleasesAsync(string id, CancellationToken cancellation)
    {
        if (!ValidId(id)) throw new OnlineException(OnlineFailure.InvalidRequest);
        return transport.Operation(async scope =>
        {
            var visited = new HashSet<string>(StringComparer.Ordinal); string current = id;
            for (int merge = 0; merge <= 3; merge++)
            {
                if (!visited.Add(current)) throw new OnlineException(OnlineFailure.MergeLoop);
                try
                {
                    var releases = new Dictionary<string, ReleaseIdentity>(StringComparer.Ordinal); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
                    for (int page = 0; page < 2; page++)
                    {
                        var p = Parameters(); p["limit"] = "100"; if (cursor is not null) p["cursor"] = cursor;
                        using var body = await transport.GetAsync("catalog/works/" + current + "/releases", p, scope).ConfigureAwait(false);
                        RequireList(body.RootElement, 100);
                        var rawCursor = Field(body.RootElement, "next_cursor"); string next = Text(body.RootElement, "next_cursor");
                        if (rawCursor.ValueKind != JsonValueKind.Undefined && (rawCursor.ValueKind != JsonValueKind.String || next.Length > 512 || !Recognition.TitleMatching.Rx(@"^cur_[A-Za-z0-9._~-]+$").IsMatch(next) || !cursors.Add(next))) throw new OnlineException(OnlineFailure.InvalidResponse);
                        foreach (var row in Rows(body.RootElement, "items"))
                        {
                            string releaseId = Text(row, "id"); var parent = Field(row, "work_id");
                            if (Text(row, "object") != "release" || !ValidId(releaseId) || Field(row, "id").GetString() != releaseId
                                || parent.ValueKind != JsonValueKind.Null && (parent.ValueKind != JsonValueKind.String || parent.GetString() != current)) throw new OnlineException(OnlineFailure.InvalidResponse);
                            string title = Text(row, "title"), kind = Text(row, "release_kind"), date = Text(row, "date");
                            var titleField = Field(row, "title"); var refs = Field(row, "refs");
                            if (title.Length > 512 || titleField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined)
                                || refs.ValueKind != JsonValueKind.Undefined && refs.ValueKind != JsonValueKind.Array) throw new OnlineException(OnlineFailure.InvalidResponse);
                            foreach (var reference in Rows(row, "refs"))
                            {
                                string source = Text(reference, "source"), external = Text(reference, "external_id");
                                if (source.Length is < 1 or > 64 || external.Length is < 1 or > 256 || Field(reference, "source").GetString() != source || Field(reference, "external_id").GetString() != external) throw new OnlineException(OnlineFailure.InvalidResponse);
                            }
                            if (kind is not ("default" or "digital" or "physical" or "trial" or "patch")) kind = "unknown";
                            releases.TryAdd(releaseId, new(releaseId, current, title, title, kind, Recognition.TitleMatching.Rx(@"^\d{4}-\d{2}-\d{2}$").IsMatch(date) ? date : null));
                        }
                        if (next.Length == 0) break; cursor = next;
                    }
                    scope.Checkpoint(OnlineAcceptanceStage.Mapped); return releases.Values.ToImmutableArray();
                }
                catch (MergedWorkException merged) { current = merged.CanonicalId; }
            }
            throw new OnlineException(OnlineFailure.MergeLimit);
        }, cancellation);
    }
}
