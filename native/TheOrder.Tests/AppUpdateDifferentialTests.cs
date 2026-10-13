using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using Xunit;

namespace TheOrder.Tests;

public sealed class AppUpdateDifferentialTests
{
    internal static byte[] Basis(int count = 2 * 1024 * 1024)
    { var bytes = new byte[count]; new Random(9271).NextBytes(bytes); bytes[0] = 77; bytes[1] = 90; return bytes; }
    internal static byte[] Changed(byte[] original)
    {
        // Insertion and modification move old block offsets while preserving most content.
        var bytes = new byte[original.Length + 12000]; original.AsSpan(0, 250000).CopyTo(bytes);
        new Random(82).NextBytes(bytes.AsSpan(250000, 12000)); original.AsSpan(250000).CopyTo(bytes.AsSpan(262000)); bytes[900000] ^= 0x51;
        return bytes;
    }
    internal static Task<AppUpdateBlockMap> Map(byte[] bytes, string version = "1.0.3") => AppUpdateBlocks.CreateAsync(new MemoryStream(bytes), version, default);
    internal static AppUpdateRelease Release(byte[] bytes, AppUpdateBlockMap map)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(map, AppUpdateBlocks.JsonOptions); var version = map.Version;
        return new(version, "差量测试", new($"https://github.com/Ph-Rank/The-Order/releases/download/v{version}/{AppUpdateIdentity.InstallerName(version)}"),
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new(new($"https://github.com/Ph-Rank/The-Order/releases/download/v{version}/{AppUpdateIdentity.BlockMapName(version)}"),
                encoded.Length, Convert.ToHexString(SHA256.HashData(encoded))));
    }
    internal sealed class ImmediateProgress(Action<AppUpdateTransfer> action) : IProgress<AppUpdateTransfer>
    { public void Report(AppUpdateTransfer value) => action(value); }
    [Fact]
    public async Task StableContentDefinedBoundariesAndFullHashSurviveOffsetChanges()
    {
        var basis = Basis(); var next = Changed(basis); var old = await Map(basis, "1.0.2"); var current = await Map(basis, "1.0.2");
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(current));
        var map = await Map(next, "1.2.0"); var plan = AppUpdateBlocks.Plan(old, map, 10000);
        Assert.True(plan.Worthwhile); Assert.True(plan.ReusedBytes > next.Length * .8); Assert.Equal(next.Length, plan.ReusedBytes + plan.DownloadBytes);
        Assert.Contains(plan.Operations, x => x.BaseOffset is not null && x.BaseOffset != x.TargetOffset);
        using var output = new MemoryStream(); long transferred = 0;
        await AppUpdateBlocks.ReconstructAsync(new MemoryStream(basis), output, plan, async (offset, count, dest, token) =>
        { transferred += count; await dest.WriteAsync(next.AsMemory((int)offset, count), token); }, new ImmediateProgress(x => Assert.True(x.Differential)), default);
        Assert.Equal(plan.DownloadBytes, transferred); Assert.Equal(next, output.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(next)), map.Sha256);
    }
    [Theory]
    [InlineData("gap")] [InlineData("overlap")] [InlineData("wrong-total")] [InlineData("empty")] [InlineData("null")] [InlineData("short")] [InlineData("hash")]
    [InlineData("schema")] [InlineData("algorithm")] [InlineData("other-version")] [InlineData("other-installer")] [InlineData("other-full-hash")]
    public async Task MalformedOrUnrelatedMapCannotAuthorizeReconstruction(string kind)
    {
        var bytes = Basis(); var map = await Map(bytes); var release = Release(bytes, map);
        var blocks = map.Blocks.ToArray();
        var invalid = kind switch
        {
            "gap" => map with { Blocks = [blocks[0] with { Offset = 1 }, .. blocks.Skip(1)] },
            "overlap" => map with { Blocks = [blocks[0], blocks[1] with { Offset = 0 }, .. blocks.Skip(2)] },
            "wrong-total" => map with { Bytes = map.Bytes + 1 },
            "empty" => map with { Blocks = [] }, "null" => map with { Blocks = null! },
            "short" => map with { Blocks = [blocks[0] with { Bytes = 1 }, .. blocks.Skip(1)] },
            "hash" => map with { Blocks = [blocks[0] with { Sha256 = "bad" }, .. blocks.Skip(1)] },
            "schema" => map with { Schema = 2 }, "algorithm" => map with { Algorithm = "instructions" },
            "other-version" => map with { Version = "1.0.4", Name = AppUpdateIdentity.InstallerName("1.0.4") },
            "other-installer" => map with { Name = "other.exe" },
            _ => map with { Sha256 = new string('0', 64) }
        };
        await Assert.ThrowsAsync<AppUpdateException>(() => Task.FromResult(AppUpdateBlocks.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid, AppUpdateBlocks.JsonOptions), release)));
    }
    [Fact]
    public async Task LittleReuseAndTooManyRequestsPreferOneFullDownload()
    {
        var bytes = Basis(); var other = new byte[bytes.Length]; new Random(818).NextBytes(other);
        Assert.False(AppUpdateBlocks.Plan(await Map(bytes, "1.0.2"), await Map(other), 10000).Worthwhile);
        var block = new AppUpdateBlock(0, AppUpdateBlocks.MinimumBlock, new string('1', 64));
        var basis = new AppUpdateBlockMap(1, AppUpdateBlocks.Algorithm, "1.0.2", AppUpdateIdentity.InstallerName("1.0.2"), block.Bytes, new string('2', 64), [block]);
        var next = basis with { Version = "1.0.3", Name = AppUpdateIdentity.InstallerName("1.0.3"), Bytes = 260 * block.Bytes,
            Blocks = Enumerable.Range(0, 260).Select(i => block with { Offset = i * (long)block.Bytes, Sha256 = i % 2 == 0 ? block.Sha256 : new string('3', 64) }).ToArray() };
        Assert.False(AppUpdateBlocks.Plan(basis, next, 0).Worthwhile);
    }
    [Fact]
    public async Task ReusedBlocksAreRecheckedAfterPlanningAndCancellationDoesNotDownload()
    {
        var basis = Basis(); var old = await Map(basis, "1.0.2"); var map = await Map(Changed(basis)); var plan = AppUpdateBlocks.Plan(old, map, 0);
        var reused = plan.Operations.First(x => x.BaseOffset is not null); basis[(int)reused.BaseOffset!.Value] ^= 1;
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<AppUpdateException>(() => AppUpdateBlocks.ReconstructAsync(new MemoryStream(basis), output, plan,
            (_, count, dest, token) => dest.WriteAsync(new byte[count], token).AsTask(), new ImmediateProgress(_ => { }), default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var calls = 0; output.SetLength(0); output.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => AppUpdateBlocks.ReconstructAsync(new MemoryStream(basis), output, plan,
            (_, _, _, _) => { calls++; return Task.CompletedTask; }, new ImmediateProgress(_ => { }), cancellation.Token));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task LatestReadsOnlyMetadataAndOptionalBadMapDoesNotBlockFullInstaller()
    {
        var bytes = Basis(); var map = await Map(bytes); var release = Release(bytes, map);
        foreach (var mapHash in new[] { "sha256:" + release.BlockMap!.Sha256, "bad" })
        {
            var metadata = JsonSerializer.Serialize(new { tag_name = "v1.0.3", draft = false, prerelease = false, body = "更新", assets = new[] {
                new { name = AppUpdateIdentity.InstallerName("1.0.3"), state = "uploaded", size = release.Bytes, browser_download_url = release.DownloadUri.AbsoluteUri, digest = "sha256:" + release.Sha256 },
                new { name = AppUpdateIdentity.BlockMapName("1.0.3"), state = "uploaded", size = release.BlockMap.Bytes, browser_download_url = release.BlockMap.DownloadUri.AbsoluteUri, digest = mapHash } } });
            using var handler = new AppUpdateTests.Handler((_, _) => AppUpdateTests.Response(AppUpdateTests.Json(metadata)));
            using var feed = new GitHubUpdateFeed(new HttpClient(handler)); var result = await feed.LatestAsync(default);
            Assert.NotNull(result); Assert.Equal(mapHash != "bad", result.BlockMap is not null); Assert.Single(handler.Requests);
        }
    }
    [Theory]
    [InlineData("valid")] [InlineData("tamper")] [InlineData("unrelated")]
    public async Task MapDownloadRequiresItsOwnDigestAndExactTargetIdentity(string mode)
    {
        var bytes = Basis(); var map = await Map(bytes); var release = Release(bytes, map);
        var json = JsonSerializer.SerializeToUtf8Bytes(mode == "unrelated" ? map with { Sha256 = new string('0', 64) } : map, AppUpdateBlocks.JsonOptions);
        if (mode == "tamper") json[0] ^= 1;
        if (mode == "unrelated") release = release with { BlockMap = release.BlockMap! with { Bytes = json.Length, Sha256 = Convert.ToHexString(SHA256.HashData(json)) } };
        using var handler = new AppUpdateTests.Handler((r, _) => { Assert.Equal(release.BlockMap!.DownloadUri, r.RequestUri); return AppUpdateTests.Response(new(HttpStatusCode.OK) { Content = new ByteArrayContent(json) }); });
        using var feed = new GitHubUpdateFeed(new HttpClient(handler));
        if (mode == "valid") Assert.Equal(map.Sha256, (await feed.ReadBlockMapAsync(release, default)).Sha256);
        else await Assert.ThrowsAsync<AppUpdateException>(() => feed.ReadBlockMapAsync(release, default));
    }
    [Theory]
    [InlineData("valid")] [InlineData("ignored")] [InlineData("wrong-range")] [InlineData("short")] [InlineData("extra")] [InlineData("foreign")]
    public async Task RangeDownloadChecksStatusBoundsAndAllowedRedirects(string mode)
    {
        var bytes = Basis(); var release = Release(bytes, await Map(bytes)); const int start = 70000, count = 20000;
        using var handler = new AppUpdateTests.Handler((r, _) =>
        {
            var requested = Assert.Single(r.Headers.Range!.Ranges); Assert.Equal(start, requested.From); Assert.Equal(start + count - 1, requested.To);
            if (r.RequestUri!.Host == "github.com") return AppUpdateTests.Response(new(HttpStatusCode.Redirect) { Headers = { Location = new(mode == "foreign" ? "https://other.test/file" : "https://release-assets.githubusercontent.com/file?token=public") } });
            var response = new HttpResponseMessage(mode == "ignored" ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes.AsSpan(start, mode == "short" ? count - 1 : mode == "extra" ? count + 1 : count).ToArray()) };
            response.Content.Headers.ContentRange = new(mode == "wrong-range" ? start + 1 : start, start + count - 1, bytes.Length);
            response.Content.Headers.ContentLength = count; return AppUpdateTests.Response(response);
        });
        using var feed = new GitHubUpdateFeed(new HttpClient(handler)); using var output = new MemoryStream();
        if (mode == "valid") { await feed.CopyRangeAsync(release, start, count, output, default); Assert.Equal(bytes.AsSpan(start, count).ToArray(), output.ToArray()); }
        else await Assert.ThrowsAsync<AppUpdateException>(() => feed.CopyRangeAsync(release, start, count, output, default));
    }
    [Theory]
    [InlineData(-1, 2)] [InlineData(0, 0)] [InlineData(5, 2)] [InlineData(long.MaxValue, 2)]
    public async Task InvalidRangesDoNotSendARequest(long start, int count)
    {
        using var handler = new AppUpdateTests.Handler((_, _) => throw new InvalidOperationException("Must not request")); using var feed = new GitHubUpdateFeed(new HttpClient(handler));
        await Assert.ThrowsAsync<AppUpdateException>(() => feed.CopyRangeAsync(AppUpdateTests.Release(), start, count, new MemoryStream(), default)); Assert.Empty(handler.Requests);
    }
}
