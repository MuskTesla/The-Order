using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class PersonalBannerTests
{
    [Fact]
    public async Task OriginalPersistsAfterSourceRemovalAndReopenWithoutChangingLibraryOrPreferences()
    {
        using var fixture = new ProfileFixture(); var encoded = await ImagePipelineTests.Png(1920, 1080);
        var path = Path.Combine(fixture.Parent, "personal.png"); File.WriteAllBytes(path, encoded);
        string? key; byte[] library, preferences;
        using (var profile = fixture.Open())
        {
            library = File.ReadAllBytes(Path.Combine(profile.Root, "library.json"));
            preferences = File.ReadAllBytes(Path.Combine(profile.Root, "native-ui.json"));
            await using var store = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec());
            key = (await store.SaveAsync("game-a", path)).Key;
            Assert.Equal(encoded, await store.ReadAsync(key!, default));
            Assert.Equal(key, await store.GetAsync("game-a")); Assert.Null(await store.GetAsync("game-b"));
        }
        File.Delete(path);
        using var reopened = fixture.Open();
        await using var next = new IsolatedPersonalBannerStore(reopened.Configuration(), new WindowsRasterCodec());
        Assert.Equal(key, await next.GetAsync("game-a")); Assert.Equal(encoded, await next.ReadAsync(key!, default));
        Assert.Equal(library, File.ReadAllBytes(Path.Combine(reopened.Root, "library.json")));
        Assert.Equal(preferences, File.ReadAllBytes(Path.Combine(reopened.Root, "native-ui.json")));
        await next.SaveAsync("game-a", null); Assert.Null(await next.GetAsync("game-a"));
        Assert.Equal(encoded, await next.ReadAsync(key!, default));
    }
    [Fact]
    public async Task InvalidImageAndCancelledSelectionRetainPriorArtwork()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open();
        var path = Path.Combine(fixture.Parent, "original.png"); File.WriteAllBytes(path, await ImagePipelineTests.Png(80, 45));
        await using var store = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec());
        var prior = (await store.SaveAsync("game", path)).Key;
        var bad = Path.Combine(fixture.Parent, "bad.png"); File.WriteAllBytes(bad, [1, 2, 3, 4]);
        Assert.False((await PersonalBannerClient.SaveAsync(store, "game", bad, default)).IsCommitted);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Equal(ClientOutcome.Cancelled, (await PersonalBannerClient.SaveAsync(store, "game", null, stop.Token)).Outcome);
        Assert.Equal(prior, await store.GetAsync("game")); Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task FailedAtomicSelectionRetainsPriorAndAdmittedCancellationFinishesCommit()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open();
        var path = Path.Combine(fixture.Parent, "original.png"); File.WriteAllBytes(path, await ImagePipelineTests.Png(80, 45));
        await using var initial = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec());
        var prior = (await initial.SaveAsync("game", path)).Key;
        await using var failing = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec(),
            stage => stage == PersistenceStage.BeforeReplace ? Task.FromException(new IOException("synthetic")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync("game", null));
        Assert.Equal(prior, await initial.GetAsync("game"));
        using var stop = new CancellationTokenSource();
        await using var cancelling = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec(),
            stage => { if (stage == PersistenceStage.BeforeReplace) stop.Cancel(); return Task.CompletedTask; });
        await cancelling.SaveAsync("game", null, stop.Token); Assert.Null(await initial.GetAsync("game"));
    }
    [Theory]
    [InlineData("Version")][InlineData("StoreId")][InlineData("ImageKey")]
    public async Task UnsupportedOrForeignSelectionIsPreserved(string field)
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open();
        await using var store = new IsolatedPersonalBannerStore(profile.Configuration(), new WindowsRasterCodec());
        await store.SaveAsync("game", null);
        var file = Path.Combine(profile.Root, IsolatedPersonalBannerStore.DirectoryName, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("game"))) + ".json");
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json[field] = field == "Version" ? JsonValue.Create(2) : JsonValue.Create(field == "StoreId" ? Guid.NewGuid().ToString() : "../outside");
        File.WriteAllText(file, json.ToJsonString()); var before = File.ReadAllBytes(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("game", null));
        Assert.Equal(before, File.ReadAllBytes(file));
    }
    [Fact]
    public async Task PreparedOriginalResolutionIsReusedFromMemoryAndDisk()
    {
        using var fixture = new ProfileFixture(); using var profile = fixture.Open();
        var encoded = await ImagePipelineTests.Png(1920, 1080); var key = new string('A', 64);
        var disk = new IsolatedThumbnailCache(profile.Configuration());
        var options = new ImageOptions(MaximumEdge: ArtworkSizing.MaximumEdge);
        await using (var pipeline = new ImagePipeline(new Source(encoded), new WindowsRasterCodec(), disk, options))
        {
            var full = await pipeline.LoadAsync(key, ArtworkSizing.MaximumEdge, default);
            Assert.Equal(1920, full.Pixels!.Width); Assert.Equal(1080, full.Pixels.Height);
            var smaller = await pipeline.LoadAsync(key, 1600, default);
            Assert.Same(full.Pixels, smaller.Pixels); Assert.Equal(1, pipeline.Metrics.Loads);
        }
        await using var reopened = new ImagePipeline(new Source([1, 2, 3, 4]), new WindowsRasterCodec(), disk, options);
        var cached = await reopened.LoadAsync(key, 1600, default);
        Assert.Equal(1920, cached.Pixels!.Width); Assert.Equal(0, reopened.Metrics.Loads);
    }
    private sealed class Source(byte[] encoded) : IImageSourceProvider
    { public Task<byte[]?> ReadAsync(string key, CancellationToken cancellation) => Task.FromResult<byte[]?>(encoded); }
}
