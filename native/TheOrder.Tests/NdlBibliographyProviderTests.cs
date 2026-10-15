using System.Diagnostics;
using System.Net;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

// Small synthetic bibliographic records, not copies of user data or book content.
// Schema: https://ndlsearch.ndl.go.jp/renkei/dcndl/version3
// ISBN examples are public identifiers from NDL's CC BY 4.0 bibliographic API;
// no images, descriptions, hundreds of records, or third-party metadata are bundled.
public sealed class NdlBibliographyProviderTests
{
    private const string Id = "000004166200", Isbn = "9784044292010";
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
    private const string Namespaces = "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:dcndl=\"http://ndl.go.jp/dcndl/terms/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:foaf=\"http://xmlns.com/foaf/0.1/\"";
    private static string Rdf(string id = Id, string isbnKind = "ISBN", string isbn = "4-04-429201-9", string extra = "", string date = "2003.6", string volume = "", string title = "合成作品", string category = "R100000002", string status = "C7", string rights = "https://creativecommons.org/licenses/by/4.0/", string owner = "国立国会図書館") => $$"""
        <rdf:RDF {{Namespaces}}>
          <dcndl:BibAdminResource rdf:about="https://ndlsearch.ndl.go.jp/books/R100000002-I{{id}}">
            <dcndl:record rdf:resource="https://ndlsearch.ndl.go.jp/books/R100000002-I{{id}}#material" />
            <dcndl:catalogingStatus>{{status}}</dcndl:catalogingStatus>
            <dcndl:bibRecordCategory>{{category}}</dcndl:bibRecordCategory>
            <dcndl:rights rdf:resource="{{rights}}" />
            <dcterms:provenance><rdf:Description><dcndl:bibRecordCategory>{{category}}</dcndl:bibRecordCategory><dcterms:contributor>{{owner}}</dcterms:contributor></rdf:Description></dcterms:provenance>
          </dcndl:BibAdminResource>
          <dcndl:BibResource rdf:about="https://ndlsearch.ndl.go.jp/books/R100000002-I{{id}}#material">
            <dcterms:identifier rdf:datatype="http://ndl.go.jp/dcndl/terms/NDLBibID">{{id}}</dcterms:identifier>
            <dcterms:identifier rdf:datatype="http://ndl.go.jp/dcndl/terms/{{isbnKind}}">{{isbn}}</dcterms:identifier>
            <dcterms:title>{{title}}</dcterms:title><dc:title><rdf:Description><rdf:value>{{title}}</rdf:value><dcndl:transcription>ゴウセイ</dcndl:transcription></rdf:Description></dc:title>
            <dc:creator>合成作者 原作</dc:creator><dc:creator>合成絵師 イラスト</dc:creator>
            <dcterms:publisher><foaf:Agent><foaf:name>合成出版社</foaf:name></foaf:Agent></dcterms:publisher>
            <dcndl:seriesTitle><rdf:Description><rdf:value>合成文庫 ; 1215</rdf:value></rdf:Description></dcndl:seriesTitle>
            <dcterms:date>{{date}}</dcterms:date><dcterms:issued>2003</dcterms:issued><dcterms:language>jpn</dcterms:language>
            <dcndl:materialType rdf:resource="http://ndl.go.jp/ndltype/Book" />
            {{volume}}{{extra}}
          </dcndl:BibResource>
          <dcndl:Item><dc:creator>不得混入的第三方作者</dc:creator><dcterms:publisher><foaf:Agent><foaf:name>第三方出版者</foaf:name></foaf:Agent></dcterms:publisher><dcterms:date>9999</dcterms:date></dcndl:Item>
        </rdf:RDF>
        """;
    private static string Search(string[]? rows = null, int total = 1, string? next = null)
    {
        rows ??= [Rdf()];
        return "<searchRetrieveResponse xmlns=\"http://www.loc.gov/zing/srw/\"><numberOfRecords>" + total + "</numberOfRecords>"
            + (next is null ? "" : "<nextRecordPosition>" + next + "</nextRecordPosition>") + "<records>"
            + string.Join("", rows.Select(r => "<record><recordPacking>xml</recordPacking><recordData>" + r + "</recordData></record>")) + "</records></searchRetrieveResponse>";
    }
    private static string Oai(string id = Id, string? rdf = null) => "<OAI-PMH xmlns=\"http://www.openarchives.org/OAI/2.0/\"><GetRecord><record><header><identifier>oai:ndlsearch.ndl.go.jp:R100000002-I" + id + "</identifier></header><metadata>" + (rdf ?? Rdf(id)) + "</metadata></record></GetRecord></OAI-PMH>";
    private static string Diagnostic(string message) => "<searchRetrieveResponse xmlns=\"http://www.loc.gov/zing/srw/\"><diagnostics><diagnostic xmlns=\"http://www.loc.gov/zing/srw/diagnostic/\"><message>" + message + "</message></diagnostic></diagnostics></searchRetrieveResponse>";
    private static BookSearchPage Parse(string xml, string? isbn = null, int start = 1) => NdlBibliographyProvider.ParseSearch(NdlBibliographyProvider.ReadXml(Encoding.UTF8.GetBytes(xml)), new("合成作品", isbn, Start: start), Stamp);
    private static HttpResponseMessage Reply(string xml, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { Calls++; return send(request, cancellation); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = Stamp;
        public List<TimeSpan> Waits { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        public Task Delay(TimeSpan wait, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Waits.Add(wait); now += wait; return Task.CompletedTask; }
        public NdlRequestCoordinator Rate() => new(this, Delay);
    }
    private static NdlBibliographyProvider Provider(HttpClient client, Clock? clock = null, TimeSpan? timeout = null)
    { clock ??= new(); return new(client, clock.Rate(), timeout, clock.GetUtcNow); }

    [Fact] public void SourceFieldsKeepPartialDatesImprintsCreatorStatementsAndIgnoreThirdPartyItems()
    {
        var page = Parse(Search([Rdf(date: "2014.8 (33版)", volume: "<dcndl:volume><rdf:Description><rdf:value>鬼隠し編 1</rdf:value></rdf:Description></dcndl:volume>", extra: "<dcndl:edition>増補版</dcndl:edition>")]));
        var book = Assert.Single(page.Publications); Assert.True(BookValidation.Page(page)); Assert.Equal(Id, book.Id); Assert.Equal("合成作品", book.BaseTitle);
        Assert.Equal("鬼隠し編 1", book.Volume); Assert.Equal("2014.8 (33版)", book.PublicationDate); Assert.Equal("増補版", book.Edition);
        Assert.Equal([Isbn], book.Isbns); Assert.Equal(["合成作者 原作", "合成絵師 イラスト"], book.CreatorStatements);
        Assert.Equal(["合成出版社"], book.Publishers); Assert.Equal(["合成文庫 ; 1215"], book.BibliographicSeries); Assert.Equal(["jpn"], book.Languages);
        Assert.False(book.IsAggregate); Assert.Equal(BookEntityLevel.Publication, book.Identity.Level); Assert.Equal("CC BY 4.0", book.License);
    }
    [Fact] public void IsbnTenAndThirteenNormalizeButSetAndErrorIdentifiersDoNotBecomePublicationIsbns()
    {
        var extra = "<dcterms:identifier rdf:datatype=\"http://ndl.go.jp/dcndl/terms/ISBN\">9784044292010</dcterms:identifier>"
            + "<dcterms:identifier rdf:datatype=\"http://ndl.go.jp/dcndl/terms/SetISBN\">9784088927121</dcterms:identifier>"
            + "<dcterms:identifier rdf:datatype=\"http://ndl.go.jp/dcndl/terms/ErrorISBN\">0-000-00000-0</dcterms:identifier>";
        var book = Assert.Single(Parse(Search([Rdf(extra: extra)]), Isbn).Publications); Assert.Equal([Isbn], book.Isbns); Assert.Equal(["9784088927121"], book.SetIsbns); Assert.False(book.IsAggregate);
    }
    [Theory] [InlineData("ErrorISBN", "0-000-00000-0", "9780000000002")] [InlineData("SetISBN", "9784088927121", "9784088927121")] [InlineData("ISBN", "9784088518312", Isbn)]
    public void WrongErrorOrSetIsbnHitsAreInvalidResponsesRatherThanEmptySuccessfulLookups(string datatype, string remote, string requested)
    { Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search([Rdf(isbnKind: datatype, isbn: remote)]), requested)).Failure); }
    [Fact] public void SameIsbnDifferentNdlIdsRemainSeparateCandidatesAndIdenticalIdsDeduplicate()
    {
        var a = Rdf(); var b = Rdf(id: "000002217748"); var result = Parse(Search([a, b, a], 3), Isbn);
        Assert.Equal(2, result.Publications.Length); Assert.Equal([Id, "000002217748"], result.Publications.Select(x => x.Id).ToArray());
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search([a, Rdf(title: "矛盾する作品")], 2))).Failure);
    }
    [Fact] public void MultipleOrdinaryIsbnsAndOpenPublicationRangesAreAggregatesNotInventedVolumes()
    {
        var multiple = Rdf(extra: "<dcterms:identifier rdf:datatype=\"http://ndl.go.jp/dcndl/terms/ISBN\">9784088518312</dcterms:identifier>");
        foreach (var xml in new[] { multiple, Rdf(date: "2003-"), Rdf(extra: "<dcndl:volumeRange>1-</dcndl:volumeRange>") })
        { var book = Assert.Single(Parse(Search([xml])).Publications); Assert.True(book.IsAggregate); Assert.Equal(BookEntityLevel.Aggregate, book.Identity.Level); Assert.Null(book.Volume); }
        Assert.False(Assert.Single(Parse(Search([Rdf(date: "2026-10-15")])).Publications).IsAggregate);
    }
    [Theory] [InlineData("C3", "R100000002", "https://creativecommons.org/licenses/by/4.0/", "国立国会図書館")]
    [InlineData("C7", "R100000137", "https://creativecommons.org/licenses/by/4.0/", "国立国会図書館")]
    [InlineData("C7", "R100000002", "https://creativecommons.org/licenses/by-nc/4.0/", "国立国会図書館")]
    [InlineData("C7", "R100000002", "https://creativecommons.org/licenses/by/4.0/", "第三方机构")]
    public void UnfinishedThirdPartyOrUnlicensedMetadataIsRejected(string status, string category, string rights, string owner)
    { Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search([Rdf(status: status, category: category, rights: rights, owner: owner)]))).Failure); }
    [Fact] public void BookSourceAndExactAdminResourceMustAgreeRatherThanTrustingRemoteLinks()
    {
        foreach (var xml in new[] { Rdf().Replace("http://ndl.go.jp/ndltype/Book", "http://ndl.go.jp/ndltype/Sound"), Rdf().Replace("R100000002-I" + Id + "#material", "R100000137-I" + Id + "#material"), Rdf().Replace("<dcndl:rights", "<dcndl:absentRights") })
            Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search([xml]))).Failure);
    }
    [Fact] public void LegalEmptyDiagnosticIsDistinctFromSyntaxServerOrMalformedHttp200()
    {
        Assert.Empty(Parse(Diagnostic("Record does not exist")).Publications); Assert.Empty(Parse(Search([], 0), start: 81).Publications);
        foreach (var xml in new[] { Diagnostic("illegal query syntax"), Diagnostic("An error occurred"), "<searchRetrieveResponse xmlns=\"http://www.loc.gov/zing/srw/\"/>", Search([], 5), Search([Rdf()], 0), Search([Rdf()], 21, "0") })
            Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(xml)).Failure);
    }
    [Fact] public void PaginationUsesRawRecordsAndSmallerPagesRetainTheHundredRecordLimit()
    {
        Assert.Equal(3, Parse(Search([Rdf(), Rdf()], 30)).NextStart);
        var rows = Enumerable.Range(1, 20).Select(i => Rdf(id: i.ToString("D12"))).ToArray();
        Assert.Equal(21, Parse(Search(rows, 200)).NextStart); Assert.Null(Parse(Search(rows, 200), start: 81).NextStart);
        Assert.Equal(91, Parse(Search(rows[..10], 200), start: 81).NextStart);
        Assert.Null(Parse(Search(rows[..10], 200), start: 91).NextStart);
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search(rows, 200), start: 91)).Failure);
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search(rows.Concat([Rdf()]).ToArray(), 200))).Failure);
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Parse(Search([Rdf()], 20, "999"))).Failure);
    }
    [Theory] [InlineData(Id)] [InlineData("030560687")]
    public async Task OaiUsesOriginalNineOrTwelveDigitIdentityAndTheSameSourceValidation(string id)
    {
        using var handler = new Handler((request, _) =>
        {
            var uri = request.RequestUri!; Assert.Equal("/api/oaipmh", uri.AbsolutePath); Assert.Contains("verb=GetRecord", uri.Query); Assert.Contains("metadataPrefix=dcndl_v3", uri.Query);
            Assert.Contains("oai:ndlsearch.ndl.go.jp:R100000002-I" + id, Uri.UnescapeDataString(uri.Query)); return Task.FromResult(Reply(Oai(id)));
        }); using var http = new HttpClient(handler); var item = await Provider(http).FetchAsync(id, default); Assert.Equal(id, item.Id); Assert.Equal(1, handler.Calls);
    }
    [Fact] public void OaiCannotSubstituteAnotherIdDeletedOrUnlicensedRecord()
    {
        BookPublication Fetch(string xml) => NdlBibliographyProvider.ParseRecord(NdlBibliographyProvider.ReadXml(Encoding.UTF8.GetBytes(xml)), Id, Stamp);
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Fetch(Oai(rdf: Rdf(id: "000002217748")))).Failure);
        Assert.Equal(BookFailure.InvalidResponse, Assert.Throws<BookProviderException>(() => Fetch(Oai(rdf: Rdf(status: "C3")))).Failure);
        Assert.Equal(BookFailure.Unavailable, Assert.Throws<BookProviderException>(() => Fetch("<OAI-PMH xmlns=\"http://www.openarchives.org/OAI/2.0/\"><error code=\"idDoesNotExist\">missing</error></OAI-PMH>")).Failure);
        Assert.Equal(BookFailure.Unavailable, Assert.Throws<BookProviderException>(() => Fetch(Oai().Replace("<header>", "<header status=\"deleted\">"))).Failure);
    }
    [Fact] public async Task SearchIsFixedOriginAnonymousBoundedAndCqlEscaped()
    {
        var title = "CLANNAD\" AND dpid = \"jpro";
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("ndlsearch.ndl.go.jp", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme); Assert.Equal("/api/sru", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.Null(request.Content);
            var q = Uri.UnescapeDataString(request.RequestUri.Query); Assert.Contains("maximumRecords=10", q); Assert.Contains("startRecord=81", q); Assert.Contains("onlyBib=true", q);
            var cql = Uri.UnescapeDataString(request.RequestUri.Query.Split('&').Single(x => x.StartsWith("query=", StringComparison.Ordinal))[6..]);
            Assert.Equal("dpid = \"iss-ndl-opac-national\" AND mediatype = \"books\" AND title = \"CLANNAD\\\" AND dpid = \\\"jpro\"", cql);
            return Task.FromResult(Reply(Search([Rdf()], 81)));
        }); using var http = new HttpClient(handler); http.DefaultRequestHeaders.Accept.Add(new("application/xml")); http.DefaultRequestHeaders.UserAgent.ParseAdd("TheOrder-Test/1.0");
        Assert.Single((await Provider(http).SearchAsync(new(title, Start: 81), default)).Publications);
    }
    [Theory] [InlineData("Authorization")] [InlineData("Cookie")] [InlineData("X-Api-Key")] [InlineData("X-Secret-Token")]
    public async Task OtherProviderCredentialsNeverReachTheNetwork(string header)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send")); using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation(header, "synthetic-sensitive-value");
        var error = await Assert.ThrowsAsync<BookProviderException>(() => Provider(http).SearchAsync(new("合成作品"), default)); Assert.Equal(BookFailure.InvalidResponse, error.Failure); Assert.Equal(0, handler.Calls); Assert.DoesNotContain("synthetic-sensitive-value", error.Message);
    }
    [Fact] public async Task InvalidRequestAndCancelledRequestNeverSend()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send")); using var http = new HttpClient(handler); var provider = Provider(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.FetchAsync("0000/secret", default)); await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync(new("title", Start: 92), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync(new("title", "4044292019"), default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(new("合成作品"), cancel.Token)); Assert.Equal(0, handler.Calls);
    }
    [Fact] public void EveryProductionProviderUsesTheSameGlobalQueue()
    { using var http = new HttpClient(); Assert.Same(NdlRequestCoordinator.Shared, new NdlBibliographyProvider(http).Coordinator); Assert.Same(new NdlBibliographyProvider(http).Coordinator, new NdlBibliographyProvider(http).Coordinator); }
    [Fact] public async Task OneShort429RetryAndOtherInstancesHonorTheGlobalCooldownAndSpacing()
    {
        var clock = new Clock(); var rate = clock.Rate(); var starts = new List<long>(); int sent = 0;
        using var handler = new Handler((request, _) =>
        {
            starts.Add(clock.GetTimestamp()); var response = Reply(request.RequestUri!.AbsolutePath == "/api/oaipmh" ? Oai() : Search(), ++sent == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (sent == 1) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2)); return Task.FromResult(response);
        }); using var http = new HttpClient(handler); var a = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow); var b = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow);
        await Task.WhenAll(a.SearchAsync(new("合成作品"), default), b.FetchAsync(Id, default));
        Assert.Equal(3, handler.Calls); Assert.True(starts[1] - starts[0] >= TimeSpan.FromSeconds(2).Ticks); Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1).Ticks));
    }
    [Fact] public async Task LongRetryAfterFailsFastAcrossInstancesWithoutSendingDuringCooldown()
    {
        var clock = new Clock(); var rate = clock.Rate(); int calls = 0; var starts = new List<long>();
        using var handler = new Handler((_, _) => { starts.Add(clock.GetTimestamp()); var r = Reply(Search(), ++calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK); if (calls == 1) r.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(10)); return Task.FromResult(r); });
        using var http = new HttpClient(handler); var a = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow); var b = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow);
        Assert.Equal(BookFailure.RateLimited, (await Assert.ThrowsAsync<BookProviderException>(() => a.SearchAsync(new("合成作品"), default))).Failure); Assert.Equal(1, calls);
        Assert.Equal(BookFailure.RateLimited, (await Assert.ThrowsAsync<BookProviderException>(() => b.SearchAsync(new("合成作品"), default))).Failure);
        Assert.Equal(1, calls); Assert.Empty(clock.Waits);
        await clock.Delay(TimeSpan.FromSeconds(10), default);
        await b.SearchAsync(new("合成作品"), default); Assert.Equal(2, calls); Assert.True(starts[1] - starts[0] >= TimeSpan.FromSeconds(10).Ticks);
    }
    [Fact] public async Task AStalledFirstAttemptCanRecoverWithoutResettingTheOperationDeadline()
    {
        int sent = 0, cancelled = 0; var clock = new Clock();
        using var handler = new Handler(async (_, cancellation) =>
        {
            if (++sent > 1) return Reply(Search());
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); throw new InvalidOperationException(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled++; throw; }
        }); using var http = new HttpClient(handler);
        var provider = new NdlBibliographyProvider(http, clock.Rate(), TimeSpan.FromSeconds(1), clock.GetUtcNow, TimeSpan.FromMilliseconds(40));
        Assert.Single((await provider.SearchAsync(new("合成作品"), default)).Publications);
        Assert.Equal(2, sent); Assert.Equal(1, cancelled); Assert.Contains(clock.Waits, x => x >= TimeSpan.FromSeconds(1));
    }
    [Fact] public async Task ConnectionFailureRetriesOnceAndPersistentStallsRemainBounded()
    {
        int sent = 0; var clock = new Clock();
        using var handler = new Handler((_, _) => ++sent == 1 ? throw new HttpRequestException("synthetic-private-details") : Task.FromResult(Reply(Oai())));
        using var http = new HttpClient(handler); Assert.Equal(Id, (await Provider(http, clock).FetchAsync(Id, default)).Id); Assert.Equal(2, sent);
        using var stall = new Handler(async (_, cancellation) => { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); throw new InvalidOperationException(); });
        using var stalledHttp = new HttpClient(stall);
        var provider = new NdlBibliographyProvider(stalledHttp, clock.Rate(), TimeSpan.FromSeconds(1), clock.GetUtcNow, TimeSpan.FromMilliseconds(40));
        Assert.Equal(BookFailure.Timeout, (await Assert.ThrowsAsync<BookProviderException>(() => provider.FetchAsync(Id, default))).Failure);
        Assert.Equal(2, stall.Calls);
    }
    [Fact] public async Task BusyQueueFailsPromptlyAndDoesNotReleaseTheActiveLease()
    {
        var clock = new Clock(); var rate = new NdlRequestCoordinator(clock, clock.Delay, TimeSpan.FromMilliseconds(40));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => { entered.TrySetResult(); return release.Task; }); using var http = new HttpClient(handler);
        var provider = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow);
        var first = provider.SearchAsync(new("合成作品"), default); await entered.Task;
        Assert.Equal(BookFailure.RateLimited, (await Assert.ThrowsAsync<BookProviderException>(() => provider.FetchAsync(Id, default))).Failure);
        Assert.Equal(1, handler.Calls); Assert.False(first.IsCompleted);
        release.SetResult(Reply(Search())); Assert.Single((await first).Publications);
    }
    [Fact] public async Task TransientServerRetryIsLimitedToOneAndSharesSpacing()
    {
        var clock = new Clock(); using var handler = new Handler((_, _) => Task.FromResult(Reply("", HttpStatusCode.ServiceUnavailable))); using var http = new HttpClient(handler);
        Assert.Equal(BookFailure.Server, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http, clock).SearchAsync(new("合成作品"), default))).Failure); Assert.Equal(2, handler.Calls); Assert.Contains(clock.Waits, x => x >= TimeSpan.FromSeconds(1));
    }
    [Fact] public async Task AShortRetryUsesTheSameCancellationBudgetAndDeadline()
    {
        int calls = 0, retryCancelled = 0;
        using var handler = new Handler(async (_, cancellation) =>
        {
            // Consume 700 ms of the one-second operation budget before retrying.
            // HttpClient wraps the operation token separately for each request;
            // test its cancellation effect, not the identity of those wrappers.
            if (++calls == 1) { await Task.Delay(700, cancellation); return Reply("", HttpStatusCode.BadGateway); }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); throw new InvalidOperationException(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { Interlocked.Exchange(ref retryCancelled, 1); throw; }
        }); using var http = new HttpClient(handler);
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(BookFailure.Timeout, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http, timeout: TimeSpan.FromSeconds(1)).SearchAsync(new("合成作品"), default))).Failure);
        elapsed.Stop();
        Assert.Equal(2, calls); Assert.Equal(1, Volatile.Read(ref retryCancelled));
        // A reset on retry would take at least 1.7 seconds. The generous 400 ms
        // scheduling margin still distinguishes it from the original deadline.
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1400));
    }
    [Fact] public async Task TimeoutIncludesQueueWaitAndDoesNotSendAfterTheDeadline()
    {
        var rate = new NdlRequestCoordinator(delay: (_, cancellation) => Task.Delay(Timeout.InfiniteTimeSpan, cancellation));
        using var handler = new Handler((_, _) => Task.FromResult(Reply(Search()))); using var http = new HttpClient(handler);
        await new NdlBibliographyProvider(http, rate).SearchAsync(new("合成作品"), default);
        Assert.Equal(BookFailure.Timeout, (await Assert.ThrowsAsync<BookProviderException>(() => new NdlBibliographyProvider(http, rate, TimeSpan.FromMilliseconds(30)).SearchAsync(new("合成作品"), default))).Failure); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task RequestTimeoutIsDistinctFromUserCancellationAndNetworkFailureIsRedacted()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); }); using var http = new HttpClient(handler);
        Assert.Equal(BookFailure.Timeout, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http, timeout: TimeSpan.FromMilliseconds(30)).FetchAsync(Id, default))).Failure);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http).FetchAsync(Id, cancel.Token));
        using var offline = new Handler((_, _) => throw new HttpRequestException("synthetic-secret-connection-details")); using var disconnected = new HttpClient(offline);
        var error = await Assert.ThrowsAsync<BookProviderException>(() => Provider(disconnected).FetchAsync(Id, default)); Assert.Equal(BookFailure.Offline, error.Failure); Assert.DoesNotContain("synthetic-secret", error.Message);
    }
    [Fact] public async Task CancellingOneQueuedCallerDoesNotReleaseAnotherCallersLeaseOrIssueItsRequest()
    {
        var clock = new Clock(); var rate = clock.Rate(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => { entered.TrySetResult(); return reply.Task; }); using var http = new HttpClient(handler); var provider = new NdlBibliographyProvider(http, rate, clock: clock.GetUtcNow);
        var first = provider.SearchAsync(new("合成作品"), default); await entered.Task; using var cancel = new CancellationTokenSource(); var queued = provider.SearchAsync(new("合成作品"), cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(1, handler.Calls); Assert.False(first.IsCompleted); reply.SetResult(Reply(Search())); Assert.Single((await first).Publications);
    }
    [Theory] [InlineData(301, BookFailure.InvalidResponse)] [InlineData(401, BookFailure.InvalidResponse)] [InlineData(404, BookFailure.Unavailable)] [InlineData(500, BookFailure.Server)]
    public async Task HttpErrorsNeverBecomeEmptySearchResults(int status, BookFailure expected)
    { using var handler = new Handler((_, _) => Task.FromResult(Reply(Search(), (HttpStatusCode)status))); using var http = new HttpClient(handler); Assert.Equal(expected, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http).SearchAsync(new("合成作品"), default))).Failure); Assert.Equal(1, handler.Calls); }
    [Fact] public async Task RedirectedResponseMalformedXmlWrongContentTypeAndInvalidUtf8AreRejected()
    {
        foreach (var kind in new[] { "redirect", "xml", "mime", "utf8" })
        {
            using var handler = new Handler((_, _) =>
            {
                var r = Reply(kind == "xml" ? "<broken>" : Search());
                if (kind == "redirect") r.RequestMessage = new(HttpMethod.Get, "https://example.invalid/secret");
                if (kind == "mime") r.Content.Headers.ContentType = new("text/html");
                if (kind == "utf8") { r.Content = new ByteArrayContent([0xff, 0xfe, 0xff]); r.Content.Headers.ContentType = new("text/xml"); }
                return Task.FromResult(r);
            }); using var http = new HttpClient(handler); Assert.Equal(BookFailure.InvalidResponse, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http).SearchAsync(new("合成作品"), default))).Failure);
        }
    }
    [Fact] public async Task DtdExternalEntitiesAndExcessiveXmlDepthAreBlocked()
    {
        foreach (var xml in new[] { "<!DOCTYPE searchRetrieveResponse [<!ENTITY xxe SYSTEM 'file:///must-never-open-private-profile'>]>" + Search().Replace("合成作品", "&xxe;"), "<!DOCTYPE searchRetrieveResponse [<!ENTITY a 'x'>]>" + Search(), string.Concat(Enumerable.Repeat("<x>", 40)) + string.Concat(Enumerable.Repeat("</x>", 40)) })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Reply(xml))); using var http = new HttpClient(handler); Assert.Equal(BookFailure.InvalidResponse, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http).SearchAsync(new("合成作品"), default))).Failure);
        }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ResponseSizeIsBoundedWithAndWithoutContentLength(bool length)
    {
        using var handler = new Handler((_, _) =>
        {
            var r = Reply(Search());
            if (length) r.Content.Headers.ContentLength = NdlBibliographyProvider.MaximumResponseBytes + 1;
            else { r.Content = new StreamContent(new NonSeekStream(new byte[NdlBibliographyProvider.MaximumResponseBytes + 1])); r.Content.Headers.ContentType = new("text/xml"); }
            return Task.FromResult(r);
        }); using var http = new HttpClient(handler); Assert.Equal(BookFailure.ResponseTooLarge, (await Assert.ThrowsAsync<BookProviderException>(() => Provider(http).SearchAsync(new("合成作品"), default))).Failure);
    }
    private sealed class NonSeekStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes, false);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) => source.ReadAsync(buffer, cancellation);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
