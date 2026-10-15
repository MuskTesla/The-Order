using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class ContentLanguageTests
{
    private static readonly ContentIdentity Id = new("Provider", "Work", "42");
    private static ContentText Text(string text, string language, bool original = false, ContentTextOrigin origin = ContentTextOrigin.ProviderTranslation)
        => new(Id, "synopsis", text, language, origin, original);
    internal static AniListMedia Media(AniListMediaKind kind = AniListMediaKind.Anime, string format = "TV") =>
        new(2167, null, kind, "English Title", "原作の題名", "English Title", format, "FINISHED", null, "2007-10-05", null, 23, null, null, ["別名 ☆", "中文别名"])
        { RomajiTitle = "Gensaku no Daimei" };
    internal static BangumiSubjectDetails Bridge(BangumiRelationKind kind = BangumiRelationKind.Anime) =>
        new("51", kind, "中文标题", "原作の題名", null, "数据库提供的中文简介。", "2007-10-05", "TV", 23, null, null, DateTimeOffset.UtcNow) { ChineseTitle = "中文标题" };
    internal static AniListDetails Details(AniListMedia? media = null) =>
        new(media ?? Media(), "The original English description.", [], [], [], [], [], [], 1, false, false, DateTimeOffset.UtcNow);
    [Theory][InlineData("zh_CN", "zh-Hans")][InlineData("zh_TW", "zh-Hant")][InlineData("jpn", "ja")][InlineData("eng", "en")][InlineData("", "und")]
    [InlineData("zh-Hant-HK", "zh-Hant")][InlineData("zh-MO-u-ca-chinese", "zh-Hant")][InlineData("en-AU", "en")][InlineData("chi", "zh")]
    public void KnownLanguageCodesNormalizeWithoutRewritingText(string input, string expected) => Assert.Equal(expected, ContentLanguage.Normalize(input));
    [Theory][InlineData("zh-Hans", "中文")][InlineData("en", "English")][InlineData("ja", "日本語")][InlineData("original", "日本語")]
    public void PreferenceAndOriginalAreIndependentFromInterface(string preferred, string expected)
    {
        using var ui = UiText.ForLanguage("zh-Hant"); using var content = ContentLanguage.ForPreference(new(preferred));
        var values = new[] { Text("日本語", "ja", true), Text("English", "en"), Text("中文", "zh-Hans") };
        Assert.Equal(expected, ContentLanguage.Select(values).Text); Assert.Equal("zh-Hant", UiText.Language);
        Assert.Equal("日本語", ContentLanguage.Select(values).OriginalText);
    }
    [Fact] public void ConfiguredFallbackPrecedesOriginalAndChineseScriptsRemainDistinct()
    {
        var rows = new[] { Text("原作", "ja", true), Text("English", "en") };
        Assert.Equal("English", ContentLanguage.Select(rows, new("zh-Hans", "en")).Text);
        Assert.Equal("原作", ContentLanguage.Select(rows, new("zh-Hans", "original")).Text);
        var traditional = Text("繁體資料", "zh-Hant");
        Assert.Equal("繁體資料", ContentLanguage.Select(rows.Append(traditional), new("zh-Hans", "en")).Text);
        Assert.Equal("zh-Hant", ContentLanguage.Select([traditional], new()).Selected!.Language);
    }
    [Fact] public void MachineTextNeverSilentlyReplacesHumanFallback()
    {
        var selected = ContentLanguage.Select([Text("原文", "ja", true), Text("机器", "zh-Hans", origin: ContentTextOrigin.MachineTranslation)], new());
        Assert.Equal("原文", selected.Text); Assert.Equal(2, selected.Variants.Length); Assert.False(selected.Selected!.IsMachine);
        Assert.Null(ContentLanguage.Select([Text("机器", "zh-Hans", origin: ContentTextOrigin.MachineTranslation)]).Selected);
    }
    [Fact] public void HumanTranslationKeepsItsTypedSourceAndDoesNotOverwriteOriginal()
    {
        var raw = Text("Original", "en", true); var manual = Text("人工译文", "zh-Hans", origin: ContentTextOrigin.HumanTranslation);
        var result = ContentLanguage.Select([raw, manual], new());
        Assert.Same(manual, result.Selected); Assert.Same(raw, result.Original); Assert.Equal(Id, result.Selected!.Identity);
        Assert.Equal("Original", raw.Text); Assert.Contains("人工翻译", result.Caption);
    }
    [Fact] public void BlankAndMalformedFieldsAreSkippedButUnicodeRemainsByteEquivalent()
    {
        const string text = "素晴らしき日々 ～不連続存在～ / 𠮷・√・∞・é\u0301\r\n\"物語\"";
        var original = Text(text, "ja", true); var before = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = ContentLanguage.Select([Text(" ", "zh-Hans"), Text("x\0y", "zh-Hans"), original], new());
        Assert.Equal(text, result.Text); Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(original));
        Assert.Null(ContentLanguage.Select([]).Selected);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void AnimeAndBooksOnlyFuseAnExplicitlyTrustedBridge(bool book)
    {
        var media = Media(book ? AniListMediaKind.Manga : AniListMediaKind.Anime, book ? "NOVEL" : "TV");
        var source = Bridge(book ? BangumiRelationKind.Book : BangumiRelationKind.Anime);
        var raw = JsonSerializer.Serialize(media); var fingerprint = AniListSource.From(source).Fingerprint;
        using var preference = ContentLanguage.ForPreference(new());
        var trusted = ContentLanguage.AniListTitle(media, source, true);
        Assert.Equal("中文标题", trusted.Text); Assert.Equal("Bangumi", trusted.Selected!.Identity.Provider);
        Assert.Equal("51", trusted.Selected.Identity.Id); Assert.Equal("原作の題名", trusted.OriginalText);
        Assert.DoesNotContain(ContentLanguage.AniListTitle(media, source, false).Variants, x => x.Identity.Provider == "Bangumi");
        Assert.Equal(raw, JsonSerializer.Serialize(media)); Assert.Equal(fingerprint, AniListSource.From(source).Fingerprint);
    }
    [Fact] public void CrossMediaBridgeCannotSupplyAnAnimeTitle()
    {
        var selected = ContentLanguage.AniListTitle(Media(), Bridge(BangumiRelationKind.Book), true);
        Assert.DoesNotContain(selected.Variants, x => x.Identity.Provider == "Bangumi");
    }
    [Fact] public void UnknownSynopsisLanguageRemainsUnknownDespiteProviderHint()
    {
        using var preference = ContentLanguage.ForPreference(new());
        var result = ContentLanguage.Synopsis(Details(), Bridge(), true);
        Assert.Equal("数据库提供的中文简介。", result.Text); Assert.Equal("und", result.Selected!.Language);
        Assert.Contains("未标注", result.Caption); Assert.Equal("The original English description.", result.OriginalText);
        Assert.Equal("The original English description.", ContentLanguage.Synopsis(Details(), Bridge(), false).Text);
    }
    [Fact] public void JapaneseBridgeSummaryDoesNotMasqueradeAsChinese()
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans", "en"));
        var bridge = Bridge() with { Summary = "そして、物語が始まる。" };
        var result = ContentLanguage.Synopsis(Details(), bridge, true);
        Assert.Equal("The original English description.", result.Text); Assert.Equal("und", result.Selected!.Language);
    }
    [Theory][InlineData("ja", "原作の題名")][InlineData("en", "English Title")][InlineData("zh-Hans", "原作の題名")]
    public void MissingChineseTitleNaturallyFallsBackToPublishedOriginal(string preferred, string expected)
    { using var scope = ContentLanguage.ForPreference(new(preferred)); Assert.Equal(expected, ContentLanguage.AniListTitle(Media()).Text); }
    [Fact] public void PersonNamesUsePublishedFormsAndRetainNativeSpelling()
    {
        var p = new AniListPerson(100, "Harumi Sakurai", "櫻井浩美", null);
        using (ContentLanguage.ForPreference(new("zh-Hans"))) Assert.Equal("櫻井浩美", ContentLanguage.Person(p).Text);
        using (ContentLanguage.ForPreference(new("en"))) Assert.Equal("Harumi Sakurai", ContentLanguage.Person(p).Text);
        Assert.Equal("櫻井浩美", p.NativeName); Assert.Equal("100", ContentLanguage.Person(p, "Character").Original!.Identity.Id);
        Assert.Equal("Character", ContentLanguage.Person(p, "Character").Original!.Identity.Resource);
    }
    [Fact] public void MusicPreviewCannotBorrowChineseAlbumTitleOrAlterReleaseIdentity()
    {
        var release = new MusicBrainzReleaseCandidate("5ae1dd91-d42c-425b-861b-63a6fe601226", null, "Original Album", "麻枝准", "2004-08-13", "JP", "Official", null, [], 3, 56);
        var album = new MusicBrainzAlbum(release, null, "Album", [], [], [], null, DateTimeOffset.UtcNow);
        var source = Bridge(BangumiRelationKind.Music);
        var raw = JsonSerializer.Serialize(album); using var preference = ContentLanguage.ForPreference(new());
        Assert.Equal("中文标题", ContentLanguage.MusicTitle(album, source, true).Text);
        Assert.Equal("Original Album", ContentLanguage.MusicTitle(album, source, false).Text);
        Assert.Equal(raw, JsonSerializer.Serialize(album));
        var track = ContentLanguage.Raw(new("MusicBrainz", "ReleaseTrack", release.Id + "/1/1"), "title", "汐 ～渚～");
        Assert.Equal("汐 ～渚～", track.Text);
    }
    [Fact] public void BibliographicAtomsAndPublicationIdentityAreNeverTranslated()
    {
        var p = new BookPublication("034881042", "魔法使いの夜 上", "魔法使いの夜", "上", ["9784065443958"], [], ["奈須きのこ 著"], ["講談社"], [], "2026.07", "初版", ["jpn"], false, DateTimeOffset.UtcNow);
        var raw = JsonSerializer.Serialize(p); using var preference = ContentLanguage.ForPreference(new("en"));
        var selected = ContentLanguage.PublicationTitle(p); Assert.Equal(p.Title, selected.Text);
        Assert.Equal("und", selected.Selected!.Language); Assert.Equal("ja", selected.Selected.LanguageHint); Assert.Equal("034881042", selected.Selected.Identity.Id);
        Assert.Equal(raw, JsonSerializer.Serialize(p)); Assert.Equal("9784065443958", Assert.Single(p.Isbns));
    }
    [Fact] public void NewlyRetainedProviderTitlesDoNotReplaceCanonicalMatchingFields()
    {
        using var row = JsonDocument.Parse("""{"id":2167,"type":"ANIME","title":{"english":"CLANNAD","romaji":"Kuranado","native":"クラナド"},"format":"TV","status":"FINISHED","synonyms":["克兰纳德"]}""");
        var m = AniListProvider.MapMedia(row.RootElement); Assert.Equal("CLANNAD", m.Title); Assert.Equal("Kuranado", m.RomajiTitle);
        Assert.Equal("クラナド", m.NativeTitle); Assert.Contains("克兰纳德", m.Synonyms);
        var legacy = m with { RomajiTitle = null }; var json = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("RomajiTitle", json); Assert.True(AniListValidation.Media(JsonSerializer.Deserialize<AniListMedia>(json)));
    }
    [Fact] public async Task ConcurrentAsyncScopesDoNotLeakLanguageBetweenRequests()
    {
        async Task<string> Pick(string language) { using var scope = ContentLanguage.ForPreference(new(language)); await Task.Yield(); return ContentLanguage.AniListTitle(Media()).Text; }
        var values = await Task.WhenAll(Pick("en"), Pick("ja"), Pick("original"));
        Assert.Equal(["English Title", "原作の題名", "原作の題名"], values);
    }
    [Fact] public void OriginalSynopsisUsesDeclaredSourceLanguageRatherThanPreviouslyChosenTranslation()
    {
        var selection = ContentLanguage.Catalog(Id, "synopsis", [new("zh-Hans", "中文简介"), new("ja", "原文の紹介")], "中文简介", "ja");
        Assert.Equal("原文の紹介", selection.OriginalText);
        Assert.Equal(ContentTextOrigin.ProviderOriginal, selection.Original!.Origin);
        using var original = ContentLanguage.ForPreference(new("original"));
        Assert.Equal("原文の紹介", ContentLanguage.Catalog(Id, "synopsis", [new("zh-Hans", "中文简介"), new("ja", "原文の紹介")], "中文简介", "ja").Text);
    }
    [Fact] public void WrongMediaSummaryCannotBeAddedEvenWithTrustedFlag()
    { Assert.DoesNotContain(ContentLanguage.Synopsis(Details(), Bridge(BangumiRelationKind.Music), true).Variants, x => x.Identity.Provider == "Bangumi"); }
    [Theory][InlineData("title")][InlineData("name")][InlineData("ISBN")][InlineData("date")][InlineData("catalogNumber")]
    public void OptionalTranslationContractDoesNotAcceptNamesOrBibliographicAtoms(string field)
    { Assert.False(new LocalTranslationRequest(Id, field, "Some prose", "en", "zh-Hans").IsEligible); }
    [Fact] public void TranslationContractOnlyAdmitsBoundedSynopsisAndExplicitTarget()
    {
        Assert.True(new LocalTranslationRequest(Id, "synopsis", "Some prose", "en", "zh-Hans").IsEligible);
        Assert.False(new LocalTranslationRequest(Id, "synopsis", new string('x', 65537), "en", "zh-Hans").IsEligible);
        Assert.False(new LocalTranslationRequest(Id, "synopsis", "Some prose", "en", "original").IsEligible);
    }
    private static WorkMetadata Game(string intros)
    {
        using var row = JsonDocument.Parse("""{"object":"work","id":"7001","display_name":"原文の名前","olang":"ja","localized":{"zh-Hans":{"value":"中文标题"},"en":{"value":"English Name"}},"titles":[{"title_kind":"alias","lang":"ja","title":"別名 ～☆～"}],"intros": """ + intros + "}");
        return new RemoteWorkDto(row.RootElement).Map(true, DateTimeOffset.UnixEpoch);
    }
    [Fact] public void GalgameLanguageProjectionPreservesCanonicalNamesAliasesAndAllCachedSourceVariants()
    {
        var work = Game("""[{"lang":"ja","value":"日本語の原文","source":"getchu"},{"lang":"en","value":"English synopsis","source":"curated"},{"lang":"zh-Hans","value":"中文简介","source":"bangumi","is_machine":false}]""");
        var raw = JsonSerializer.SerializeToUtf8Bytes(CachedCatalogWork.From(work));
        using (ContentLanguage.ForPreference(new("zh-Hans"))) { Assert.Equal("中文标题", CatalogLanguage.Title(work)); Assert.Equal("中文简介", CatalogLanguage.Project(work).Description); }
        using (ContentLanguage.ForPreference(new("original"))) { Assert.Equal("原文の名前", CatalogLanguage.Title(work)); Assert.Equal("日本語の原文", CatalogLanguage.Project(work).Description); }
        using (ContentLanguage.ForPreference(new("en"))) { Assert.Equal("English Name", CatalogLanguage.Title(work)); Assert.Equal("English synopsis", CatalogLanguage.Project(work).Description); }
        Assert.Equal(raw, JsonSerializer.SerializeToUtf8Bytes(CachedCatalogWork.From(work))); Assert.Contains("別名 ～☆～", work.Aliases);
        var cached = JsonSerializer.Deserialize<CachedCatalogWork>(raw)!.Work;
        using (ContentLanguage.ForPreference(new("zh-Hans"))) Assert.Equal("中文简介", CatalogLanguage.Project(cached).Description);
    }
    [Fact] public void PreviouslySelectedMachineDescriptionCannotSneakBackAsUnlabelledOriginal()
    {
        var work = Game("""[{"lang":"zh-Hans","value":"机器简介","source":"bangumi","is_machine":true}]""");
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        Assert.Empty(CatalogLanguage.Project(work).Description); var selection = ContentLanguage.CatalogSynopsis(work);
        Assert.Null(selection.Selected); Assert.True(Assert.Single(selection.Variants).IsMachine);
    }
    [Fact] public void AggregatedFieldAttributionAndUntaggedAliasesAreNotLost()
    {
        var selection = ContentLanguage.Catalog(Id, "synopsis", [new("ja", "原文", "getchu"), new("zh-Hans", "中文", "bangumi")], "原文", "ja");
        Assert.Equal("bangumi", selection.Selected!.Attribution); Assert.Equal("getchu", selection.Original!.Attribution);
        Assert.Contains("bangumi", selection.Caption);
        var aliases = ContentLanguage.Aliases(Id, ["別名", "中文别名", "𠮷～☆"]);
        Assert.Equal(3, aliases.Variants.Length); Assert.All(aliases.Variants, x => Assert.Equal("und", x.Language));
    }
    [Fact] public void OriginalProjectionKeepsTheSelectedIntroductionAttribution()
    {
        var work = Game("""[{"lang":"en","value":"English synopsis","source":"curated"},{"lang":"ja","value":"日本語の原文","source":"getchu"}]""");
        using var preference = ContentLanguage.ForPreference(new("original"));
        var projected = CatalogLanguage.Project(work);
        Assert.Equal("日本語の原文", projected.Description);
        Assert.Equal("getchu", projected.Presentation!.IntroSource);
    }
    [Fact] public void ReleaseTextLanguageIsContextNotADeclaredLanguageForEveryTitle()
    {
        var r = new MusicBrainzReleaseCandidate("5ae1dd91-d42c-425b-861b-63a6fe601226", null, "CLANNAD Original SoundTrack", "Key", null, null, "Official", null, [], 3, 56) { TextLanguage = "jpn" };
        var a = new MusicBrainzAlbum(r, null, null, [], [], [], null, DateTimeOffset.UtcNow);
        var selected = ContentLanguage.MusicTitle(a, null, false).Selected!;
        Assert.Equal("und", selected.Language); Assert.Equal("ja", selected.LanguageHint); Assert.Equal(r.Title, selected.Text);
    }
}

[Collection("Native profile resource")]
public sealed class ContentLanguagePreferenceTests
{
    [Theory][InlineData("zh-Hans", "original")][InlineData("en", "ja")][InlineData("original", "zh-Hant")]
    public async Task IndependentCompanionSurvivesRestartAndPreservesExistingFiles(string primary, string fallback)
    {
        using var f = new ProfileFixture(); string[] paths = ["library.json", "native-ui.json", NativeProfileStore.ManifestName]; byte[][] original;
        using (var p = f.Open())
        {
            await using var ui = new LanguagePreferencesStore(p.Configuration()); await ui.SaveAsync(new("en"));
            paths = paths.Append(LanguagePreferencesStore.FileName).ToArray(); original = paths.Select(x => File.ReadAllBytes(Path.Combine(f.Root, x))).ToArray();
            await using var content = new ContentLanguagePreferencesStore(p.Configuration()); await content.SaveAsync(new(primary, fallback));
        }
        using var reopened = f.Open(); await using var store = new ContentLanguagePreferencesStore(reopened.Configuration());
        Assert.Equal(new(primary, fallback), (await store.LoadAsync()).Preference);
        for (var i = 0; i < paths.Length; i++) Assert.Equal(original[i], File.ReadAllBytes(Path.Combine(f.Root, paths[i])));
    }
    [Theory][InlineData(PersistenceStage.DuringTempWrite)][InlineData(PersistenceStage.BeforeReplace)]
    public async Task InterruptedWriteRetainsPreviousPreference(PersistenceStage stage)
    {
        using var f = new ProfileFixture(); using var p = f.Open(); await using var good = new ContentLanguagePreferencesStore(p.Configuration()); await good.SaveAsync(new("ja", "en"));
        await using var bad = new ContentLanguagePreferencesStore(p.Configuration(), x => x == stage ? throw new IOException("synthetic") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => bad.SaveAsync(new("en"))); Assert.Equal(new("ja", "en"), (await good.LoadAsync()).Preference);
    }
    [Theory][InlineData("Version")][InlineData("ProfileId")][InlineData("StoreId")][InlineData("Preferred")][InlineData("Fallback")][InlineData("Future")]
    public async Task ForeignOrFutureSettingsRemainReadOnly(string field)
    {
        using var f = new ProfileFixture(); using var p = f.Open(); await using var store = new ContentLanguagePreferencesStore(p.Configuration()); await store.SaveAsync(new());
        var path = Path.Combine(f.Root, ContentLanguagePreferencesStore.FileName); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        json[field] = field == "Version" ? JsonValue.Create(2) : field is "ProfileId" or "StoreId" ? JsonValue.Create(Guid.NewGuid()) : JsonValue.Create("unsupported");
        File.WriteAllText(path, json.ToJsonString()); var before = File.ReadAllBytes(path);
        Assert.True((await store.LoadAsync()).ReadOnly); await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new("en")));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact] public async Task CancellationAndDisposedStoreDoNotWrite()
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var store = new ContentLanguagePreferencesStore(p.Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), new(true)));
        Assert.False(File.Exists(Path.Combine(f.Root, ContentLanguagePreferencesStore.FileName)));
        await store.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(new()));
    }
    private sealed class Store : IContentLanguagePreferencesStore
    {
        public ContentLanguagePreference Value = new(); public bool Fail; public TaskCompletionSource? Held;
        public Task<ContentLanguagePreferenceRead> LoadAsync(CancellationToken c = default) => Task.FromResult(new ContentLanguagePreferenceRead(Value));
        public async Task SaveAsync(ContentLanguagePreference v, CancellationToken c = default) { if (Held is not null) await Held.Task; if (Fail) throw new IOException("synthetic"); Value = v; }
    }
    [Fact] public async Task ConcurrentSaveAndDisposeAwaitAtomicDecisionWithoutApplyingLateSecondChoice()
    {
        var store = new Store { Held = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var applied = new List<ContentLanguagePreference>();
        var settings = new ContentLanguageSettings(store, applied.Add); await settings.InitializeAsync();
        var saving = settings.ChangeAsync(new("en")); await settings.ChangeAsync(new("ja"));
        var close = settings.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); store.Held.SetResult(); await saving; await close;
        Assert.Equal("en", store.Value.Preferred); Assert.Equal("en", applied.Last().Preferred);
    }
    [Fact] public async Task FailedSaveKeepsOldPreferenceAndNoLanguageIsApplied()
    {
        var store = new Store { Fail = true }; var applied = new List<ContentLanguagePreference>(); await using var settings = new ContentLanguageSettings(store, applied.Add);
        await settings.InitializeAsync(); await settings.ChangeAsync(new("en"));
        Assert.Equal(new(), settings.Preference); Assert.Single(applied); Assert.Contains("未能保存", settings.Message); Assert.True(settings.CanChange);
    }
}
