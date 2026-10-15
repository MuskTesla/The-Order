using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

// Adversarial contract tests. FindingB* assert the required safety invariant,
// deliberately retaining baseline failures as evidence. All inputs are synthetic;
// HTTP is intercepted in memory and no real profile or credentials are opened.
public sealed class AcgnRedTeamIdentityTests
{
    [Fact]
    public async Task FindingB01_RelationFieldUrlMustNotBecomeSameWorkIdentity()
    {
        using var infobox = JsonDocument.Parse("""{"infobox":[{"key":"续作","value":"https://anilist.co/anime/4181"}]}""");
        var links = AniListBridgeLinks.Map(infobox.RootElement, BangumiRelationKind.Anime);
        var source = new AniListSource("51", AniListMediaKind.Anime, "合成作品 第一季", "Synthetic Season One",
            "2024-01-01", "TV", 12, links);
        var sequel = AniListTestData.Media() with { Id = 4181, Title = "Synthetic Season Two", NativeTitle = "合成作品 第二季",
            EnglishTitle = "", Synonyms = [], StartDate = "2024-10-01", Episodes = 12 };
        var provider = new AniListFakeProvider { Results = [sequel] };
        var match = await new AniListService(provider).MatchAsync(source, null, false, default);
        Assert.Null(match.Binding);
    }

    [Fact]
    public async Task FindingB02_TruncatedAnimeSearchMustNotPersistUniqueWinner()
    {
        var source = AniListTestData.Source() with { Date = "2007-10-05", Episodes = 23 };
        var rows = Enumerable.Range(0, 10).Select(i =>
        {
            var node = JsonNode.Parse(AniListTestData.MediaJson)!;
            if (i > 0)
            {
                node["id"] = 9000 + i; node["startDate"]!["year"] = 2010;
            }
            return node;
        }).ToArray();
        var queries = new List<string>();
        using var handler = new AniListTestData.Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var query = body.RootElement.GetProperty("query").GetString()!; queries.Add(query);
            return query.Contains("Page(page:1", StringComparison.Ordinal)
                ? AniListTestData.Response(JsonSerializer.Serialize(new { data = new { Page = new { pageInfo = new { hasNextPage = true, total = 11 }, media = rows } } }))
                : AniListTestData.Response("{\"data\":{\"Media\":" + AniListTestData.DetailJson + "}}");
        });
        using var client = new HttpClient(handler);
        var disk = new AniListMemoryCache();
        var service = new AniListService(new AniListProvider(client, new AniListTestData.Clock().Rate()), disk);
        var match = await service.MatchAsync(source, null, false, default);
        Assert.Equal(10, match.Candidates.Length);
        Assert.Contains(queries, x => x.Contains("Page(page:1,perPage:10)", StringComparison.Ordinal));
        Assert.Null(match.Binding);
        Assert.DoesNotContain(disk.Values.Values, x => x.Binding is not null);
    }

    [Fact]
    public void FindingB03_ConflictingReleaseGroupsMustVetoAutomaticAlbumBinding()
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds =
            [new(MusicBrainzEntityKind.ReleaseGroup, MusicTestData.Group), new(MusicBrainzEntityKind.ReleaseGroup, MusicTestData.Other)] } };
        Assert.True(source.IsValid);
        Assert.True(MusicBrainzMatching.Assess(source, MusicTestData.Candidate()).HasIdentityConflict);
        Assert.Null(MusicBrainzMatching.Automatic(source, MusicTestData.Search()));
    }

    [Fact]
    public async Task FindingB04_ExplicitReleaseConflictMustNotBePersistedByManualConfirmation()
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds = [new(MusicBrainzEntityKind.Release, MusicTestData.Release)] } };
        var other = MusicTestData.Album(MusicTestData.Other);
        Assert.True(MusicBrainzMatching.Assess(source, other.Release).HasConflict);
        Assert.True(MusicBrainzMatching.Assess(source, other.Release).HasIdentityConflict);
        var disk = new MusicMemoryCache();
        var service = new MusicBrainzService(new MusicTestProvider(), disk);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(source, other, default));
        Assert.Empty(disk.Values);
    }

    [Fact]
    public async Task FindingB05_ChangedEditionMustBeReviewedAgainBeforePublicationConfirmation()
    {
        var selected = BookBibliographyServiceTests.Publication() with { Edition = "初版" };
        var reread = selected with { Edition = "増補改訂版" };
        var source = new BookSource(new("Bangumi", "48", BookEntityLevel.Publication), selected.Title, selected.BaseTitle,
            "小说", ["Key"], selected.Isbns, null);
        var store = new BookChoices();
        var service = new BookBibliographyService(new BookProvider(reread), choices: store);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(source, selected, default));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void FindingB06_LabelledIsbnMustRetainPublicationIdentityEvidence()
    {
        using var json = JsonDocument.Parse("""{"series":false,"infobox":[{"key":"ISBN","value":[{"k":"初版","v":"4-8402-3250-4"}]}]}""");
        var facts = BangumiBookFactsMapping.Map(json.RootElement);
        Assert.Contains("9784840232500", facts.Isbns);
        Assert.Equal(BookEntityLevel.Publication, facts.Level);
    }

    [Theory]
    [InlineData(MusicBrainzEntityKind.Recording)]
    [InlineData(MusicBrainzEntityKind.Work)]
    [InlineData(MusicBrainzEntityKind.Artist)]
    public void RelatedMusicEntityCannotAuthorizeAlbumEvenWithMatchingCatalogue(MusicBrainzEntityKind kind)
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds = [new(kind, MusicTestData.Recording)] } };
        var assessed = MusicBrainzMatching.Assess(source, MusicTestData.Candidate());
        Assert.False(assessed.HasConflict); Assert.False(assessed.Strong);
        Assert.Null(MusicBrainzMatching.Automatic(source, MusicTestData.Search()));
    }

    [Fact]
    public void DuplicateSameGroupClaimIsCompatibleButDifferentReleaseClaimsAreNot()
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds =
            [new(MusicBrainzEntityKind.ReleaseGroup, MusicTestData.Group), new(MusicBrainzEntityKind.ReleaseGroup, MusicTestData.Group)] } };
        Assert.Equal(MusicTestData.Release, MusicBrainzMatching.Automatic(source, MusicTestData.Search()));
        source = source with { Facts = source.Facts with { ExternalIds =
            [new(MusicBrainzEntityKind.Release, MusicTestData.Release), new(MusicBrainzEntityKind.Release, MusicTestData.Other)] } };
        Assert.True(MusicBrainzMatching.Assess(source, MusicTestData.Candidate()).HasConflict);
        Assert.Null(MusicBrainzMatching.Automatic(source, MusicTestData.Search()));
    }

    [Fact]
    public async Task ConflictingMusicPreviewCannotConfirmAndOldConflictingBindingIsNotDisplayed()
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds = [new(MusicBrainzEntityKind.Release, MusicTestData.Release)] } };
        var provider = new MusicTestProvider { Search = (_, _) => Task.FromResult(new MusicBrainzSearchResult([MusicTestData.Candidate(MusicTestData.Other)], 1, false, null, null)) };
        var disk = new MusicMemoryCache();
        using (var vm = new MusicAlbumViewModel(new MusicSubjectReader(source.Facts), new MusicBrainzService(provider, disk)))
        {
            await vm.OpenAsync(MusicTestData.Item()); await vm.PreviewAsync(MusicTestData.Other);
            Assert.Equal(MusicAlbumState.Preview, vm.State); Assert.False(vm.CanConfirm); Assert.Contains("不能确认", vm.Message);
            await vm.ConfirmAsync(); Assert.DoesNotContain(disk.Values.Values, x => x.Binding is not null);
        }
        await disk.WriteAsync(new("binding:" + source.Fingerprint, DateTimeOffset.UtcNow, Binding: new(source.Fingerprint, MusicTestData.Other)), default);
        using var reopened = new MusicAlbumViewModel(new MusicSubjectReader(source.Facts), new MusicBrainzService(provider, disk));
        await reopened.OpenAsync(MusicTestData.Item());
        Assert.Equal(MusicAlbumState.Choosing, reopened.State); Assert.Null(reopened.Album); Assert.Contains("冲突", reopened.Message);
        Assert.Single(disk.Values.Values, x => x.Binding is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameGroupReissueCanBeExplicitlyConfirmedAndReopenedWithDifferenceWarning(bool digital)
    {
        var original = MusicTestData.Source();
        var source = original with { Facts = original.Facts with { ExternalIds = [new(MusicBrainzEntityKind.ReleaseGroup, MusicTestData.Group)] } };
        var reissue = MusicTestData.Album(MusicTestData.Other);
        var discs = digital ? reissue.Discs.Take(1).Select(x => x with { Format = "Digital Media" }).ToArray() : reissue.Discs;
        reissue = reissue with { Discs = discs, Release = reissue.Release with { Date = "2025-01-01", Labels = [new("Key Sounds Label", "REISSUE-2025")],
            DiscCount = discs.Length, TrackCount = discs.Sum(x => x.Tracks.Length) } };
        var assessment = MusicBrainzMatching.Assess(source, reissue.Release);
        Assert.True(assessment.HasConflict); Assert.False(assessment.HasIdentityConflict); Assert.False(assessment.Strong);
        var search = new MusicBrainzSearchResult([reissue.Release], 1, false, null, null);
        Assert.Null(MusicBrainzMatching.Automatic(source, search));
        var provider = new MusicTestProvider { Search = (_, _) => Task.FromResult(search), Load = (_, _) => Task.FromResult(reissue) };
        var disk = new MusicMemoryCache();
        using (var model = new MusicAlbumViewModel(new MusicSubjectReader(source.Facts), new MusicBrainzService(provider, disk)))
        {
            await model.OpenAsync(MusicTestData.Item()); Assert.Equal(MusicAlbumState.Choosing, model.State);
            await model.PreviewAsync(MusicTestData.Other);
            Assert.True(model.CanConfirm); Assert.Contains("核对版本", model.Message);
            await model.ConfirmAsync(); Assert.Equal(MusicAlbumState.Ready, model.State);
            Assert.Equal(MusicTestData.Other, model.Album?.Album.Release.Id);
        }
        using var reopened = new MusicAlbumViewModel(new MusicSubjectReader(source.Facts), new MusicBrainzService(provider, disk));
        await reopened.OpenAsync(MusicTestData.Item());
        Assert.Equal(MusicAlbumState.Ready, reopened.State); Assert.Equal(MusicTestData.Other, reopened.Album?.Album.Release.Id);
        Assert.Contains("核对版本", reopened.Message);
        Assert.Single(disk.Values.Values, x => x.Binding?.ReleaseId == MusicTestData.Other);
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("date")]
    [InlineData("publisher")]
    [InlineData("language")]
    [InlineData("set-isbn")]
    public async Task ChangedPublicationIdentityFactsRequireNewReview(string changed)
    {
        var selected = BookBibliographyServiceTests.Publication();
        var reread = changed switch
        {
            "volume" => selected with { Volume = "2" }, "date" => selected with { PublicationDate = "2006.11" },
            "publisher" => selected with { Publishers = ["Other publisher"] }, "language" => selected with { Languages = ["eng"] },
            _ => selected with { SetIsbns = ["9784044292010"] }
        };
        var source = new BookSource(new("Bangumi", "48", BookEntityLevel.Publication), selected.Title, selected.BaseTitle,
            "小说", ["Key"], selected.Isbns, null);
        var store = new BookChoices();
        await Assert.ThrowsAsync<InvalidDataException>(() => new BookBibliographyService(new BookProvider(reread), choices: store).ConfirmAsync(source, selected, default));
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task ReorderedIdentifierSetsAndNonIdentityAnnotationsDoNotInvalidatePublicationReview()
    {
        var selected = BookBibliographyServiceTests.Publication() with { SetIsbns = ["9784840232500", "9784044292010"],
            Publishers = ["Publisher A", "Publisher B"], Languages = ["jpn", "eng"] };
        var reread = selected with { SetIsbns = selected.SetIsbns.Reverse().ToArray(), Publishers = selected.Publishers.Reverse().ToArray(),
            Languages = selected.Languages.Reverse().ToArray(), CreatorStatements = ["Updated contributor wording"],
            BibliographicSeries = ["New series annotation"], FetchedAtUtc = selected.FetchedAtUtc.AddHours(1) };
        var source = new BookSource(new("Bangumi", "48", BookEntityLevel.Publication), selected.Title, selected.BaseTitle,
            "小说", ["Key"], selected.Isbns, null);
        var store = new BookChoices();
        await new BookBibliographyService(new BookProvider(reread), choices: store).ConfirmAsync(source, selected, default);
        Assert.Single(store.Values);
    }

    [Fact]
    public void Control_HomonymsAndGameAdaptationKeepDistinctProviderMediaIdentities()
    {
        var game = new CatalogEntityRef(CatalogEntityKind.Work, "51", "同名作品", Relation: "adaptation_of") { KunGalGalgameId = "51" };
        var anime = new BangumiRelationItem("51", BangumiRelationKind.Anime, "同名作品", "同名作品", "动画", null);
        var other = anime with { Id = "52" };
        var result = RelatedWorksProjection.Build([game], [anime, other], gameIdentities: [new("51", "51", "51")]);
        Assert.Equal(3, result.Length); Assert.Equal(3, result.Select(x => x.Key).Distinct().Count());
        Assert.All(result, x => Assert.Null(x.LinkedBangumiSubjectId));
    }

    [Theory]
    [InlineData("MOVIE")]
    [InlineData("OVA")]
    public void Control_AnimeTvCannotBindMovieOrOva(string format)
    {
        var source = AniListTestData.Source() with { Date = "2007-10-05", Episodes = 23 };
        Assert.Null(AniListMatching.AutomaticCandidate(source, [AniListTestData.Media() with { Format = format }]));
    }

    [Fact]
    public void Control_BookSeriesVolumePublicationAndIsbnRemainSeparate()
    {
        var series = new BookIdentity("Bangumi", "43194", BookEntityLevel.Series);
        var volume = new BookIdentity("Bangumi", "43194", BookEntityLevel.Volume);
        var publication = new BookIdentity("NDL", "000008005812", BookEntityLevel.Publication);
        Assert.NotEqual(series.Key, volume.Key); Assert.NotEqual(volume.Key, publication.Key);
        Assert.Equal("9784840232500", BookIsbn.Normalize("4-8402-3250-4"));
        var source = new AniListSource("43194", AniListMediaKind.Manga, "同名小说", "同名小说", null, "轻小说", null, []) { BookLevel = BookEntityLevel.Volume };
        var manga = AniListTestData.Media() with { Kind = AniListMediaKind.Manga, Format = "MANGA", Volumes = 2 };
        Assert.True(AniListMatching.Assess(source, manga).Conflict);
        Assert.Null(AniListMatching.AutomaticCandidate(source, [manga]));
    }

    [Fact]
    public void Control_AlbumReleaseRecordingWorkAndTrackPositionsRemainSeparate()
    {
        var sameId = MusicTestData.Release;
        var references = Enum.GetValues<MusicBrainzEntityKind>().Select(k => new MusicBrainzReference(k, sameId)).ToArray();
        Assert.Equal(5, references.Distinct().Count()); Assert.Equal(5, references.Select(x => x.Url).Distinct().Count());
        var album = MusicTestData.Album();
        Assert.True(MusicBrainzValidation.Album(album));
        Assert.Equal(3, album.Discs.Length);
        Assert.Single(album.Discs.SelectMany(d => d.Tracks).Select(t => t.RecordingId).Distinct());
    }

    [Fact]
    public void Control_StaffHomonymsAndAliasesKeepProviderPersonIds()
    {
        var node = JsonNode.Parse(AniListTestData.DetailJson)!;
        node["staff"]!["edges"] = JsonNode.Parse("""[{"role":"Story","node":{"id":100,"name":{"full":"Same Name","native":"同名"}}},{"role":"Story","node":{"id":101,"name":{"full":"Same Name","native":"同名"}}},{"role":"Art","node":{"id":100,"name":{"full":"Alias Name","native":"別名"}}}]""");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        var details = AniListProvider.MapDetails(doc.RootElement, 1, AniListTestData.Stamp);
        Assert.Equal(3, details.Staff.Length); Assert.Equal(2, details.Staff.Select(x => x.Person.Id).Distinct().Count());
        Assert.Equal(3, details.Staff.Select(x => (x.Person.Id, x.Role)).Distinct().Count());
    }

    [Fact]
    public async Task Control_CyclicAnimeGraphIsLazyBoundedAndReturnsToExactRoot()
    {
        var provider = new AniListFakeProvider { Fetch = (id, page, _) => Task.FromResult(AniListTestData.Details(id, page) with
            { Relations = [new(AniListTestData.Media() with { Id = id == 2167 ? 4181 : 2167 }, "SEQUEL")] }) };
        using var model = new AniListDetailsViewModel(new AniListBridgeReader(), new AniListService(provider));
        await model.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null));
        await model.PreviewAsync(2167); await model.ConfirmAsync();
        for (var i = 0; i < 25; i++) await model.ExploreAsync(model.Displayed!.Details.Relations[0], i);
        Assert.Equal(20, model.HistoryDepth);
        Assert.InRange(provider.Fetches, 1, 2);
        for (var i = 0; i < 20; i++) Assert.True(model.GoBack());
        Assert.False(model.GoBack()); Assert.True(model.IsBridge); Assert.Equal(2167, model.Media!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenConflictingIdentityBeyondParserLimitCannotReuseOrCreateBinding(bool overflowFields)
    {
        using var data = BoundaryIdentitySubject(overflowFields, true);
        var subject = BangumiSubjectDetailsProvider.Map("51", BangumiRelationKind.Anime, data.RootElement, AniListTestData.Stamp);
        Assert.True(subject.HasAmbiguousExternalIdentity);
        Assert.Equal("Bangumi summary remains available", subject.Summary);
        Assert.Equal(["https://anilist.co/anime/2167"], subject.ExternalLinks);
        var source = AniListSource.From(subject);
        Assert.True(source.IsValid); Assert.True(source.HasAmbiguousExternalIdentity);
        Assert.NotEqual((source with { HasAmbiguousExternalIdentity = false }).Fingerprint, source.Fingerprint);
        Assert.True(AniListMatching.Assess(source, AniListTestData.Media()).Conflict);
        Assert.False(AniListMatching.StrongEvidence(source, AniListTestData.Media()));
        var disk = new AniListMemoryCache();
        var key = "binding:" + source.Fingerprint;
        var previous = new AniListCacheValue(key, AniListTestData.Stamp,
            Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认 · 历史选择"));
        disk.Values[key] = previous;
        var provider = new AniListFakeProvider();
        var service = new AniListService(provider, disk);
        var match = await service.MatchAsync(source, null, false, default);
        Assert.Null(match.Binding); Assert.Single(match.Candidates); Assert.Contains("歧义", match.Notice);
        Assert.Equal(0, provider.Resolves); Assert.Equal(1, provider.Searches);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(source, AniListTestData.Details(), default));
        Assert.Equal(previous, disk.Values[key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactParserLimitOfDuplicateIdentityClaimsRemainsUsable(bool maximumFields)
    {
        using var data = BoundaryIdentitySubject(maximumFields, false);
        var subject = BangumiSubjectDetailsProvider.Map("51", BangumiRelationKind.Anime, data.RootElement, AniListTestData.Stamp);
        Assert.False(subject.HasAmbiguousExternalIdentity);
        var source = AniListSource.From(subject);
        Assert.Single(source.ExternalLinks); Assert.False(source.HasAmbiguousExternalIdentity);
        var provider = new AniListFakeProvider();
        var result = await new AniListService(provider).MatchAsync(source, null, false, default);
        Assert.Equal(2167, result.Binding?.MediaId); Assert.Equal(1, provider.Resolves); Assert.Equal(0, provider.Searches);
    }

    private static JsonDocument BoundaryIdentitySubject(bool fields, bool overflow)
    {
        var infobox = new JsonArray();
        const string accepted = "https://anilist.co/anime/2167", conflicting = "https://anilist.co/anime/4181";
        if (fields)
        {
            for (var i = 0; i < 200; i++) infobox.Add(new JsonObject { ["key"] = "url", ["value"] = accepted });
            if (overflow) infobox.Add(new JsonObject { ["key"] = "url", ["value"] = conflicting });
        }
        else
        {
            var values = new JsonArray();
            for (var i = 0; i < 30; i++) values.Add(new JsonObject { ["k"] = "AniList", ["v"] = accepted });
            if (overflow) values.Add(new JsonObject { ["k"] = "AniList", ["v"] = conflicting });
            infobox.Add(new JsonObject { ["key"] = "url", ["value"] = values });
        }
        return JsonDocument.Parse(new JsonObject { ["id"] = 51, ["type"] = 2, ["name"] = "CLANNAD", ["name_cn"] = "CLANNAD",
            ["summary"] = "Bangumi summary remains available", ["nsfw"] = false, ["date"] = "2007-10-05", ["platform"] = "TV",
            ["eps"] = 23, ["infobox"] = infobox }.ToJsonString());
    }

    private sealed class BookChoices : IBookPublicationBindings
    {
        public BookPublicationBinding[] Values = [];
        public Task<BookPublicationBinding[]> ReadAsync(BookIdentity source, CancellationToken cancellation) => Task.FromResult(Values);
        public Task WriteAsync(BookIdentity source, BookPublicationBinding[] bindings, CancellationToken cancellation) { Values = bindings; return Task.CompletedTask; }
    }
    private sealed class BookProvider(BookPublication publication) : IBookBibliographyProvider
    {
        public Task<BookPublication> FetchAsync(string id, CancellationToken cancellation) => Task.FromResult(publication);
        public Task<BookSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellation) => throw new NotSupportedException();
    }
    private sealed class MusicSubjectReader(BangumiMusicFacts facts) : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string id, BangumiRelationKind kind, bool refresh, CancellationToken cancellation)
            => Task.FromResult(new BangumiSubjectDetailsResult(MusicTestData.Details(id) with { MusicFacts = facts }, false, false, null));
    }
}
