using System.Net;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Online;
using Xunit;
using static TheOrder.Tests.OnlineTransportTests;

namespace TheOrder.Tests;

public sealed class CatalogEntityTests
{
    public static IEnumerable<object[]> Kinds()
    {
        yield return [CatalogEntityKind.Character,"characters","character"];
        yield return [CatalogEntityKind.Company,"companies","company"];
        yield return [CatalogEntityKind.CreditName,"credit-names","credit_name"];
        yield return [CatalogEntityKind.Person,"persons","person"];
        yield return [CatalogEntityKind.Tag,"tags","tag"];
        yield return [CatalogEntityKind.Engine,"engines","engine"];
        yield return [CatalogEntityKind.Series,"series","series"];
        yield return [CatalogEntityKind.Trait,"traits","trait"];
    }
    private static HttpResponseMessage Detail(string obj,string id) => Json(JsonSerializer.Serialize(new { @object=obj,id,display_name="Synthetic",intros=new[]{new {source="curated",lang="zh-Hans",value="**简介**"}} }));
    [Theory,MemberData(nameof(Kinds))]
    public async Task EntityRequestsUseClosedFamilyAndVerifyReturnedIdentity(CatalogEntityKind kind,string family,string obj)
    {
        int calls=0;
        using var http=new HttpClient(new Handler((r,_)=>{
            calls++; Assert.Equal("api.nextmoe.dev",r.RequestUri!.Host); Assert.NotNull(r.Headers.Authorization);
            Assert.DoesNotContain("synthetic",r.RequestUri!.ToString());
            if(r.RequestUri!.AbsolutePath.EndsWith("/credit-names"))return Task.FromResult(Json("{\"object\":\"list\",\"items\":[]}"));
            Assert.Equal("/v2/catalog/"+family+"/42",r.RequestUri.AbsolutePath); return Task.FromResult(Detail(obj,"42"));
        }));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        var result=await catalog.EntityAsync(kind,"42",default); Assert.Equal(kind,result.Entity.Kind);Assert.Equal("42",result.Entity.Id);
        Assert.Equal("**简介**",result.Description); Assert.Equal(kind==CatalogEntityKind.Person?2:1,calls);
    }
    [Theory][InlineData("41","character")][InlineData("42","company")]
    public async Task WrongEntityResponseCannotBeShownAsRequestedCharacter(string id,string obj)
    {
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Detail(obj,id))));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidResponse,(await Assert.ThrowsAsync<OnlineException>(()=>catalog.EntityAsync(CatalogEntityKind.Character,"42",default))).Failure);
    }
    [Theory,MemberData(nameof(Kinds))]
    public async Task EntityMergeStaysInItsFamily(CatalogEntityKind kind,string family,string obj)
    {
        int calls=0;
        using var http=new HttpClient(new Handler((r,_)=>{
            if(r.RequestUri!.AbsolutePath.EndsWith("/credit-names"))return Task.FromResult(Json("{\"object\":\"list\",\"items\":[]}"));
            Assert.StartsWith("/v2/catalog/"+family+"/",r.RequestUri!.AbsolutePath);
            return Task.FromResult(++calls==1?Json(JsonSerializer.Serialize(new{@object=obj,code="ENTITY_MERGED",current_id="99"}),404):Detail(obj,"99"));
        }));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        Assert.Equal("99",(await catalog.EntityAsync(kind,"42",default)).Entity.Id); Assert.Equal(2,calls);
    }
    [Fact]
    public async Task CrossFamilyMergeIsRejectedBeforeFollowingIt()
    {
        int calls=0;using var http=new HttpClient(new Handler((_,_)=>{calls++;return Task.FromResult(Json("{\"object\":\"work\",\"code\":\"ENTITY_MERGED\",\"current_id\":\"99\"}",404));}));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidResponse,(await Assert.ThrowsAsync<OnlineException>(()=>catalog.EntityAsync(CatalogEntityKind.Character,"42",default))).Failure);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task CharacterTraitsUseVerifiedCharacterTraitShapeAndOmitSpoilers()
    {
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Json("{\"object\":\"character\",\"id\":\"42\",\"display_name\":\"Person\",\"traits\":[{\"object\":\"character_trait\",\"id\":\"7\",\"display_name\":\"Blond\",\"spoiler\":\"none\"},{\"object\":\"character_trait\",\"id\":\"8\",\"display_name\":\"Secret\",\"spoiler\":\"major\"}]}"))));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        Assert.Equal("7",Assert.Single((await catalog.EntityAsync(CatalogEntityKind.Character,"42",default)).Related).Id);
    }
    [Fact]
    public async Task LateEntityResponseIsRejectedAfterCredentialIntentChanges()
    {
        var waiting=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store=new Credential();var credentials=new AuthenticatedCredentials(store,store);
        using var http=new HttpClient(new Handler((_,_)=>waiting.Task));
        var catalog=new NextMoeCatalog(new(http,credentials,new("https://api.nextmoe.dev/v2/")));
        var old=catalog.EntityAsync(CatalogEntityKind.Character,"42",default);
        await credentials.SaveAsync("new-synthetic",default);waiting.SetResult(Detail("character","42"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>old);
    }
    [Fact]
    public async Task CancelledTraitPageCannotBecomeUsableResult()
    {
        using var cancel=new CancellationTokenSource();
        using var http=new HttpClient(new Handler((_,_)=>{cancel.Cancel();return Task.FromResult(Json("{\"object\":\"list\",\"items\":[]}"));}));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.TraitCharactersAsync("7",null,cancel.Token));
    }
    [Fact]
    public async Task TraitPaginationHasFixedFilterNoSecretsAndPreservesCursor()
    {
        using var http=new HttpClient(new Handler((r,_)=>{
            Assert.Equal("/v2/catalog/characters",r.RequestUri!.AbsolutePath);
            Assert.Contains("trait_id=7",r.RequestUri.Query);Assert.Contains("cursor=cur_next",r.RequestUri.Query);Assert.Contains("limit=20",r.RequestUri.Query);
            return Task.FromResult(Json("{\"object\":\"list\",\"items\":[{\"object\":\"character\",\"id\":\"42\",\"display_name\":\"Person\",\"image\":{\"url\":\"https://example.org/image.png\"}}],\"next_cursor\":\"cur_after\"}"));
        }));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        var page=await catalog.TraitCharactersAsync("7","cur_next",default);Assert.Equal("cur_after",page.NextCursor);Assert.Equal("42",Assert.Single(page.Items).Id);
    }
    [Theory][InlineData("../7",null)][InlineData("7","../cursor")][InlineData("7","")]
    public async Task InvalidEntityOrCursorCannotCauseRequest(string id,string? cursor)
    {
        int calls=0;using var http=new HttpClient(new Handler((_,_)=>{calls++;return Task.FromResult(Json("{}"));}));
        var catalog=new NextMoeCatalog(new(http,new Credential(),new("https://api.nextmoe.dev/v2/")));
        Assert.Equal(OnlineFailure.InvalidRequest,(await Assert.ThrowsAsync<OnlineException>(()=>catalog.TraitCharactersAsync(id,cursor,default))).Failure);Assert.Equal(0,calls);
    }
}
