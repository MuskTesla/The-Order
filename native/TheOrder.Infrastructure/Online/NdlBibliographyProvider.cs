using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// The caller owns the pooled anonymous client. Its handler must disable cookies,
// credentials and redirects (PublicArtworkNetwork does); this provider never owns
// or replaces that client, and never uses the authenticated catalog transport.
public sealed class NdlBibliographyProvider : IBookBibliographyProvider
{
    public const string Origin = "https://ndlsearch.ndl.go.jp";
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int SearchPageSize = 10;
    public const string UserAgent = "TheOrder/1.1.0 (https://github.com/Ph-Rank/The-Order)";
    private readonly HttpClient client;
    internal NdlRequestCoordinator Coordinator { get; }
    private readonly TimeSpan duration;
    private readonly TimeSpan attemptDuration;
    private readonly Func<DateTimeOffset> clock;
    private static readonly XNamespace Sru = "http://www.loc.gov/zing/srw/", Diagnostic = "http://www.loc.gov/zing/srw/diagnostic/",
        Oai = "http://www.openarchives.org/OAI/2.0/", Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        Dc = "http://purl.org/dc/elements/1.1/", Terms = "http://purl.org/dc/terms/",
        Ndl = "http://ndl.go.jp/dcndl/terms/", Foaf = "http://xmlns.com/foaf/0.1/";
    private static readonly Regex DateRange = new(@"\b\d{4}(?:年)?(?:[./]\d{1,2}){0,2}\s*(?:[-–—〜～]|to)\s*(?:\d{4}|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public NdlBibliographyProvider(HttpClient client) : this(client, NdlRequestCoordinator.Shared) { }
    internal NdlBibliographyProvider(HttpClient client, NdlRequestCoordinator coordinator,
        TimeSpan? operationTimeout = null, Func<DateTimeOffset>? clock = null, TimeSpan? attemptTimeout = null)
    { this.client = client; Coordinator = coordinator; duration = operationTimeout ?? TimeSpan.FromSeconds(25); this.clock = clock ?? (() => DateTimeOffset.UtcNow); attemptDuration = attemptTimeout ?? TimeSpan.FromSeconds(12); }

    public Task<BookSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellation)
    {
        if (query is null || !query.IsValid) throw new InvalidDataException("出版资料检索条件无效。");
        // ISBN first: a translated/display title must not suppress an exact ISBN hit.
        var cql = "dpid = \"iss-ndl-opac-national\" AND mediatype = \"books\" AND " + (query.Isbn is { } isbn
            ? "isbn = " + Quote(isbn) : "title = " + Quote(query.Title));
        if (query.Isbn is null && query.Creator is { Length: > 0 } creator) cql += " AND creator = " + Quote(creator);
        var uri = UriFor("sru", new() { ["operation"] = "searchRetrieve", ["version"] = "1.2", ["recordSchema"] = "dcndl_v3",
            ["recordPacking"] = "xml", ["onlyBib"] = "true", ["maximumRecords"] = SearchPageSize.ToString(CultureInfo.InvariantCulture), ["startRecord"] = query.Start.ToString(CultureInfo.InvariantCulture), ["query"] = cql });
        return Operation(async token => ParseSearch(await GetXml(uri, token).ConfigureAwait(false), query, clock()), cancellation);
    }
    public Task<BookPublication> FetchAsync(string id, CancellationToken cancellation)
    {
        if (!BookValidation.NdlId(id)) throw new InvalidDataException("NDL 出版资料编号无效。");
        var uri = UriFor("oaipmh", new() { ["verb"] = "GetRecord", ["metadataPrefix"] = "dcndl_v3", ["identifier"] = "oai:ndlsearch.ndl.go.jp:R100000002-I" + id });
        return Operation(async token => ParseRecord(await GetXml(uri, token).ConfigureAwait(false), id, clock()), cancellation);
    }
    private async Task<T> Operation<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); GuardHeaders();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(duration);
        try { var result = await action(deadline.Token).ConfigureAwait(false); deadline.Token.ThrowIfCancellationRequested(); return result; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new BookProviderException(BookFailure.Timeout, "NDL 出版资料读取超时，可稍后重试。"); }
        catch (HttpRequestException) { throw new BookProviderException(BookFailure.Offline, "无法连接 NDL，已保留原有资料。"); }
        catch (OnlineException e) { throw e.Failure == OnlineFailure.ResponseTooLarge ? TooLarge() : Invalid(); }
        catch (Exception e) when (e is XmlException or DecoderFallbackException or FormatException or RegexMatchTimeoutException or OverflowException) { throw Invalid(); }
        catch (IOException) { throw new BookProviderException(BookFailure.Offline, "NDL 响应未能完整读取，请稍后重试。"); }
    }
    private void GuardHeaders()
    {
        // Only harmless framework negotiation headers can be inherited. Reject
        // arbitrary API-key/secret headers without reading or logging their values.
        if (client.DefaultRequestHeaders.Any(x => !x.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
            && !x.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !x.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))) throw Invalid();
    }
    private async Task<XDocument> GetXml(Uri uri, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var lease = await Coordinator.EnterAsync(token).ConfigureAwait(false);
            GuardHeaders(); token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent); request.Headers.Accept.ParseAdd("application/xml"); request.Headers.Accept.ParseAdd("text/xml");
            // One stalled connection must not consume the entire operation. A
            // second attempt shares the original 25s deadline and serial queue.
            using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptDeadline.CancelAfter(attemptDuration);
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptDeadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != uri || (int)response.StatusCode is >= 300 and < 400) throw Invalid();
                int status = (int)response.StatusCode;
                if (status == 429)
                {
                    var wait = RetryAfter(response, clock()) ?? TimeSpan.FromMinutes(1); Coordinator.Cooldown(wait);
                    if (attempt == 0 && wait <= TimeSpan.FromSeconds(2)) continue;
                    throw new BookProviderException(BookFailure.RateLimited, "NDL 正在限流，请稍后重试。");
                }
                if (status is 502 or 503 or 504 && attempt == 0)
                {
                    var wait = RetryAfter(response, clock()) ?? TimeSpan.FromMilliseconds(500); Coordinator.Cooldown(wait);
                    if (wait <= TimeSpan.FromSeconds(2)) continue;
                }
                if (status != 200) throw new BookProviderException(status switch { 404 => BookFailure.Unavailable, 408 => BookFailure.Timeout, >= 500 => BookFailure.Server, _ => BookFailure.InvalidResponse }, "NDL 未能提供出版资料，已保留原有资料。");
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is null || !(type.Equals("text/xml", StringComparison.OrdinalIgnoreCase) || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))) throw Invalid();
                var bytes = await CatalogTransport.ReadBoundedAsync(response.Content, MaximumResponseBytes, attemptDeadline.Token).ConfigureAwait(false);
                attemptDeadline.Token.ThrowIfCancellationRequested(); return ReadXml(bytes, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt == 0)
            { Coordinator.Cooldown(TimeSpan.FromSeconds(1)); }
            catch (HttpRequestException) when (attempt == 0)
            { token.ThrowIfCancellationRequested(); Coordinator.Cooldown(TimeSpan.FromSeconds(1)); }
        }
    }
    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var value = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now);
        if (value is null && response.Headers.TryGetValues("Retry-After", out var raw) && double.TryParse(raw.FirstOrDefault(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds) value = TimeSpan.FromSeconds(seconds);
        return value is { } wait ? wait < TimeSpan.Zero ? TimeSpan.Zero : wait : null;
    }
    private static Uri UriFor(string path, Dictionary<string, string> values) => new(Origin + "/api/" + path + "?" + string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))));
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static XDocument ReadXml(byte[] bytes, CancellationToken cancellation = default)
    {
        if (bytes.Length > MaximumResponseBytes) throw TooLarge();
        _ = new UTF8Encoding(false, true).GetCharCount(bytes);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumResponseBytes, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using var input = new MemoryStream(bytes, false);
        // Check depth and cancellation before materializing the bounded document.
        using (var scan = XmlReader.Create(input, settings))
            while (scan.Read()) { cancellation.ThrowIfCancellationRequested(); if (scan.Depth > 32) throw Invalid(); }
        input.Position = 0;
        using var reader = XmlReader.Create(input, settings); var document = XDocument.Load(reader);
        cancellation.ThrowIfCancellationRequested(); return document;
    }
    internal static BookSearchPage ParseSearch(XDocument document, BookSearchQuery query, DateTimeOffset fetched)
    {
        var root = document.Root; if (root is null || root.Name != Sru + "searchRetrieveResponse") throw Invalid();
        if (root.Element(Sru + "diagnostics") is { } diagnostics)
        {
            var entries = diagnostics.Elements(Diagnostic + "diagnostic").ToArray();
            if (entries.Length == 0 || diagnostics.Elements().Count() != entries.Length
                || entries.Any(x => Literal(x, Diagnostic + "message") != "Record does not exist") || root.Element(Sru + "records")?.Elements().Any() == true) throw Invalid();
            return new([], 0, null, fetched);
        }
        if (!int.TryParse(Literal(root, Sru + "numberOfRecords"), NumberStyles.None, CultureInfo.InvariantCulture, out int total) || total is < 0 or > 10000000) throw Invalid();
        var container = root.Element(Sru + "records"); var rows = container?.Elements(Sru + "record").ToArray() ?? [];
        if (rows.Length > 20 || container is not null && container.Elements().Count() != rows.Length
            || total == 0 && rows.Length != 0 || total > 0 && rows.Length == 0 || rows.Length > 0 && total < query.Start - 1 + rows.Length) throw Invalid();
        var found = new Dictionary<string, BookPublication>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var data = row.Element(Sru + "recordData"); var rdf = data?.Element(Rdf + "RDF");
            if (rdf is null || data!.Elements().Count() != 1) throw Invalid();
            var publication = MapRdf(rdf, fetched);
            if (query.Isbn is { } isbn && !publication.Isbns.Contains(isbn, StringComparer.Ordinal)) throw Invalid();
            if (found.TryGetValue(publication.Id, out var previous))
            { if (JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(publication)) throw Invalid(); }
            else found.Add(publication.Id, publication);
        }
        int expected = query.Start + rows.Length;
        if (expected > 101) throw Invalid();
        int? next = expected <= 91 && expected <= total && rows.Length > 0 ? expected : null;
        if (root.Element(Sru + "nextRecordPosition") is { } position)
        {
            if (!int.TryParse(position.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int serverNext) || serverNext < 0
                || serverNext != 0 && serverNext != expected || serverNext == 0 && expected <= total && rows.Length > 0) throw Invalid();
        }
        var page = new BookSearchPage(found.Values.ToArray(), total, next, fetched); if (!BookValidation.Page(page)) throw Invalid(); return page;
    }
    internal static BookPublication ParseRecord(XDocument document, string expectedId, DateTimeOffset fetched)
    {
        var root = document.Root; if (root is null || root.Name != Oai + "OAI-PMH") throw Invalid();
        if (root.Element(Oai + "error") is { } error)
            throw error.Attribute("code")?.Value == "idDoesNotExist" ? new BookProviderException(BookFailure.Unavailable, "NDL 没有此出版资料。") : Invalid();
        var records = root.Element(Oai + "GetRecord")?.Elements(Oai + "record").ToArray() ?? [];
        if (records.Length != 1) throw Invalid(); var record = records[0]; var header = record.Element(Oai + "header");
        if (header?.Attribute("status")?.Value == "deleted") throw new BookProviderException(BookFailure.Unavailable, "此 NDL 出版资料记录已移除。");
        if (header is null || Literal(header, Oai + "identifier") != "oai:ndlsearch.ndl.go.jp:R100000002-I" + expectedId) throw Invalid();
        var metadata = record.Element(Oai + "metadata"); var rdf = metadata?.Element(Rdf + "RDF"); if (rdf is null || metadata!.Elements().Count() != 1) throw Invalid();
        var publication = MapRdf(rdf, fetched); if (publication.Id != expectedId) throw Invalid(); return publication;
    }
    private static BookPublication MapRdf(XElement rdf, DateTimeOffset fetched)
    {
        var rows = rdf.Elements(Ndl + "BibResource").ToArray(); if (rows.Length != 1) throw Invalid(); var bib = rows[0];
        if (!bib.Elements(Ndl + "materialType").Any(x => x.Attribute(Rdf + "resource")?.Value == "http://ndl.go.jp/ndltype/Book")) throw Invalid();
        var ids = Identifiers(bib, "NDLBibID"); if (ids.Length != 1 || !BookValidation.NdlId(ids[0])) throw Invalid(); var id = ids[0];
        var baseUri = Origin + "/books/R100000002-I" + id; if (bib.Attribute(Rdf + "about")?.Value != baseUri + "#material") throw Invalid();
        var admins = rdf.Elements(Ndl + "BibAdminResource").Where(x => x.Element(Ndl + "record")?.Attribute(Rdf + "resource")?.Value == baseUri + "#material").ToArray();
        if (admins.Length != 1) throw Invalid(); var admin = admins[0];
        var provenance = admin.Elements(Terms + "provenance").SelectMany(x => x.Elements(Rdf + "Description")).ToArray();
        var rights = admin.Elements(Ndl + "rights").ToArray();
        if (admin.Attribute(Rdf + "about")?.Value != baseUri || Literal(admin, Ndl + "bibRecordCategory") != "R100000002" || Literal(admin, Ndl + "catalogingStatus") != "C7"
            || rights.Length != 1 || rights[0].Attribute(Rdf + "resource")?.Value != "https://creativecommons.org/licenses/by/4.0/"
            || provenance.Length != 1 || Literal(provenance[0], Ndl + "bibRecordCategory") != "R100000002" || Literal(provenance[0], Terms + "contributor") != "国立国会図書館") throw Invalid();
        var title = Literal(bib, Terms + "title"); var baseTitle = Structured(bib, Dc + "title"); var volume = Structured(bib, Ndl + "volume");
        var isbns = NormalizeIsbns(Identifiers(bib, "ISBN")); var sets = NormalizeIsbns(Identifiers(bib, "SetISBN"));
        var dates = Literals(bib, Terms + "date"); if (dates.Length == 0) dates = Literals(bib, Terms + "issued");
        var date = dates.Length == 0 ? null : string.Join("; ", dates);
        bool aggregate = isbns.Length > 1 || date is not null && DateRange.IsMatch(date)
            || Literals(admin, Terms + "description").Any(x => Regex.IsMatch(x, @"^type\s*:\s*title$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            || bib.Elements(Ndl + "volumeRange").Any(x => !string.IsNullOrWhiteSpace(x.Value));
        var result = new BookPublication(id, title, baseTitle ?? title, volume, isbns, sets, Literals(bib, Dc + "creator"),
            bib.Elements(Terms + "publisher").SelectMany(x => x.Elements(Foaf + "Agent")).SelectMany(x => x.Elements(Foaf + "name")).Select(x => x.Value.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
            StructuredMany(bib, Ndl + "seriesTitle"), date, OptionalLiteral(bib, Ndl + "edition"), Literals(bib, Terms + "language"), aggregate, fetched);
        if (!BookValidation.Publication(result)) throw Invalid(); return result;
    }
    private static string[] Identifiers(XElement bib, string datatype)
    { var rows = bib.Elements(Terms + "identifier").Where(x => x.Attribute(Rdf + "datatype")?.Value == Ndl.NamespaceName + datatype).ToArray(); if (rows.Any(x => x.HasElements)) throw Invalid(); return rows.Select(x => x.Value.Trim()).ToArray(); }
    private static string[] NormalizeIsbns(string[] values)
    { var result = values.Select(x => BookIsbn.Normalize(x) ?? throw Invalid()).Distinct(StringComparer.Ordinal).ToArray(); if (result.Length > 64) throw Invalid(); return result; }
    private static string Literal(XElement parent, XName name) => OptionalLiteral(parent, name) ?? "";
    private static string? OptionalLiteral(XElement parent, XName name)
    { var rows = parent.Elements(name).ToArray(); if (rows.Length > 1 || rows.Any(x => x.HasElements)) throw Invalid(); return rows.FirstOrDefault()?.Value.Trim() is { Length: > 0 } value ? value : null; }
    private static string[] Literals(XElement parent, XName name)
    { var rows = parent.Elements(name).ToArray(); if (rows.Any(x => x.HasElements)) throw Invalid(); return rows.Select(x => x.Value.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray(); }
    private static string? Structured(XElement parent, XName name)
    { var values = StructuredMany(parent, name); if (values.Length > 1) throw Invalid(); return values.FirstOrDefault(); }
    private static string[] StructuredMany(XElement parent, XName name)
    {
        var result = new List<string>();
        foreach (var element in parent.Elements(name))
        {
            if (!element.HasElements) { if (element.Value.Trim() is { Length: > 0 } value) result.Add(value); continue; }
            var descriptions = element.Elements(Rdf + "Description").ToArray(); if (descriptions.Length != 1 || element.Elements().Count() != 1) throw Invalid();
            var text = Literal(descriptions[0], Rdf + "value"); if (text.Length > 0) result.Add(text);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    private static BookProviderException Invalid() => new(BookFailure.InvalidResponse, "NDL 返回的来源、出版身份或资料格式不一致，已保留原有资料。");
    private static BookProviderException TooLarge() => new(BookFailure.ResponseTooLarge, "NDL 响应超过可读取范围。");
    // For isolated probes/tests, using the same policy as the application's public pool.
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
        Credentials = null, MaxConnectionsPerServer = 1, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli }) { Timeout = Timeout.InfiniteTimeSpan };
}

// Production instances and both endpoints share one monotonic serial queue.
// An internal isolated clock is available only for deterministic offline tests.
internal sealed class NdlRequestCoordinator(TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? maximumQueueWait = null)
{
    internal static NdlRequestCoordinator Shared { get; } = new();
    private readonly SemaphoreSlim queue = new(1);
    private TimeProvider Clock => clock ?? TimeProvider.System;
    private bool started; private long lastStart; private long cooldownStart; private TimeSpan cooldown;
    internal async Task<IDisposable> EnterAsync(CancellationToken cancellation)
    {
        if (!await queue.WaitAsync(maximumQueueWait ?? TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false))
            throw new BookProviderException(BookFailure.RateLimited, "NDL 上一请求尚未结束，请稍后重试；已保留现有资料。");
        try
        {
            while (true)
            {
                var wait = started ? TimeSpan.FromSeconds(1) - Clock.GetElapsedTime(lastStart) : TimeSpan.Zero;
                var blocked = cooldown - Clock.GetElapsedTime(cooldownStart);
                if (blocked > TimeSpan.FromSeconds(2))
                    throw new BookProviderException(BookFailure.RateLimited, "NDL 正在限流，请稍后重试；已保留现有资料。");
                if (blocked > wait) wait = blocked;
                if (wait <= TimeSpan.Zero) break; await (delay ?? Task.Delay)(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested(); started = true; lastStart = Clock.GetTimestamp(); return new Lease(queue);
        }
        catch { queue.Release(); throw; }
    }
    // Called while holding the lease; monotonic time avoids wall-clock jumps.
    internal void Cooldown(TimeSpan duration)
    {
        var remaining = cooldown - Clock.GetElapsedTime(cooldownStart);
        if (duration > remaining) { cooldownStart = Clock.GetTimestamp(); cooldown = duration; }
    }
    private sealed class Lease(SemaphoreSlim queue) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Release(); } }
}
