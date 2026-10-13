using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class MaterialPreferencesTests
{
    [Theory]
    [InlineData("Auto")][InlineData("Advanced")][InlineData("Standard")][InlineData("Opaque")]
    public async Task PersistsAcrossReopenWithoutChangingOldPreferences(string mode)
    {
        using var f = new ProfileFixture(); byte[] old;
        using (var p = f.Open())
        {
            old = File.ReadAllBytes(Path.Combine(f.Root, "native-ui.json"));
            await using var store = new MaterialPreferencesStore(p.Configuration());
            Assert.Equal("Standard", (await store.LoadAsync()).Preference.Mode);
            await store.SaveAsync(new(mode));
        }
        using var reopened = f.Open(); // Existing v1 bootstrap/schema still accepts the extra companion.
        await using var next = new MaterialPreferencesStore(reopened.Configuration());
        Assert.Equal(mode, (await next.LoadAsync()).Preference.Mode);
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(f.Root, "native-ui.json")));
        Assert.Equal("grid", (await new IsolatedUiPreferencesStore(reopened.Configuration()).LoadAsync(default)).Layout);
    }
    [Theory]
    [InlineData("Auto")][InlineData("Advanced")]
    public void OrdinaryGlassChoiceDoesNotRequireTestQualification(string mode)
    {
        Assert.Equal(mode, MaterialActivationPolicy.Effective(mode, NativeProfileKind.Test, false));
        Assert.Equal(mode, MaterialActivationPolicy.Effective(mode, NativeProfileKind.Production, true));
        Assert.Equal(mode, MaterialActivationPolicy.Effective(mode, NativeProfileKind.Development, true));
        Assert.Equal(mode, MaterialActivationPolicy.Effective(mode, NativeProfileKind.Test, true));
    }
    [Theory]
    [InlineData("future-mode")][InlineData("corrupt")][InlineData("duplicate")]
    public async Task InvalidSettingFallsBackWithoutRewritingOnLoad(string fault)
    {
        using var f = new ProfileFixture(); using var p = f.Open();
        await using var store = new MaterialPreferencesStore(p.Configuration());
        await store.SaveAsync(new("Advanced")); var path = Path.Combine(f.Root, MaterialPreferencesStore.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!; json["Mode"] = "future-mode";
        File.WriteAllText(path, fault == "corrupt" ? "{" : fault == "duplicate" ? json.ToJsonString().Replace("{", "{\"Mode\":\"Advanced\",", StringComparison.Ordinal) : json.ToJsonString());
        var before = File.ReadAllBytes(path); var read = await store.LoadAsync();
        Assert.Equal("Standard", read.Preference.Mode); Assert.NotNull(read.Warning); Assert.Equal(before, File.ReadAllBytes(path));
        await store.SaveAsync(new("Opaque")); Assert.Equal("Opaque", (await store.LoadAsync()).Preference.Mode);
    }
    [Theory]
    [InlineData("version")][InlineData("identity")][InlineData("oversize")][InlineData("schema")]
    public async Task IncompatibleSettingIsNotOverwritten(string fault)
    {
        using var f = new ProfileFixture(); using var p = f.Open();
        await using var store = new MaterialPreferencesStore(p.Configuration()); await store.SaveAsync(new());
        var path = Path.Combine(f.Root, MaterialPreferencesStore.FileName); var json = JsonNode.Parse(File.ReadAllText(path))!;
        if (fault == "version") json["Version"] = 2;
        else if (fault == "schema") json["FutureSetting"] = true;
        else json["StoreId"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, fault == "oversize" ? new string('x', 4097) : json.ToJsonString()); var before = File.ReadAllBytes(path);
        Assert.True((await store.LoadAsync()).ReadOnly);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new("Auto"))); Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Theory]
    [InlineData(PersistenceStage.DuringTempWrite)][InlineData(PersistenceStage.BeforeReplace)]
    public async Task InterruptedWriteRetainsLastCommittedChoice(PersistenceStage point)
    {
        using var f = new ProfileFixture(); using var p = f.Open();
        await using var good = new MaterialPreferencesStore(p.Configuration()); await good.SaveAsync(new("Opaque"));
        await using var failed = new MaterialPreferencesStore(p.Configuration(), stage => stage == point ? throw new IOException("synthetic interruption") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => failed.SaveAsync(new("Advanced")));
        Assert.Equal("Opaque", (await good.LoadAsync()).Preference.Mode);
        Assert.Empty(Directory.GetFiles(f.Root, MaterialPreferencesStore.FileName + ".*.tmp"));
    }
    [Fact]
    public async Task AdmissionCancellationAndDisposedOwnerDoNotWrite()
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var store = new MaterialPreferencesStore(p.Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new("Auto"), new(true)));
        Assert.False(File.Exists(Path.Combine(f.Root, MaterialPreferencesStore.FileName)));
        await store.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(new("Auto")));
    }
    [Fact]
    public void LifecycleJournalIsBoundedAndDoesNotLogUserContent()
    {
        using var f = new ProfileFixture(); using var p = f.Open(); var log = new NativeLifecycleJournal(p);
        var path = Path.Combine(f.Root, "native-runtime.log");
        File.WriteAllText(path, new string('x', 65536));
        log.Write("Information"); Assert.True(new FileInfo(path).Length < 1024);
        var before = File.ReadAllBytes(path); log.Write("Information", "synthetic-secret\n");
        Assert.Equal(before, File.ReadAllBytes(path));
        log.Write("Critical", "IOException"); Assert.Contains("IOException", File.ReadAllText(path));
    }
}
