using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Recognition;
using Xunit;

namespace TheOrder.Tests;

public sealed class RecognitionCoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static IEnumerable<object[]> TitleCases()
    {
        using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","recognition-oracle.json")));
        int i=0;foreach(var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            if(c.GetProperty("operation").GetString()!="rankEvidenceMatches")yield return [i++,c.GetRawText()];
    }
    [Theory][MemberData(nameof(TitleCases))]
    public void ActualElectronTitleBehaviorMatches(int caseId,string json)
    {
        _=caseId;using var c=JsonDocument.Parse(json);var root=c.RootElement;var args=root.GetProperty("input");
        object value=root.GetProperty("operation").GetString() switch {
            "normalizeGameTitle"=>TitleMatching.Normalize(args[0].GetString()!),
            "gameTitleVariants"=>TitleMatching.Variants(args[0].GetString()!),
            "hasExplicitGameVersion"=>TitleMatching.HasVersion(args[0].GetString()!),
            "compareGameTitles"=>TitleMatching.Compare(args[0].GetString()!,args[1].GetString()!),
            _=>throw new InvalidOperationException()};
        Assert.True(JsonElement.DeepEquals(root.GetProperty("expected"),JsonSerializer.SerializeToElement(value,JsonOptions)),root.GetProperty("group").GetString()+"\n"+json+"\nActual: "+JsonSerializer.Serialize(value,JsonOptions));
    }
    [Fact] public void OracleTracksCurrentElectronSources()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"package.json")))directory=directory.Parent;
        Assert.NotNull(directory);
        using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","recognition-oracle.json")));
        foreach(var source in doc.RootElement.GetProperty("sources").EnumerateObject())Assert.Equal(source.Value.GetString(),Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory.FullName,source.Name)))));
    }
    public static IEnumerable<object[]> RankingCases()
    {
        using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","recognition-oracle.json")));
        int i=0;foreach(var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            if(c.GetProperty("operation").GetString()=="rankEvidenceMatches")yield return [i++,c.GetRawText()];
    }
    [Theory][MemberData(nameof(RankingCases))]
    public void ActualElectronEvidenceRankingMatches(int caseId,string json)
    {
        _=caseId;using var c=JsonDocument.Parse(json);var root=c.RootElement;var args=root.GetProperty("input");
        var identity=args[0];var evidence=identity.GetProperty("evidence").Deserialize<RecognitionEvidence>(JsonOptions)!;
        var works=args[1].Deserialize<WorkMetadata[]>(JsonOptions)!;
        var releases=identity.TryGetProperty("releases",out var rows)?rows.Deserialize<Dictionary<string,ImmutableArray<ReleaseIdentity>>>(JsonOptions):null;
        var actual=new EvidenceRanking().Rank(evidence,works,releases);
        var expected=root.GetProperty("expected").EnumerateArray().ToArray();Assert.Equal(expected.Length,actual.Length);
        for(int i=0;i<expected.Length;i++) {
            Assert.Equal(expected[i].GetProperty("work").GetProperty("id").GetString(),actual[i].Work.Id);
            Assert.Equal(expected[i].GetProperty("confidence").GetDouble(),actual[i].Confidence,10);
            Assert.Equal(expected[i].GetProperty("automatic").GetBoolean(),actual[i].HighConfidence);
            Assert.Equal(expected[i].GetProperty("reason").GetString(),actual[i].Reason);
            Assert.False(actual[i].Vetoed&&actual[i].HighConfidence);
        }
        Assert.Equal(actual.Select(r=>r.Work.Id),new EvidenceRanking().Rank(evidence,works.Reverse(),releases).Select(r=>r.Work.Id));
    }
    [Fact] public void TitleComparisonDoesNotDependOnCurrentCulture()
    {
        var before=CultureInfo.CurrentCulture;try {var expected=TitleMatching.Compare("Ｃａｆé Title II","Cafe Title 2");CultureInfo.CurrentCulture=new("tr-TR");Assert.Equal(expected,TitleMatching.Compare("Ｃａｆé Title II","Cafe Title 2"));}finally{CultureInfo.CurrentCulture=before;}
    }
}
