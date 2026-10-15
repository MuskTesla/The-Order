using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class MediaIdentityPersistenceTests
{
    private static AniListSource Source() => AniListTestData.Source() with { Date = "2007-10-05", Episodes = 23 };
    private static MediaIdentityDecision Decision(AniListSource source, int id, MediaIdentityDecisionKind kind = MediaIdentityDecisionKind.Confirmed)
        => new(new("AniList", source.Kind == AniListMediaKind.Anime ? "Anime" : "Manga", id.ToString()), kind, MediaIdentityContext.From(source),
            source.ExpectedFormat ?? "MANGA", null, DateTimeOffset.UtcNow, MediaIdentityDecisionOrigin.User, "合成用户决定");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConfirmationSurvivesAllRemoteCacheDeletionProfileRestartAndOrdinaryMetadataEdits(bool music)
    {
        using var fixture = new Profile(); var source = Source();
        var aniProvider = new AniListFakeProvider(); var musicProvider = new MusicTestProvider();
        var ani = new AniListService(aniProvider, new IsolatedAniListCache(fixture.Store.Configuration()));
        var albums = new MusicBrainzService(musicProvider, new IsolatedMusicBrainzCache(fixture.Store.Configuration()));
        if (music) { await albums.LoadAlbumAsync(MusicTestData.Release, false, default); await albums.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(), default); }
        else { await ani.LoadAsync(2167, source.Kind, false, 1, default); await ani.ConfirmAsync(source, AniListTestData.Details(), default); }
        var userFiles = fixture.UserHashes(); fixture.ClearRemoteCaches(); fixture.Restart();
        if (music)
        {
            var reopened = new MusicBrainzService(musicProvider, new IsolatedMusicBrainzCache(fixture.Store.Configuration()));
            var changed = MusicTestData.Source() with { Title = "修正后的标题", Facts = MusicTestData.Source().Facts with { Artists = ["新增署名"], Labels = ["修正厂牌"] } };
            Assert.Equal(MusicTestData.Release, (await reopened.MatchAsync(changed, false, default)).ConfirmedReleaseId);
            Assert.Equal(0, musicProvider.Searches);
        }
        else
        {
            var reopened = new AniListService(aniProvider, new IsolatedAniListCache(fixture.Store.Configuration()));
            var match = await reopened.MatchAsync(source with { Title = "修正后的标题", OriginalTitle = "原文别名修正", Episodes = 24 }, null, false, default);
            Assert.Equal(2167, match.Binding!.MediaId); Assert.False(match.Binding.NeedsReview); Assert.Equal(0, aniProvider.Searches);
        }
        Assert.Equal(userFiles, fixture.UserHashes()); fixture.AssertLibraryUnchanged();
    }

    [Fact]
    public async Task MoreThan128UserChoicesDoNotConsumeRemoteCacheCapacityAndEvictionCannotRemoveThem()
    {
        using var fixture = new Profile(); var disk = new IsolatedMusicBrainzCache(fixture.Store.Configuration());
        var service = new MusicBrainzService(new MusicTestProvider(), disk);
        for (int i = 0; i < 132; i++)
            await service.ConfirmAsync(MusicTestData.Source() with { BangumiId = (10000 + i).ToString() }, MusicTestData.Album(), default);
        var before = fixture.UserHashes();
        for (int i = 0; i < 135; i++) await service.LoadAlbumAsync(i.ToString("x8") + "-1111-1111-1111-111111111111", false, default);
        Assert.True(Directory.GetFiles(Path.Combine(fixture.Root, IsolatedMusicBrainzCache.DirectoryName)).Length <= 128);
        Assert.Equal(before, fixture.UserHashes()); fixture.ClearRemoteCaches(); fixture.Restart();
        var reopened = new MusicBrainzService(new MusicTestProvider(), new IsolatedMusicBrainzCache(fixture.Store.Configuration()));
        foreach (var i in new[] { 0, 64, 131 })
            Assert.Equal(MusicTestData.Release, (await reopened.MatchAsync(MusicTestData.Source() with { BangumiId = (10000 + i).ToString() }, false, default)).ConfirmedReleaseId);
    }

    [Fact]
    public async Task AFullLegacyBindingCacheBecomesEvictableOnlyAfterIndependentArchiveAndRawRollbackBackup()
    {
        using var fixture = new Profile(); var cache = new IsolatedMusicBrainzCache(fixture.Store.Configuration());
        var sources = Enumerable.Range(0, 128).Select(i => MusicTestData.Source() with { BangumiId = (20000 + i).ToString() }).ToArray();
        foreach (var source in sources) await cache.WriteAsync(new("binding:" + source.Fingerprint, DateTimeOffset.UtcNow,
            Binding: new(source.Fingerprint, MusicTestData.Release)), default);
        var rawDirectory = Path.Combine(fixture.Root, IsolatedMusicBrainzCache.DirectoryName);
        var oldPath = Directory.GetFiles(rawDirectory).First(); var oldBytes = File.ReadAllBytes(oldPath); var oldName = Path.GetFileName(oldPath);
        // This is the first new-version metadata write. The old implementation
        // would reject it because all 128 slots were protected bindings.
        await new MusicBrainzService(new MusicTestProvider(), cache).LoadAlbumAsync(MusicTestData.Release, false, default);
        Assert.Equal(128, Directory.GetFiles(rawDirectory).Length);
        var userDirectory = Path.Combine(fixture.Root, IsolatedMediaIdentityDecisions.DirectoryName);
        var backups = Directory.GetFiles(userDirectory, "legacy-file-*.json"); Assert.Equal(128, backups.Length);
        var matching = Assert.Single(backups, p => Path.GetFileName(p).Contains(Path.GetFileNameWithoutExtension(oldName), StringComparison.Ordinal));
        using (var backup = JsonDocument.Parse(File.ReadAllBytes(matching)))
            Assert.Equal(oldBytes, JsonSerializer.Deserialize<byte[]>(backup.RootElement.GetProperty("Payload").GetBytesFromBase64()));
        fixture.ClearRemoteCaches(); fixture.Restart();
        var reader = new MusicBrainzService(new MusicTestProvider(), new IsolatedMusicBrainzCache(fixture.Store.Configuration()));
        foreach (var source in sources) Assert.Equal(MusicTestData.Release, (await reader.MatchAsync(source, false, default)).ConfirmedReleaseId);
        // A rollback can restore the exact v1 file; the existing v1 reader must
        // accept it without adopting a foreign Profile or rewriting user data.
        Directory.CreateDirectory(rawDirectory); File.WriteAllBytes(Path.Combine(rawDirectory, oldName), oldBytes);
        using var oldEnvelope = JsonDocument.Parse(oldBytes);
        var oldKey = oldEnvelope.RootElement.GetProperty("Key").GetString()!;
        Assert.Equal(MusicTestData.Release, (await new IsolatedMusicBrainzCache(fixture.Store.Configuration()).ReadAsync(oldKey, default))!.Binding!.ReleaseId);
    }

    [Fact]
    public async Task AnUnassignedLegacyFingerprintCannotSilentlyBecomeAnAutomaticReplacementAfterMetadataChanges()
    {
        using var fixture = new Profile(); var original = Source(); var key = "binding:" + original.Fingerprint;
        await new IsolatedAniListCache(fixture.Store.Configuration()).WriteAsync(new(key, DateTimeOffset.UtcNow,
            Binding: new(original.Fingerprint, 4181, original.Kind, "用户核对确认 · 历史选择")), default);
        var changed = original with { Title = "修订名称" };
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        var unassigned = await store.ReadAsync(MediaIdentityContext.From(changed), default);
        Assert.True(unassigned.HasLegacyConflict); Assert.Null(unassigned.Confirmed);
        var service = new AniListService(new AniListFakeProvider(), identityDecisions: store);
        Assert.Null((await service.MatchAsync(changed, null, false, default)).Binding);
        Assert.Contains("历史", (await service.MatchAsync(changed, null, false, default)).Notice);
        // The original fingerprint is still available for exact recovery; the
        // new source can only acquire a mapping through an explicit decision.
        Assert.Equal("4181", (await store.ReadAsync(MediaIdentityContext.From(original), default)).Confirmed!.Target.Id);
        await service.ConfirmAsync(changed, AniListTestData.Details(), default);
        Assert.Equal(2167, (await service.MatchAsync(changed, null, false, default)).Binding!.MediaId);
    }

    [Fact]
    public async Task ResolvingLegacySourceIdsRestoresAutomaticMatchingForGenuinelyNewSources()
    {
        using var fixture = new Profile(); var source = Source(); var key = "binding:" + source.Fingerprint;
        await new IsolatedAniListCache(fixture.Store.Configuration()).WriteAsync(new(key, DateTimeOffset.UtcNow,
            Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认 · 历史选择")), default);
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        var other = source with { BangumiId = "987654" };
        Assert.True((await store.ReadAsync(MediaIdentityContext.From(other), default)).HasLegacyConflict);
        Assert.Equal("2167", (await store.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed!.Target.Id);
        Assert.False((await store.ReadAsync(MediaIdentityContext.From(other), default)).HasLegacyConflict);
        fixture.Restart();
        var service = new AniListService(new CompletenessProvider(), new IsolatedAniListCache(fixture.Store.Configuration()));
        Assert.Equal(2167, (await service.MatchAsync(other, null, false, default)).Binding!.MediaId);
    }

    [Fact]
    public async Task MigrationReceiptFailureCannotRollBackACommittedChoiceAndIsRepairedOnRead()
    {
        using var fixture = new Profile(); var source = Source(); var key = "binding:" + source.Fingerprint;
        await new IsolatedAniListCache(fixture.Store.Configuration()).WriteAsync(new(key, DateTimeOffset.UtcNow,
            Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认 · 历史选择")), default);
        var initialized = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); await initialized.InitializeAsync();
        int beforeReplace = 0;
        var interrupted = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration(), stage =>
        { if (stage == PersistenceStage.BeforeReplace && ++beforeReplace == 2) throw new IOException("synthetic secondary receipt failure"); return Task.CompletedTask; });
        Assert.Equal("2167", (await interrupted.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed!.Target.Id);
        var repaired = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        Assert.Equal("2167", (await repaired.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed!.Target.Id);
        Assert.False((await repaired.ReadAsync(MediaIdentityContext.From(source with { BangumiId = "987655" }), default)).HasLegacyConflict);
    }

    [Fact]
    public async Task ACorruptRemoteCacheCannotInvalidateAnAlreadyPersistedDecisionOrAnotherProvidersDecision()
    {
        using var fixture = new Profile(); var context = MediaIdentityContext.From(Source());
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); await store.ApplyAsync(Decision(Source(), 2167), 0, default);
        var directory = Path.Combine(fixture.Root, IsolatedAniListCache.DirectoryName); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, new string('A', 64) + ".json"), "damaged metadata"); fixture.Restart();
        var reopened = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        Assert.Equal("2167", (await reopened.ReadAsync(context, default)).Confirmed!.Target.Id);
        var music = new MusicBrainzService(new MusicTestProvider(), identityDecisions: reopened);
        await music.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(), default);
        Assert.Equal(MusicTestData.Release, (await music.MatchAsync(MusicTestData.Source(), false, default)).ConfirmedReleaseId);
    }

    [Theory]
    [InlineData(PersistenceStage.DuringTempWrite)] [InlineData(PersistenceStage.BeforeReplace)] [InlineData(PersistenceStage.AfterReplace)]
    public async Task InterruptedLegacyArchivePreservesOldFilesAndCanResumeIdempotently(PersistenceStage stage)
    {
        using var fixture = new Profile(); var source = Source(); var key = "binding:" + source.Fingerprint;
        await new IsolatedAniListCache(fixture.Store.Configuration()).WriteAsync(new(key, DateTimeOffset.UtcNow,
            Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认 · 旧版")), default);
        var original = fixture.RemoteHashes(); bool failOnce = true;
        var first = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration(), current =>
        { if (current == stage && failOnce) { failOnce = false; throw new IOException("synthetic migration interruption"); } return Task.CompletedTask; });
        _ = await Record.ExceptionAsync(() => first.InitializeAsync()); Assert.Equal(original, fixture.RemoteHashes());
        var restored = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); await restored.InitializeAsync();
        var doc = await restored.ReadAsync(MediaIdentityContext.From(source), default); Assert.Equal("2167", doc.Confirmed!.Target.Id);
        var committed = fixture.UserHashes(); await new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()).InitializeAsync();
        Assert.Equal(committed, fixture.UserHashes()); fixture.AssertLibraryUnchanged();
    }

    [Fact]
    public async Task PartialProviderAndCompetingCandidatesCannotOverrideOrCreateUserDecisions()
    {
        using var fixture = new Profile(); var source = Source(); var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        var partial = new AniListService(new PartialProvider(), identityDecisions: store);
        Assert.Null((await partial.MatchAsync(source, null, false, default)).Binding);
        Assert.Equal(0, (await store.ReadAsync(MediaIdentityContext.From(source), default)).Revision);
        var provider = new AniListFakeProvider { Results = [AniListTestData.Media(), AniListTestData.Media() with { Id = 4181 }] };
        var multiple = new AniListService(provider, identityDecisions: store);
        Assert.Null((await multiple.MatchAsync(source, null, false, default)).Binding);
        await multiple.ConfirmAsync(source, AniListTestData.Details(4181), default);
        Assert.Equal(4181, (await partial.MatchAsync(source, null, true, default)).Binding!.MediaId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OldAutomaticProofCannotBypassIncompleteSearchAfterRefreshOrExpiry(bool explicitRefresh)
    {
        var now = DateTimeOffset.UtcNow; var provider = new CompletenessProvider(); var cache = new AniListMemoryCache();
        var service = new AniListService(provider, cache, () => now);
        Assert.Equal(2167, (await service.MatchAsync(Source(), null, false, default)).Binding!.MediaId);
        provider.Complete = false;
        if (!explicitRefresh) now += AniListService.SearchFreshFor + TimeSpan.FromMinutes(1);
        var result = await service.MatchAsync(Source(), null, explicitRefresh, default);
        Assert.Null(result.Binding); Assert.Equal(2, provider.Searches);
        Assert.Null((await cache.IdentityDecisions.ReadAsync(MediaIdentityContext.From(Source()), default)).Confirmed);
    }

    private sealed class CompletenessProvider : IAniListProvider
    {
        public bool Complete = true; public int Searches;
        public Task<AniListMedia[]> SearchAsync(AniListSource source, string? query, CancellationToken cancellation) => Task.FromResult(new[] { AniListTestData.Media() });
        public Task<AniListSearchCandidates> SearchCandidatesAsync(AniListSource source, string? query, CancellationToken cancellation)
        { Searches++; return Task.FromResult(new AniListSearchCandidates([AniListTestData.Media()], Complete)); }
        public Task<AniListMedia> ResolveAsync(AniListReference reference, CancellationToken cancellation) => Task.FromResult(AniListTestData.Media());
        public Task<AniListDetails> FetchAsync(int id, AniListMediaKind kind, int peoplePage, CancellationToken cancellation) => Task.FromResult(AniListTestData.Details());
    }

    [Fact]
    public async Task BookGranularityChangeRetainsTheChoiceButRequiresExplicitReview()
    {
        using var fixture = new Profile(); var source = Source() with { Kind = AniListMediaKind.Manga, Platform = "漫画", Date = null, BookLevel = BookEntityLevel.Series };
        var details = AniListTestData.Details() with { Media = AniListTestData.Media() with { Kind = AniListMediaKind.Manga, Format = "MANGA", Volumes = 2 } };
        var service = new AniListService(new AniListFakeProvider(), new IsolatedAniListCache(fixture.Store.Configuration()));
        await service.ConfirmAsync(source, details, default);
        var changed = source with { BookLevel = BookEntityLevel.Volume };
        var match = await service.MatchAsync(changed, null, false, default);
        Assert.True(match.Binding!.NeedsReview); Assert.Equal(2167, match.Binding.MediaId);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmAsync(changed, details, default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectedTargetSurvivesRestartRefreshMetadataChangesAndExplicitProviderLinks(bool music)
    {
        using var fixture = new Profile();
        if (music)
        {
            var disk = new IsolatedMusicBrainzCache(fixture.Store.Configuration()); var service = new MusicBrainzService(new MusicTestProvider(), disk);
            await service.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(), default);
            await service.RejectAsync(MusicTestData.Source(), MusicTestData.Candidate(), default);
            fixture.ClearRemoteCaches(); fixture.Restart();
            var source = MusicTestData.Source() with { Title = "新标题", Facts = MusicTestData.Source().Facts with { ExternalIds = [new(MusicBrainzEntityKind.Release, MusicTestData.Release)] } };
            var reopened = new MusicBrainzService(new MusicTestProvider(), new IsolatedMusicBrainzCache(fixture.Store.Configuration()));
            var result = await reopened.MatchAsync(source, true, default);
            Assert.Null(result.AutomaticReleaseId); Assert.Null(result.ConfirmedReleaseId); Assert.Contains(MusicTestData.Release, result.RejectedIds);
            await reopened.ConfirmAsync(source, MusicTestData.Album(), default);
            Assert.Equal(MusicTestData.Release, (await reopened.MatchAsync(source, false, default)).ConfirmedReleaseId);
        }
        else
        {
            var source = Source(); var service = new AniListService(new AniListFakeProvider(), new IsolatedAniListCache(fixture.Store.Configuration()));
            await service.ConfirmAsync(source, AniListTestData.Details(), default); await service.RejectAsync(source, AniListTestData.Media(), default);
            fixture.ClearRemoteCaches(); fixture.Restart();
            var reopened = new AniListService(new AniListFakeProvider(), new IsolatedAniListCache(fixture.Store.Configuration()));
            var changed = source with { Title = "新标题", ExternalLinks = ["https://anilist.co/anime/2167"] };
            var result = await reopened.MatchAsync(changed, null, true, default);
            Assert.Null(result.Binding); Assert.Contains(2167, result.RejectedIds);
            await reopened.ConfirmAsync(changed, AniListTestData.Details(), default);
            Assert.Equal(2167, (await reopened.MatchAsync(changed, null, false, default)).Binding!.MediaId);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LateAutomaticResponseCannotOverrideACompletedUserDecision(bool reject)
    {
        using var fixture = new Profile(); var source = Source();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<AniListDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AniListFakeProvider { Fetch = (_, _, _) => { entered.SetResult(); return response.Task; } };
        var service = new AniListService(provider, new IsolatedAniListCache(fixture.Store.Configuration()));
        var automatic = service.MatchAsync(source, null, false, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reject) await service.RejectAsync(source, AniListTestData.Media(), default);
        else await service.ConfirmAsync(source, AniListTestData.Details(4181), default);
        var committed = fixture.UserHashes(); response.SetResult(AniListTestData.Details()); var result = await automatic;
        if (reject) { Assert.Null(result.Binding); Assert.Contains(2167, result.RejectedIds); }
        else Assert.Equal(4181, result.Binding!.MediaId);
        Assert.Equal(committed, fixture.UserHashes());
    }

    [Fact]
    public async Task IndependentStoreInstancesUseRevisionChecksInsteadOfOverwritingAnotherManualDecision()
    {
        using var fixture = new Profile(); var context = MediaIdentityContext.From(Source());
        var a = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); var b = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        Assert.Equal(0, (await a.ReadAsync(context, default)).Revision);
        var results = await Task.WhenAll(new[] { (a, 2167), (b, 4181) }.Select(async x => await Record.ExceptionAsync(() => x.Item1.ApplyAsync(Decision(Source(), x.Item2), 0, default))));
        Assert.Single(results, e => e is null); Assert.Single(results, e => e is IOException);
        Assert.Equal(1, (await a.ReadAsync(context, default)).Revision);
    }

    [Theory]
    [InlineData(PersistenceStage.BeforeTempWrite, false)]
    [InlineData(PersistenceStage.DuringTempWrite, false)]
    [InlineData(PersistenceStage.AfterTempWrite, false)]
    [InlineData(PersistenceStage.BeforeReplace, false)]
    [InlineData(PersistenceStage.AfterReplace, true)]
    public async Task InterruptedDecisionWriteRestartsWithExactlyTheCommittedVersion(PersistenceStage fault, bool committed)
    {
        using var fixture = new Profile(); bool enabled = false;
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration(), stage =>
        { if (enabled && stage == fault) throw new IOException("synthetic persistence interruption"); return Task.CompletedTask; });
        var context = MediaIdentityContext.From(Source()); await store.ReadAsync(context, default);
        await store.ApplyAsync(Decision(Source(), 2167), 0, default); enabled = true;
        var error = await Record.ExceptionAsync(() => store.ApplyAsync(Decision(Source(), 4181), 1, default));
        if (committed) Assert.Null(error); else Assert.IsType<IOException>(error);
        fixture.Restart();
        var value = await new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()).ReadAsync(context, default);
        Assert.Equal(committed ? "4181" : "2167", value.Confirmed!.Target.Id); Assert.Equal(committed ? 2 : 1, value.Revision);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, IsolatedMediaIdentityDecisions.DirectoryName), "*.tmp")); fixture.AssertLibraryUnchanged();
    }

    [Fact]
    public async Task LegacyBindingsAreArchivedWithoutChangingOldFilesAndRecoverAfterCacheDeletion()
    {
        using var fixture = new Profile(); var source = Source(); var music = MusicTestData.Source();
        var aniCache = new IsolatedAniListCache(fixture.Store.Configuration()); var musicCache = new IsolatedMusicBrainzCache(fixture.Store.Configuration());
        var key = "binding:" + source.Fingerprint; var musicKey = "binding:" + music.Fingerprint;
        await aniCache.WriteAsync(new(key, DateTimeOffset.UtcNow, Binding: new(source.Fingerprint, 2167, source.Kind, "用户核对确认 · 旧版")), default);
        await musicCache.WriteAsync(new(musicKey, DateTimeOffset.UtcNow, Binding: new(music.Fingerprint, MusicTestData.Release)), default);
        var original = fixture.RemoteHashes(); var decisions = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); await decisions.InitializeAsync();
        Assert.Equal(original, fixture.RemoteHashes()); // A previous binary can still read its untouched v1 files.
        Assert.NotNull(await aniCache.ReadAsync(key, default)); Assert.NotNull(await musicCache.ReadAsync(musicKey, default));
        fixture.ClearRemoteCaches(); fixture.Restart();
        var reopened = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        var animation = await reopened.ReadAsync(MediaIdentityContext.From(source), default);
        var album = await reopened.ReadAsync(MediaIdentityContext.From(music), default);
        Assert.Equal("2167", animation.Confirmed!.Target.Id); Assert.Equal(MediaIdentityDecisionOrigin.LegacyConfirmation, animation.Confirmed.Origin);
        Assert.Equal(MusicTestData.Release, album.Confirmed!.Target.Id);
        await reopened.ApplyAsync(Decision(source, 2167, MediaIdentityDecisionKind.Rejected), animation.Revision, default);
        Assert.Null((await new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()).ReadAsync(MediaIdentityContext.From(source), default)).Confirmed);
    }

    [Fact]
    public async Task LegacyAutomaticEvidenceIsNotMigratedIntoAnIrrevocableUserChoice()
    {
        using var fixture = new Profile(); var source = Source(); var key = "binding:" + source.Fingerprint;
        await new IsolatedAniListCache(fixture.Store.Configuration()).WriteAsync(new(key, DateTimeOffset.UtcNow,
            Binding: new(source.Fingerprint, 2167, source.Kind, "自动关联 · 旧策略") { AutomaticPolicyVersion = 1 }), default);
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); await store.InitializeAsync(); fixture.ClearRemoteCaches();
        Assert.Null((await store.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed);
        var provider = new PartialProvider(); var service = new AniListService(provider, identityDecisions: store);
        Assert.Null((await service.MatchAsync(source, null, false, default)).Binding);
    }

    [Theory]
    [InlineData("foreign")] [InlineData("future")] [InlineData("hash")] [InlineData("duplicate")]
    public async Task DamagedOrForeignUserDataIsNeverTreatedAsEmptyAndOtherSourcesRemainUsable(string fault)
    {
        using var fixture = new Profile(); var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); var context = MediaIdentityContext.From(Source());
        await store.ApplyAsync(Decision(Source(), 2167), 0, default);
        var path = Path.Combine(fixture.Root, IsolatedMediaIdentityDecisions.DirectoryName, context.Anchor.Key + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (fault == "foreign") json["ProfileId"] = Guid.NewGuid(); else if (fault == "future") json["Version"] = 99; else if (fault == "hash") json["Hash"] = "bad";
        var changed = json.ToJsonString(); if (fault == "duplicate") changed = changed.Replace("\"Version\":1", "\"Version\":1,\"Version\":1");
        File.WriteAllText(path, changed); var damaged = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(context, default));
        await Assert.ThrowsAsync<IOException>(() => store.ApplyAsync(Decision(Source(), 4181), 1, default));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        var other = Source() with { BangumiId = "876" }; await store.ApplyAsync(Decision(other, 4181), 0, default);
        Assert.Equal("4181", (await store.ReadAsync(MediaIdentityContext.From(other), default)).Confirmed!.Target.Id);
    }

    [Fact]
    public async Task TypesSeasonsBookLevelsAndExplicitConflictsCannotReuseOrEraseAUserMapping()
    {
        using var fixture = new Profile(); var source = Source(); var provider = new AniListFakeProvider();
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); var service = new AniListService(provider, identityDecisions: store);
        await service.ConfirmAsync(source, AniListTestData.Details(), default);
        Assert.Null((await service.MatchAsync(source with { BangumiId = "876", Date = "2008-10-03" }, null, false, default)).Binding);
        var wrongKind = source with { Kind = AniListMediaKind.Manga, Platform = "漫画", BookLevel = BookEntityLevel.Volume };
        Assert.Null((await store.ReadAsync(MediaIdentityContext.From(wrongKind), default)).Confirmed);
        var conflicting = source with { ExternalLinks = ["https://anilist.co/anime/4181"] };
        using var vm = new AniListDetailsViewModel(new FixedBridge(conflicting), service); await vm.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null));
        Assert.Equal(AniListDetailState.Preview, vm.State); Assert.False(vm.HasTrustedBridge); Assert.False(vm.CanConfirm);
        Assert.Equal("2167", (await store.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed!.Target.Id);
    }

    [Fact]
    public async Task TargetDeletionOrFormatChangeDoesNotSearchForAReplacementAndLeavesTheDecisionIntact()
    {
        using var fixture = new Profile(); var source = Source(); var provider = new AniListFakeProvider();
        var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration()); var service = new AniListService(provider, identityDecisions: store);
        await service.ConfirmAsync(source, AniListTestData.Details(), default); var before = fixture.UserHashes();
        provider.Fetch = (_, _, _) => throw new AniListException(AniListFailure.Unavailable, "removed");
        using (var vm = new AniListDetailsViewModel(new FixedBridge(source), service))
        { await vm.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null)); Assert.Equal(AniListDetailState.Failed, vm.State); Assert.False(vm.HasTrustedBridge); }
        provider.Fetch = (_, _, _) => Task.FromResult(AniListTestData.Details() with { Media = AniListTestData.Media() with { Format = "OVA" } });
        using (var vm = new AniListDetailsViewModel(new FixedBridge(source), new AniListService(provider, identityDecisions: store)))
        { await vm.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null)); Assert.Equal(AniListDetailState.Preview, vm.State); Assert.False(vm.HasTrustedBridge); }
        Assert.Equal(0, provider.Searches); Assert.Equal(before, fixture.UserHashes());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ADeletedRemoteTargetCanBeRevokedLocallyWithoutAnyProviderRequest(bool music)
    {
        using var fixture = new Profile(); var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        if (music)
        {
            var provider = new MusicTestProvider(); var reader = new MusicBrainzService(provider, identityDecisions: store);
            await reader.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(), default);
            provider.Load = (_, _) => throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "deleted");
            await reader.RevokeAsync(MusicTestData.Source(), MusicTestData.Release, default);
            Assert.Equal(0, provider.Albums); Assert.Equal(0, provider.Searches);
            var result = await reader.MatchAsync(MusicTestData.Source(), false, default);
            Assert.Null(result.ConfirmedReleaseId); Assert.Null(result.AutomaticReleaseId); Assert.Contains(MusicTestData.Release, result.RejectedIds);
        }
        else
        {
            var provider = new AniListFakeProvider(); var reader = new AniListService(provider, identityDecisions: store);
            await reader.ConfirmAsync(Source(), AniListTestData.Details(), default);
            provider.Fetch = (_, _, _) => throw new AniListException(AniListFailure.Unavailable, "deleted");
            using var model = new AniListDetailsViewModel(new FixedBridge(Source()), reader);
            await model.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null));
            Assert.Equal(AniListDetailState.Failed, model.State); Assert.True(model.CanRevoke); await model.RevokeAsync();
            Assert.False(model.CanRevoke); Assert.False(model.HasTrustedBridge); Assert.Equal(0, provider.Searches);
            Assert.True((await store.ReadAsync(MediaIdentityContext.From(Source()), default)).Rejects(new("AniList", "Anime", "2167")));
        }
        fixture.Restart(); fixture.AssertLibraryUnchanged();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevokingAStaleSelectionCannotRevokeANewerManualChoice(bool music)
    {
        using var fixture = new Profile(); var store = new IsolatedMediaIdentityDecisions(fixture.Store.Configuration());
        if (music)
        {
            var service = new MusicBrainzService(new MusicTestProvider(), identityDecisions: store);
            await service.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(), default);
            await service.ConfirmAsync(MusicTestData.Source(), MusicTestData.Album(MusicTestData.Other), default);
            await Assert.ThrowsAsync<IOException>(() => service.RevokeAsync(MusicTestData.Source(), MusicTestData.Release, default));
            Assert.Equal(MusicTestData.Other, (await service.MatchAsync(MusicTestData.Source(), false, default)).ConfirmedReleaseId);
        }
        else
        {
            var service = new AniListService(new AniListFakeProvider(), identityDecisions: store);
            await service.ConfirmAsync(Source(), AniListTestData.Details(), default); await service.ConfirmAsync(Source(), AniListTestData.Details(4181), default);
            await Assert.ThrowsAsync<IOException>(() => service.RevokeAsync(Source(), 2167, default));
            Assert.Equal(4181, (await service.MatchAsync(Source(), null, false, default)).Binding!.MediaId);
        }
    }

    [Fact]
    public async Task RefreshOfAConfirmedTargetRechecksItsStoredFormatEvenWhenSourceFormatWasUnknown()
    {
        using var fixture = new Profile(); var source = Source() with { Platform = null };
        var provider = new AniListFakeProvider(); var service = new AniListService(provider, new IsolatedAniListCache(fixture.Store.Configuration()));
        await service.ConfirmAsync(source, AniListTestData.Details(), default);
        using var vm = new AniListDetailsViewModel(new FixedBridge(source), service);
        await vm.OpenAsync(new("51", BangumiRelationKind.Anime, "CLANNAD", "CLANNAD", "动画", null)); Assert.True(vm.HasTrustedBridge);
        provider.Fetch = (_, _, _) => Task.FromResult(AniListTestData.Details() with { Media = AniListTestData.Media() with { Format = "OVA" } });
        await vm.RefreshAsync(); Assert.False(vm.HasTrustedBridge); Assert.Equal(AniListDetailState.Preview, vm.State);
        Assert.NotNull((await ((IMediaIdentityDecisionOwner)new IsolatedAniListCache(fixture.Store.Configuration())).IdentityDecisions.ReadAsync(MediaIdentityContext.From(source), default)).Confirmed);
    }

    [Fact]
    public async Task BookChoicesStayDiscoverableAfterCacheDeletionTitleAndLevelChangesAndCanBeRevoked()
    {
        using var fixture = new Profile(); var store = new IsolatedBookBibliographyStore(fixture.Store.Configuration());
        var source = new BookSource(new("Bangumi", "48", BookEntityLevel.Publication), "Clannad", "Clannad", "小说", ["Key"], ["9784840232500"], null);
        var service = new BookBibliographyService(new Books(), store, store); await service.ConfirmAsync(source, BookBibliographyServiceTests.Publication(), default);
        fixture.ClearRemoteCaches(); fixture.Restart(); store = new(fixture.Store.Configuration()); service = new(new Books(), store, store);
        var changed = source with { Title = "修订标题", Identity = source.Identity with { Level = BookEntityLevel.Volume } };
        var retained = Assert.Single(await service.BindingsAsync(changed, default)); Assert.True(retained.NeedsReview); Assert.Equal(source.Identity, retained.SourceIdentity);
        Assert.Equal(BookBibliographyServiceTests.Publication().Id, retained.PublicationId);
        await service.RemoveAsync(changed, retained.PublicationId, default); Assert.Empty(await service.BindingsAsync(source, default));
    }

    private sealed class PartialProvider : IAniListProvider
    {
        public Task<AniListMedia[]> SearchAsync(AniListSource source, string? query, CancellationToken cancellation) => Task.FromResult(new[] { AniListTestData.Media() });
        public Task<AniListMedia> ResolveAsync(AniListReference reference, CancellationToken cancellation) => throw new InvalidOperationException();
        public Task<AniListDetails> FetchAsync(int id, AniListMediaKind kind, int peoplePage, CancellationToken cancellation) => throw new InvalidOperationException("Incomplete search must not become automatic");
    }
    private sealed class FixedBridge(AniListSource source) : IBangumiSubjectDetailsReader
    {
        public Task<BangumiSubjectDetailsResult> LoadAsync(string id, BangumiRelationKind kind, bool refresh, CancellationToken cancellation) => Task.FromResult(
            new BangumiSubjectDetailsResult(new(source.BangumiId, kind, source.Title, source.OriginalTitle, null, "合成简介", source.Date, source.Platform,
                source.Episodes, null, null, DateTimeOffset.UtcNow) { ExternalLinks = source.ExternalLinks }, false, false, null));
    }
    private sealed class Books : IBookBibliographyProvider
    {
        public Task<BookPublication> FetchAsync(string id, CancellationToken cancellation) => Task.FromResult(BookBibliographyServiceTests.Publication(id));
        public Task<BookSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellation) => Task.FromResult(new BookSearchPage([], 0, null, DateTimeOffset.UtcNow));
    }
    private sealed class Profile : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "the-order-identity-test-" + Guid.NewGuid().ToString("N"));
        public NativeProfileStore Store { get; private set; }
        private readonly byte[] library;
        public Profile() { Store = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec()); library = File.ReadAllBytes(Path.Combine(Root, "library.json")); }
        public void Restart() { Store.Dispose(); Store = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec()); }
        public void AssertLibraryUnchanged() => Assert.Equal(library, File.ReadAllBytes(Path.Combine(Root, "library.json")));
        public string[] UserHashes() => Hashes(IsolatedMediaIdentityDecisions.DirectoryName);
        public string[] RemoteHashes() => Hashes(IsolatedAniListCache.DirectoryName).Concat(Hashes(IsolatedMusicBrainzCache.DirectoryName)).ToArray();
        private string[] Hashes(string directory) => Directory.Exists(Path.Combine(Root, directory)) ? Directory.GetFiles(Path.Combine(Root, directory)).Order().Select(p => Path.GetFileName(p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray() : [];
        public void ClearRemoteCaches()
        {
            foreach (var directory in new[] { IsolatedAniListCache.DirectoryName, IsolatedMusicBrainzCache.DirectoryName, IsolatedBookBibliographyStore.CacheDirectoryName })
            {
                var path = Path.GetFullPath(Path.Combine(Root, directory));
                if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }
        public void Dispose()
        {
            Store.Dispose(); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Root).StartsWith("the-order-identity-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }
}
