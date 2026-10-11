using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class LanguageTests
{
    [Theory]
    [InlineData("zh-CN", "zh-Hans")][InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh_TW", "zh-Hant")][InlineData("zh-HK", "zh-Hant")][InlineData("zh-MO-u-ca-chinese", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")][InlineData("ja-JP", "ja")][InlineData("en-GB", "en")][InlineData("de-DE", "en")]
    public void SystemUiLanguageResolutionAndExplicitOverride(string system, string expected)
    {
        Assert.Equal(expected, UiText.Resolve("system", system));
        Assert.Equal("ja", UiText.Resolve("ja", system));
        Assert.Throws<ArgumentException>(() => UiText.Resolve("unregistered", system));
    }
    [Fact]
    public async Task ConcurrentDisplayScopesDoNotChangeOperatingSystemCulture()
    {
        var before = CultureInfo.CurrentCulture; var ui = CultureInfo.CurrentUICulture;
        var names = await Task.WhenAll(new[]{"ja","en","zh-Hant"}.Select(async code => {
            using var scope = UiText.ForLanguage(code); await Task.Yield();
            Assert.Equal(code, UiText.Language); return UiText.Get("设置");
        }));
        Assert.Equal(new[]{"設定","Settings","設定"}, names);
        Assert.Equal(before, CultureInfo.CurrentCulture); Assert.Equal(ui, CultureInfo.CurrentUICulture);
    }
    [Theory][InlineData("en")][InlineData("ja")][InlineData("zh-Hant")]
    public void EveryResourceHasMatchingPlaceholdersAndCompleteLanguageCoverage(string language)
    {
        var baseline = UiText.Messages("en"); var table = UiText.Messages(language);
        Assert.Equal(baseline.Keys.Order(), table.Keys.Order()); Assert.True(table.Count >= 885);
        foreach (var (key,value) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.Equal(CompositeFormat.Parse(key).MinimumArgumentCount, CompositeFormat.Parse(value).MinimumArgumentCount);
            var index = new Regex(@"(?<!\{)\{(\d+)(?:[,}:])");
            Assert.Equal(index.Matches(key).Select(m=>m.Groups[1].Value).Order(), index.Matches(value).Select(m=>m.Groups[1].Value).Order());
            var count=CompositeFormat.Parse(value).MinimumArgumentCount;
            _ = string.Format(CultureInfo.InvariantCulture,value,Enumerable.Repeat<object>(3,count).ToArray());
        }
        Assert.Equal("日本語", table["日本語"]); Assert.Equal("简体中文",table["简体中文"]);
    }
    [Theory]
    [InlineData("zh-Hans", "37 / 37 部作品")]
    [InlineData("zh-Hant", "37 / 37 部作品")]
    [InlineData("ja", "37 / 37 作品")]
    [InlineData("en", "37 / 37 games")]
    public void ActualInterpolatedCallsTranslateBeforeFormatting(string language, string expected)
    {
        using var scope = UiText.ForLanguage(language);
        int visible = 37, total = 37;
        Assert.Equal(expected, UiText.Format($"{visible:N0} / {total:N0} 部作品"));
        // User text remains an argument, never a translation key.
        var title = "我的个人名称";
        var result = UiText.Format($"此目录已属于「{title}」，请核对已有安装。");
        Assert.Contains(title, result);
        if (language == "en") Assert.DoesNotContain("此目录", result);
        Assert.Contains("English", UiText.FormatKey("暂无所选语言的简介，显示原文（{0}）。", "English"));
    }
    private static WorkMetadata Map(string fields)
    {
        using var doc=JsonDocument.Parse("{\"object\":\"work\",\"id\":\"7001\",\"display_name\":\"Original Japanese\",\"olang\":\"ja\","+fields+"}");
        return new RemoteWorkDto(doc.RootElement).Map(true,DateTimeOffset.UnixEpoch);
    }
    [Theory][InlineData("zh-Hans")][InlineData("zh-Hant")][InlineData("ja")][InlineData("en")]
    public void RecognitionReasonsTranslateOnlyDisplayAndDoNotClaimUniquenessForReview(string language)
    {
        using var scope = UiText.ForLanguage(language);
        const string canonical = "完整官方标题唯一对应；候选达到上限，无法确认唯一性。";
        var shown = RecognitionDisplayText.Reason(canonical, false);
        Assert.Contains(UiText.Get("完整官方标题与本地名称一致"), shown);
        Assert.DoesNotContain(UiText.Get("完整官方标题唯一对应"), shown);
        Assert.Contains(UiText.Get("候选达到上限，无法确认唯一性。"), shown);
        Assert.Equal("完整官方标题唯一对应；候选达到上限，无法确认唯一性。", canonical);
        Assert.Equal("unchanged-user-value", RecognitionDisplayText.Message("unchanged-user-value"));
        if (language == "en") Assert.Contains("candidate", shown, StringComparison.OrdinalIgnoreCase);
        if (language == "ja") Assert.DoesNotContain("无法", shown);
    }
    [Theory][InlineData("zh-Hans","中文介绍")][InlineData("zh-Hant","繁體介紹")][InlineData("ja","日本語紹介")][InlineData("en","English description")]
    public void DescriptionLanguagePrecedesSourceButPreservesRecognitionAndPersonalData(string language,string expected)
    {
        var work=Map("\"intros\":[{\"source\":\"kungal\",\"lang\":\"en\",\"value\":\"English description\"},{\"source\":\"bangumi\",\"lang\":\"zh-Hans\",\"value\":\"中文介绍\"},{\"source\":\"curated\",\"lang\":\"zh-Hant\",\"value\":\"繁體介紹\"},{\"source\":\"vndb\",\"lang\":\"ja\",\"value\":\"日本語紹介\"}],\"localized\":{\"zh-Hans\":{\"value\":\"中文标题\"},\"en\":{\"value\":\"English title\"}}");
        Assert.Equal("中文介绍",work.Description);
        var entry=Entry("My untouched personal title") with {Metadata=work,NameIsCustom=true,Notes="日本語 **private**",CustomTags=["own"],TotalPlaySeconds=100};
        var codec=new NativeLibraryCodec();var bytes=codec.Encode([entry],Clock);
        using var scope=UiText.ForLanguage(language);var projected=CatalogLanguage.Project(work);
        Assert.Equal(expected,projected.Description);Assert.Null(CatalogLanguage.Fallback(work));
        Assert.Equal(work.Id,projected.Id);Assert.Equal(work.ReleaseDate,projected.ReleaseDate);
        Assert.Equal(work.TitleEvidence,projected.TitleEvidence);Assert.Equal(work.References,projected.References);
        Assert.Equal(bytes,codec.Encode([entry],Clock)); Assert.Equal("中文介绍",work.Description);
        Assert.Equal("My untouched personal title",entry.Name);
        Assert.Null(Assert.Single(codec.Decode(bytes)).Metadata!.Presentation);
    }
    [Fact]
    public void MissingLanguageIsHonestAndHumanTextOutranksMachineAtSameLanguage()
    {
        using var scope=UiText.ForLanguage("zh-Hans");
        var candidates=new[]{new CatalogLocalizedText("en","Original English","kungal"),new CatalogLocalizedText("zh-Hant","中文原文","vndb")};
        var p=new CatalogPresentation("kungal",[],[],[],[],[],[],[],[]) {LanguageSchema=1,Introductions=candidates};
        var work=Work() with {Presentation=p}; Assert.Equal("中文原文",CatalogLanguage.Project(work).Description);
        Assert.Contains("繁體中文",CatalogLanguage.Fallback(work));
        Assert.Equal("Human",CatalogLanguage.Select([new("zh-Hans","Machine","kungal",true),new("zh-CN","Human","bangumi")])!.Value);
        Assert.Equal("Kun",CatalogLanguage.Select([new("zh-Hans","Other","bangumi"),new("zh-Hans","Kun","curated")])!.Value);
    }
    [Fact]
    public void LegacyCacheSignatureDoesNotGainNullLanguageProperties()
    {
        var old=CachedCatalogWork.From(Work() with {Presentation=new("kungal",[],[],[],[],[],[],[],[])});
        var json=JsonSerializer.Serialize(old);Assert.DoesNotContain("LanguageSchema",json);Assert.DoesNotContain("Introductions",json);Assert.DoesNotContain("Names",json);
        var restored=JsonSerializer.Deserialize<CachedCatalogWork>(json)!;Assert.Equal(CatalogPresentationService.Signature(old,null),CatalogPresentationService.Signature(restored,null));
    }
    [Theory][InlineData("en")][InlineData("ja")]
    public void MissingTranslatedNamesUseOriginalForBothCharacterAndVoice(string language)
    {
        var work=Map("\"characters\":[{\"id\":\"42\",\"display_name\":\"仲村 ゆり\",\"lang\":\"ja\",\"localized\":{\"ja\":{\"value\":\"Nakamura Yuri\"}},\"voices\":[{\"id\":\"582\",\"display_name\":\"櫻井浩美\",\"lang\":\"ja\",\"localized\":{\"zh-Hans\":{\"value\":\"樱井浩美\"}}}]}]");
        using var scope=UiText.ForLanguage(language);var shown=Assert.Single(CatalogLanguage.Project(work).Presentation!.Characters);
        Assert.Equal("仲村 ゆり",shown.Entity.Name);Assert.Equal("櫻井浩美",Assert.Single(shown.Voices).Name);
        Assert.Equal("樱井浩美",Assert.Single(work.Presentation!.Characters[0].Voices).Name);
        Assert.Equal("ja",Assert.Single(shown.Voices).Names!.Single(n=>n.Source=="original").Language);
    }
    [Fact]
    public void PublishedLatinAndEnglishNamesWinWithoutInventingAliasReadings()
    {
        var work=Map("\"characters\":[{\"id\":\"42\",\"display_name\":\"仲村 ゆり\",\"lang\":\"ja\",\"latin\":\"Yuri Nakamura\",\"voices\":[{\"id\":\"582\",\"display_name\":\"櫻井浩美\",\"lang\":\"ja\",\"localized\":{\"en\":{\"value\":\"Harumi Sakurai\"}},\"aliases\":[{\"lang\":\"zh-Hans\",\"value\":\"风音\"}]}]}]");
        using var scope=UiText.ForLanguage("en");var shown=Assert.Single(CatalogLanguage.Project(work).Presentation!.Characters);
        Assert.Equal("Yuri Nakamura",shown.Entity.Name);Assert.Equal("Harumi Sakurai",Assert.Single(shown.Voices).Name);
        using var japanese=UiText.ForLanguage("ja");Assert.Equal("仲村 ゆり",CatalogLanguage.Name(shown.Entity));
    }
    [Theory][InlineData("en","Angels","Shared characters")][InlineData("ja","天使","共通キャラクター")][InlineData("zh-Hant","天使","角色出演")]
    public void VocabularyAndRelationLabelsTranslateButKeepIdsAndCanonicalFacts(string language,string tag,string relation)
    {
        var work=Map("\"tags\":[{\"id\":\"54\",\"display_name\":\"天使\"}],\"relations\":[{\"relation_type\":\"shares_character\",\"phrase\":\"角色出演\",\"work\":{\"object\":\"work\",\"id\":\"200\",\"medium\":\"galgame\",\"claim\":{\"site\":\"kungal\",\"state\":\"live\",\"site_work_id\":\"200\"},\"display_name\":\"Related work\"}}]");
        using var scope=UiText.ForLanguage(language);var shown=CatalogLanguage.Project(work).Presentation!;
        Assert.Equal(tag,Assert.Single(shown.Tags).Name);Assert.Equal("54",shown.Tags[0].Id);
        Assert.Equal(relation,Assert.Single(shown.RelatedWorks).Subtitle);Assert.Equal("200",shown.RelatedWorks[0].Id);
        Assert.Equal("角色出演",work.Presentation!.RelatedWorks[0].Subtitle);Assert.Equal("天使",work.Presentation.Tags[0].Name);
        Assert.Equal("未知的资料源专有词",CatalogVocabulary.Name("未知的资料源专有词"));
    }
    [Fact]
    public void PublishedRelationDirectionIsPreservedWhenTypeDoesNotEncodeDirection()
    {
        using var scope=UiText.ForLanguage("en");
        var entity=new CatalogEntityRef(CatalogEntityKind.Work,"200","Related","被改编为",Relation:"adaptation_of");
        Assert.Equal("Adapted as",CatalogLanguage.Project(entity).Subtitle);
        Assert.Equal("被改编为",entity.Subtitle);
    }
    [Theory][InlineData("zh-Hans","未公开姓名")][InlineData("zh-Hant","未公開姓名")][InlineData("ja","名前非公開")][InlineData("en","Unnamed character")]
    public void AnonymousCharacterLabelDoesNotInventNameOrChangeCatalogIdentity(string language,string expected)
    {
        using var scope=UiText.ForLanguage(language);
        var original=new CatalogEntityRef(CatalogEntityKind.Character,"99","??????") {Names=[new("","??????","original")]};
        var shown=CatalogLanguage.Project(original);Assert.Equal(expected,shown.Name);Assert.Equal("99",shown.Id);
        Assert.Equal("??????",original.Name);Assert.Equal(original.Names,shown.Names);
        Assert.Equal("Who?",CatalogLanguage.Name(original with {Name="Who?",Names=null}));
    }
    [Theory][InlineData("en","Editing","Quality assurance")][InlineData("ja","編集","品質管理")][InlineData("zh-Hant","編輯","品質檢查")][InlineData("zh-Hans","编辑","质量检查")]
    public void CatalogProfessionsTranslateWithoutChangingUiEditActionOrStoredRoles(string language,string edit,string qa)
    {
        using var scope=UiText.ForLanguage(language);
        Assert.Equal(edit,CatalogStaffRoles.Name("编辑","text-editor"));Assert.Equal(qa,CatalogStaffRoles.Name("QA"));
        Assert.True(CatalogStaffRoles.IsVoiceActing("声优","voice-actor"));Assert.False(CatalogStaffRoles.IsVoiceActing("导演","director"));
        Assert.Equal("未知职位",CatalogStaffRoles.Name("未知职位"));
        var work=Map("\"credits\":[{\"role_key\":\"text-editor\",\"role_name\":\"编辑\",\"credits\":[{\"id\":\"321\",\"display_name\":\"Person\"}]}]");
        var credit=Assert.Single(CatalogLanguage.Project(work).Presentation!.Credits);
        Assert.Equal("编辑",credit.Role);Assert.Equal("text-editor",credit.RoleKey);Assert.Equal("321",Assert.Single(credit.People).Id);
        if(language=="en")Assert.Equal("Edit",UiText.Get("编辑"));
    }
    [Fact]
    public void AllObservedPublicStaffLabelsHaveFourNonemptyTranslations()
    {
        string[] roles=["CG 监修","QA","SD原画","上色","主题歌作曲","主题歌作词","主题歌演出","人物设定","企画","作曲","作词","其他","制作人","制作总指挥","剧本","动画制作","动画监督","协力","原作","原画","发行","声乐","声优","宣传","导演","开发","录音工作室","执行制片人","插入歌演出","插画","标题设计","游戏设计师","演出","特别鸣谢","监修","程序","编曲","编辑","美工","翻译","背景","脚本","音乐","音响","音响监督","音效"];
        foreach(var role in roles){Assert.True(CatalogStaffRoles.HasTranslation(role),role);foreach(var language in new[]{"zh-Hans","zh-Hant","ja","en"})Assert.False(string.IsNullOrWhiteSpace(CatalogStaffRoles.Translate(role,language)));}
    }
}

[Collection("Native profile resource")]
public sealed class LanguagePreferenceTests
{
    [Theory][InlineData("system")][InlineData("zh-Hans")][InlineData("zh-Hant")][InlineData("ja")][InlineData("en")]
    public async Task SelectionSurvivesOrdinaryReopenWithoutChangingLibraryOrPreviousPreferences(string language)
    {
        using var f=new ProfileFixture();string[] names=["library.json","native-ui.json",NativeProfileStore.ManifestName];byte[][] original;
        using(var p=f.Open())
        {
            original=names.Select(n=>File.ReadAllBytes(Path.Combine(f.Root,n))).ToArray();
            await using var s=new LanguagePreferencesStore(p.Configuration());Assert.Equal("system",(await s.LoadAsync()).Preference.Language);
            await s.SaveAsync(new(language));
        }
        using var next=f.Open();await using var store=new LanguagePreferencesStore(next.Configuration());Assert.Equal(language,(await store.LoadAsync()).Preference.Language);
        for(int i=0;i<names.Length;i++)Assert.Equal(original[i],File.ReadAllBytes(Path.Combine(f.Root,names[i])));
    }
    [Theory][InlineData("version")][InlineData("profile")][InlineData("store")][InlineData("unknown")][InlineData("language")][InlineData("oversize")]
    public async Task ForeignOrFutureChoiceCannotBeSilentlyOverwritten(string fault)
    {
        using var f=new ProfileFixture();using var p=f.Open();await using var s=new LanguagePreferencesStore(p.Configuration());await s.SaveAsync(new("ja"));
        var path=Path.Combine(f.Root,LanguagePreferencesStore.FileName);var json=JsonNode.Parse(File.ReadAllBytes(path))!;
        if(fault=="version")json["Version"]=2;else if(fault=="profile")json["ProfileId"]=Guid.NewGuid();else if(fault=="store")json["StoreId"]=Guid.NewGuid();else if(fault=="language")json["Language"]="future-lang";else json["Future"]=true;
        File.WriteAllText(path,fault=="oversize"?new string('x',4097):json.ToJsonString());var before=File.ReadAllBytes(path);
        var read=await s.LoadAsync();Assert.True(read.ReadOnly);Assert.Equal("system",read.Preference.Language);
        await Assert.ThrowsAsync<InvalidDataException>(()=>s.SaveAsync(new("en")));Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Theory][InlineData("corrupt")][InlineData("duplicate")][InlineData("null")]
    public async Task InvalidOptionalFileFallsBackWithoutWritingUntilExplicitSelection(string fault)
    {
        using var f=new ProfileFixture();using var p=f.Open();await using var s=new LanguagePreferencesStore(p.Configuration());await s.SaveAsync(new("ja"));
        var path=Path.Combine(f.Root,LanguagePreferencesStore.FileName);var data=JsonNode.Parse(File.ReadAllBytes(path))!;
        data["Language"]=null;File.WriteAllText(path,fault=="corrupt"?"{":fault=="duplicate"?data.ToJsonString().Replace("{","{\"Language\":\"en\",",StringComparison.Ordinal):data.ToJsonString());
        var before=File.ReadAllBytes(path);var read=await s.LoadAsync();Assert.NotNull(read.Warning);Assert.Equal("system",read.Preference.Language);Assert.Equal(before,File.ReadAllBytes(path));
        if(read.ReadOnly)await Assert.ThrowsAsync<InvalidDataException>(()=>s.SaveAsync(new("en")));
        else {await s.SaveAsync(new("en"));Assert.Equal("en",(await s.LoadAsync()).Preference.Language);}
    }
    [Theory][InlineData(PersistenceStage.DuringTempWrite)][InlineData(PersistenceStage.BeforeReplace)]
    public async Task InterruptedAtomicWriteKeepsPreviousChoice(PersistenceStage stage)
    {
        using var f=new ProfileFixture();using var p=f.Open();await using var good=new LanguagePreferencesStore(p.Configuration());await good.SaveAsync(new("ja"));
        await using var failed=new LanguagePreferencesStore(p.Configuration(),s=>s==stage?throw new IOException("synthetic"):Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(()=>failed.SaveAsync(new("en")));Assert.Equal("ja",(await good.LoadAsync()).Preference.Language);
        Assert.Empty(Directory.GetFiles(f.Root,LanguagePreferencesStore.FileName+".*.tmp"));
    }
    [Fact]public async Task CancellationAndDisposedAdapterNeverWrite()
    {
        using var f=new ProfileFixture();using var p=f.Open();var s=new LanguagePreferencesStore(p.Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>s.SaveAsync(new("ja"),new(true)));Assert.False(File.Exists(Path.Combine(f.Root,LanguagePreferencesStore.FileName)));
        await s.DisposeAsync();await Assert.ThrowsAsync<ObjectDisposedException>(()=>s.SaveAsync(new("ja")));
    }
    private sealed class Store:ILanguagePreferencesStore
    {
        public string Choice="system";public bool Fail; public TaskCompletionSource? Held;
        public Task<LanguagePreferenceRead> LoadAsync(CancellationToken c=default)=>Task.FromResult(new LanguagePreferenceRead(new(Choice)));
        public async Task SaveAsync(LanguagePreference p,CancellationToken c=default){if(Held is not null)await Held.Task;if(Fail)throw new IOException("synthetic");Choice=p.Language;}
    }
    [Fact]public async Task ChineseOnlyBuildPreservesSavedChoiceAndCanReenableMultilingualLater()
    {
        var store=new Store {Choice="en"};
        try
        {
            await using(var chinese=new LanguageSettings(store,"ja-JP",false))
            {
                await chinese.InitializeAsync();Assert.Equal("en",chinese.Selected);Assert.Equal("zh-Hans",chinese.Effective);
                Assert.Equal("zh-Hans",UiText.Language);Assert.False(chinese.SelectionAvailable);Assert.False(chinese.CanChange);
                await chinese.ChangeAsync("ja");Assert.Equal("en",store.Choice);Assert.Equal("en",chinese.Selected);
            }
            await using var future=new LanguageSettings(store,"ja-JP",true);await future.InitializeAsync();
            Assert.True(future.SelectionAvailable);Assert.True(future.CanChange);Assert.Equal("en",future.Effective);
        }
        finally {UiText.Configure("system","zh-CN");}
    }
    [Fact]public async Task UiChangeSavesForRestartAndDrainsPendingWrite()
    {
        var store=new Store{Held=new(TaskCreationOptions.RunContinuationsAsynchronously)};var s=new LanguageSettings(store,"zh-CN");
        try {
            await s.InitializeAsync();var write=s.ChangeAsync("ja");Assert.False(s.CanChange);Assert.Equal("zh-Hans",s.Effective);Assert.Equal("system",s.Selected);
            var close=s.DisposeAsync().AsTask();Assert.False(close.IsCompleted);store.Held.SetResult();await write;await close;
            Assert.Equal("ja",store.Choice);Assert.Equal("ja",s.Selected);Assert.Equal("zh-Hans",s.Effective);
            var reopen=new LanguageSettings(store,"en-US");await reopen.InitializeAsync();Assert.Equal("ja",reopen.Effective);await reopen.DisposeAsync();
        } finally {UiText.Configure("system","zh-CN");}
    }
    [Fact]public async Task FailedSaveKeepsPreviousSelection()
    {
        var store=new Store{Fail=true};await using var s=new LanguageSettings(store,"zh-CN");await s.InitializeAsync();await s.ChangeAsync("en");
        Assert.Equal("system",s.Selected);Assert.Equal("zh-Hans",s.Effective);Assert.Contains("未能保存",s.Message);Assert.True(s.CanChange);
    }
}
