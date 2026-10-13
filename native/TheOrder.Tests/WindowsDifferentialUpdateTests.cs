using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class WindowsDifferentialUpdateTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Parent = Path.Combine(Path.GetTempPath(), "the-order-delta-" + Guid.NewGuid().ToString("N"));
        internal readonly NativeProfileStore Profile;
        internal Fixture() => Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Path.Combine(Parent, "profile")), new NativeLibraryCodec());
        public void Dispose() { Profile.Dispose(); if (Path.GetFullPath(Parent).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Parent).StartsWith("the-order-delta-", StringComparison.Ordinal)) Directory.Delete(Parent, true); }
    }
    private sealed class Feed(byte[] bytes, AppUpdateBlockMap map) : IAppUpdateFeed, IAppUpdateDifferentialFeed
    {
        internal int FullCalls, MapCalls, RangeCalls;
        internal bool BadMap, CorruptRange, CorruptFull;
        internal Action? MapAction;
        internal Func<CancellationToken, Task>? Wait;
        public Task<AppUpdateRelease?> LatestAsync(CancellationToken token) => Task.FromResult<AppUpdateRelease?>(AppUpdateDifferentialTests.Release(bytes, map));
        public async Task CopyInstallerAsync(AppUpdateRelease r, Stream dest, IProgress<AppUpdateTransfer> progress, CancellationToken token)
        { FullCalls++; var data = bytes.ToArray(); if (CorruptFull) data[0] ^= 1; await dest.WriteAsync(data, token); progress.Report(new(data.Length, data.Length)); }
        public Task<AppUpdateBlockMap> ReadBlockMapAsync(AppUpdateRelease r, CancellationToken token)
        { MapCalls++; MapAction?.Invoke(); if (BadMap) throw new AppUpdateException("Synthetic invalid map"); return Task.FromResult(map); }
        public async Task CopyRangeAsync(AppUpdateRelease r, long offset, int count, Stream dest, CancellationToken token)
        { RangeCalls++; if (Wait is not null) await Wait(token); var data = bytes.AsSpan((int)offset, count).ToArray(); if (CorruptRange) data[0] ^= 1; await dest.WriteAsync(data, token); }
    }
    [Fact]
    public async Task DifferentialUsesOnlyMissingBytesAndRetainsSecurityMarkerAndSyntheticLibrary()
    {
        using var fixture = new Fixture(); var old = AppUpdateDifferentialTests.Basis(); var bytes = AppUpdateDifferentialTests.Changed(old); var map = await AppUpdateDifferentialTests.Map(bytes);
        var store = new WindowsUpdatePackages(fixture.Profile.Configuration(), () => new MemoryStream(old), "1.0.2"); var feed = new Feed(bytes, map);
        var before = File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json")); var modes = new List<bool>();
        var result = await store.DownloadAsync(feed, AppUpdateDifferentialTests.Release(bytes, map), new AppUpdateDifferentialTests.ImmediateProgress(x => modes.Add(x.Differential)), default);
        Assert.Equal(0, feed.FullCalls); Assert.True(feed.RangeCalls > 0); Assert.True(result.ReusedBytes > bytes.Length * .8); Assert.True(result.DownloadedBytes < bytes.Length * .2);
        Assert.All(modes, Assert.True); Assert.Equal(bytes, File.ReadAllBytes(result.InstallerPath)); Assert.Contains("ZoneId=3", File.ReadAllText(result.InstallerPath + ":Zone.Identifier"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Profile.Root, "library.json")));
        Assert.False(new WindowsUpdateInstaller(fixture.Profile.Configuration(), store).Availability.CanInstall);
    }
    [Theory]
    [InlineData("no-cache")] [InlineData("bad-map")] [InlineData("bad-range")] [InlineData("changed-cache")] [InlineData("unsafe-cache")]
    public async Task UnsuitableDeltaFallsBackOnceAndNeverLeavesReconstructionTail(string mode)
    {
        using var fixture = new Fixture(); var old = AppUpdateDifferentialTests.Basis(); var bytes = AppUpdateDifferentialTests.Changed(old); var map = await AppUpdateDifferentialTests.Map(bytes);
        var feed = new Feed(bytes, map) { BadMap = mode == "bad-map", CorruptRange = mode == "bad-range" };
        if (mode == "changed-cache") feed.MapAction = () => Array.Fill<byte>(old, 0);
        var store = new WindowsUpdatePackages(fixture.Profile.Configuration(), () => mode switch {
            "no-cache" => null, "unsafe-cache" => throw new AppUpdateException("Linked cache"), _ => new MemoryStream(old) }, "1.0.2");
        var result = await store.DownloadAsync(feed, AppUpdateDifferentialTests.Release(bytes, map), new AppUpdateDifferentialTests.ImmediateProgress(_ => { }), default);
        Assert.Equal(1, feed.FullCalls); Assert.Equal(0, result.ReusedBytes); Assert.Equal(bytes, File.ReadAllBytes(result.InstallerPath));
        if (mode == "no-cache") Assert.Equal(0, feed.MapCalls);
    }
    [Fact]
    public async Task CancelingDeltaNeverStartsFallbackOrCommitsPartialInstaller()
    {
        using var fixture = new Fixture(); var old = AppUpdateDifferentialTests.Basis(); var bytes = AppUpdateDifferentialTests.Changed(old); var map = await AppUpdateDifferentialTests.Map(bytes);
        using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new Feed(bytes, map) { Wait = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var store = new WindowsUpdatePackages(fixture.Profile.Configuration(), () => new MemoryStream(old), "1.0.2");
        var task = store.DownloadAsync(feed, AppUpdateDifferentialTests.Release(bytes, map), new AppUpdateDifferentialTests.ImmediateProgress(_ => { }), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Assert.ThrowsAsync<TaskCanceledException>(() => task);
        Assert.Equal(0, feed.FullCalls); Assert.Empty(Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task CorruptDeltaAndFullFallbackCannotBecomeAnExecutable()
    {
        using var fixture = new Fixture(); var old = AppUpdateDifferentialTests.Basis(); var bytes = AppUpdateDifferentialTests.Changed(old); var map = await AppUpdateDifferentialTests.Map(bytes);
        var feed = new Feed(bytes, map) { CorruptRange = true, CorruptFull = true }; var store = new WindowsUpdatePackages(fixture.Profile.Configuration(), () => new MemoryStream(old), "1.0.2");
        await Assert.ThrowsAsync<AppUpdateException>(() => store.DownloadAsync(feed, AppUpdateDifferentialTests.Release(bytes, map), new AppUpdateDifferentialTests.ImmediateProgress(_ => { }), default));
        Assert.Equal(1, feed.FullCalls); Assert.Empty(Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories));
    }
}
