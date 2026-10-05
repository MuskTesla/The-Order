using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Performance;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class ProfileValidationPerformanceTests
{
    [Theory][InlineData(1000)][InlineData(10000)]
    public async Task FullArrayValidationStillRejectsUnknownNestedFieldAtLastRecord(int count)
    {
        using var fixture = new ProfileFixture(); var codec = new NativeLibraryCodec();
        using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, fixture.Root), codec);
        var bytes = codec.Encode(Program.Dataset(1, count), Program.Date);
        using var persistence = owner.CreateLibraryPersistence();
        await persistence.SaveAsync(owner.ReadComponent("library.json")!, bytes);
        var root = JsonNode.Parse(bytes)!;
        root["games"]![0]!["sessions"]![count-1]!["futureField"] = 1;
        var error = await Assert.ThrowsAsync<NativeProfileException>(() => persistence.SaveAsync(bytes, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root)));
        Assert.Contains("UnknownLibraryField", error.Message);
        Assert.Equal(bytes, owner.ReadComponent("library.json"));
    }
    [Fact]
    public async Task NestedArrayLengthAndDuplicateFieldChecksRemainInForce()
    {
        using var fixture = new ProfileFixture(); using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, fixture.Root), new NativeLibraryCodec());
        var bytes = new NativeLibraryCodec().Encode(Program.Dataset(2), Program.Date);
        using var persistence = owner.CreateLibraryPersistence();
        await persistence.SaveAsync(owner.ReadComponent("library.json")!, bytes);
        var root = JsonNode.Parse(bytes)!; root["games"]![0]!["customTags"]!.AsArray().Add("tag 0");
        await Assert.ThrowsAnyAsync<Exception>(() => persistence.SaveAsync(bytes, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root)));
        var duplicate = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("\"notes\":", "\"notes\":\"duplicate\",\"notes\":"));
        await Assert.ThrowsAnyAsync<Exception>(() => persistence.SaveAsync(bytes, duplicate));
        Assert.Equal(bytes, owner.ReadComponent("library.json"));
    }
}
