using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;
namespace TheOrder.Tests;
public sealed class MusicContextRetrievalTests
{
    private static MusicAlbumSource Source()=>new("172618","Blue sky","2016-05-27",new([], ["レナ・リヒテナウアー（CV：種﨑敦美）"], [], null,1,[]));
    private static string Result(bool correct)=>JsonSerializer.Serialize(new{count=1,releases=new[]{new{
        id=correct?MusicTestData.Release:MusicTestData.Other,title=correct?"千恋*万花 キャラクターソング Vol.4 「Blue sky」":"Blue sky",date="2016-05-27",status="Official",
        media=new[]{new Dictionary<string,object>{["format"]="CD",["track-count"]=3}},
        artist_credit_not_api="", // Actual API uses artist-credit, serialized below.
    }}}).Replace("\"artist_credit_not_api\":\"\"", "\"artist-credit\":[{\"name\":\""+(correct?"レナ・リヒテナウアー(CV:沢澤砂羽)":"Unrelated performer")+"\"}]");
    [Fact] public async Task SameTitleAndDateByUnrelatedArtistCannotStopContextRetrieval()
    {
        using var handler=new MusicTestData.Handler((request,_)=>Task.FromResult(MusicTestData.Response(Result(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("artist:",StringComparison.Ordinal)))));
        using var http=new HttpClient(handler);
        var result=await new MusicBrainzProvider(http,new MusicTestData.Clock().Rate()).SearchAsync(Source(),default);
        Assert.Equal(MusicTestData.Release,Assert.Single(result.Candidates).Id);
        Assert.Null(MusicBrainzMatching.Automatic(Source(),result));
        Assert.Contains(handler.Urls,url=>Uri.UnescapeDataString(url).Contains("artist:",StringComparison.Ordinal));
    }
    [Fact] public void RetrievalEvidenceUsesArtistBeforeUnqualifiedExactShortTitle()
    {
        var source=Source();
        var wrong=MusicTestData.Candidate(MusicTestData.Other) with {Title="Blue sky",Date=source.ReleaseDate,Artist="Unrelated performer",Labels=[],DiscCount=1};
        var right=wrong with{Id=MusicTestData.Release,Title="千恋*万花 キャラクターソング Vol.4 「Blue sky」",Artist="レナ・リヒテナウアー(CV:沢澤砂羽)"};
        Assert.True(MusicBrainzSearchQueries.EvidenceOrder(source,right).CompareTo(MusicBrainzSearchQueries.EvidenceOrder(source,wrong))>0);
        Assert.Null(MusicBrainzMatching.Automatic(source,new([right],1,false,null,null)));
    }
}
