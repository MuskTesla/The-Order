using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;
namespace TheOrder.Tests;
public sealed class ChannelRollbackCompatibilityTests
{
    private static WorkMetadata Work(string id) => new(id,"Synthetic work","Original",[],null,null,null,null,"Description",null,null,[],[],[],[],[],null,null,SessionTestRig.Date);
    private static LibraryEntry Enhanced(string id="g") => SessionTestRig.Entry(id) with { ManualPlaytime=[new(Guid.NewGuid().ToString("D"),"2026-10-16",10,"synthetic",SessionTestRig.Date)] };
    [Theory] [InlineData("source")] [InlineData("target")]
    public async Task OlderMergeCannotDropEitherSidesEnhancedUserRecords(string side)
    {
        var source=side=="source"?Enhanced():SessionTestRig.Entry();
        var target=(side=="target"?Enhanced("target"):SessionTestRig.Entry("target")) with { Metadata=Work("123"), Installations=[], PreferredInstallationId=null };
        await using var rig=new SessionTestRig(SessionTestRig.Codec.Encode([source,target],SessionTestRig.Date));var original=rig.Disk.Main!.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(()=>rig.Library.LinkAsync("g",Work("123")));
        Assert.Equal(original,rig.Disk.Main);
    }
    [Fact]
    public async Task UnsupportedFutureExtrasRemainReadOnlyInsteadOfDiscardingFields()
    {
        var document=JsonNode.Parse(SessionTestRig.Codec.Encode([Enhanced()],SessionTestRig.Date))!;
        document["games"]![0]!["nativeExtras"]!["version"]=99;
        var bytes=System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());await using var rig=new SessionTestRig(bytes);
        Assert.Equal(LibraryAccess.ValidReadOnly,rig.Library.Snapshot.Access);
        await Assert.ThrowsAsync<LibraryReadOnlyException>(()=>rig.Library.UpdateAsync("g",new(){Notes="must not write"}));Assert.Equal(bytes,rig.Disk.Main);
    }
}
