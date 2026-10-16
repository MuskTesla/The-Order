using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class ProviderSynopsisLanguageTests
{
    private const string Japanese = "学園での日常から物語は始まる。";
    private const string Chinese = "故事从校园日常开始。";
    private const string English = "The story begins at school.";

    private static WorkMetadata Work(string chineseLanguage = "zh-Hans", bool human = false, bool reverse = false)
    {
        var introductions = new[]
        {
            new { lang = "en", value = English, source = "curated", is_machine = false },
            new { lang = "ja", value = Japanese, source = "bangumi", is_machine = false },
            new { lang = chineseLanguage, value = Chinese, source = "bangumi", is_machine = !human }
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            @object = "work", id = "86", display_name = "CLANNAD", olang = "ja",
            refs = new[] { new { source = "vndb", external_id = "v4" }, new { source = "bangumi", external_id = "13" } },
            intros = reverse ? introductions.Reverse().ToArray() : introductions
        }));
        return new RemoteWorkDto(document.RootElement).Map(false, DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-CN")]
    [InlineData("zh")]
    public void ProviderChineseTranslationIsUsedInsteadOfForeignOriginal(string language)
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        var work = Work(language);
        var selection = ContentLanguage.CatalogSynopsis(work);
        Assert.Equal(Chinese, selection.Text);
        Assert.True(selection.Selected!.IsMachine);
        Assert.False(selection.Selected.IsOriginal);
        Assert.Equal("bangumi", selection.Selected.Attribution);
        Assert.Equal(new ContentIdentity("NextMoe", "Work", "86"), selection.Selected.Identity);
        Assert.Contains("机器", selection.Caption);
        Assert.Equal(Chinese, CatalogLanguage.Project(work).Description);
        Assert.Null(CatalogLanguage.Fallback(work));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonMachineChineseWinsRegardlessOfProviderOrder(bool reverse)
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        var work = Work(reverse: reverse);
        work = work with { Presentation = work.Presentation! with
        {
            Introductions = [..work.Presentation.Introductions!, new("zh-Hans", "资料源提供的中文介绍。", "curated")]
        } };
        var selection = ContentLanguage.CatalogSynopsis(work);
        Assert.Equal("资料源提供的中文介绍。", selection.Text);
        Assert.False(selection.Selected!.IsMachine);
    }

    [Fact]
    public void AvailableHumanChineseIsPreferredToMachineChineseAcrossScripts()
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        var work = Work();
        work = work with { Presentation = work.Presentation! with
        {
            Introductions = [..work.Presentation.Introductions!, new("zh-Hant", "故事從校園日常開始。", "curated")]
        } };
        Assert.Equal("故事從校園日常開始。", ContentLanguage.CatalogSynopsis(work).Text);
    }

    [Theory]
    [InlineData("en", English)]
    [InlineData("ja", Japanese)]
    [InlineData("original", Japanese)]
    public void ExplicitOtherLanguagePreferenceIsRespected(string preferred, string expected)
    {
        using var preference = ContentLanguage.ForPreference(new(preferred));
        Assert.Equal(expected, ContentLanguage.CatalogSynopsis(Work()).Text);
    }

    [Fact]
    public void TranslationInConfiguredFallbackLanguagePrecedesUnrelatedOriginal()
    {
        using var preference = ContentLanguage.ForPreference(new("en", "zh-Hans"));
        var work = Work();
        work = work with { Presentation = work.Presentation! with
        {
            Introductions = [..work.Presentation.Introductions!.Where(x => x.Language != "en")]
        } };
        Assert.Equal(Chinese, ContentLanguage.CatalogSynopsis(work).Text);
    }

    [Theory]
    [InlineData("und")]
    [InlineData("fr")]
    public void UnknownOrUnrequestedMachineLanguageDoesNotBecomeAutomaticFallback(string language)
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        Assert.Equal(Japanese, ContentLanguage.CatalogSynopsis(Work(language)).Text);
    }

    [Fact]
    public void ExistingCachedVariantsUseFixedPolicyWithoutRefreshOrDataRewrite()
    {
        var work = Work();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CachedCatalogWork.From(work));
        var restored = JsonSerializer.Deserialize<CachedCatalogWork>(bytes)!.Work;
        using (ContentLanguage.ForPreference(new("zh-Hans")))
            Assert.Equal(Chinese, CatalogLanguage.Project(restored).Description);
        using (ContentLanguage.ForPreference(new("ja")))
            Assert.Equal(Japanese, CatalogLanguage.Project(restored).Description);
        using (ContentLanguage.ForPreference(new("zh-Hans")))
            Assert.Equal(Chinese, CatalogLanguage.Project(restored).Description);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(CachedCatalogWork.From(restored)));
        Assert.Equal(work.Id, restored.Id);
        Assert.Equal(work.TitleEvidence?.ToArray() ?? [], restored.TitleEvidence?.ToArray() ?? []);
        Assert.Equal(work.References.ToArray(), restored.References.ToArray());
    }

    [Fact]
    public void ProviderTitleTranslationIsDisplayOnlyAndKeepsItsSourceIdentity()
    {
        using var preference = ContentLanguage.ForPreference(new("zh-Hans"));
        var result = ContentLanguage.Catalog(new("NextMoe", "Work", "86"), "title",
            [new("ja", "CLANNAD", "original"), new("zh-Hans", "资料源译名", "bangumi", true)], "CLANNAD", "ja");
        Assert.Equal("资料源译名", result.Text);
        Assert.True(result.Selected!.IsMachine);
        Assert.False(result.Selected.IsOriginal);
        Assert.Equal("bangumi", result.Selected.Attribution);
        Assert.Equal("86", result.Selected.Identity.Id);
        Assert.Equal("CLANNAD", result.OriginalText);
    }
}
