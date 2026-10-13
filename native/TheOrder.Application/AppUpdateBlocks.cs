using System.Security.Cryptography;
using System.Text.Json;

namespace TheOrder.Application;

public sealed record AppUpdateBlock(long Offset, int Bytes, string Sha256);
public sealed record AppUpdateBlockMap(int Schema, string Algorithm, string Version, string Name, long Bytes, string Sha256, AppUpdateBlock[] Blocks);
public sealed record AppUpdateDeltaOperation(long TargetOffset, int Bytes, long? BaseOffset, string? Sha256);
public sealed record AppUpdateDeltaPlan(AppUpdateDeltaOperation[] Operations, long DownloadBytes, long ReusedBytes, bool Worthwhile);

// Content-defined boundaries survive insertions/deletions and compression-stream shifts.
// This format is versioned; publisher and downloader share the exact implementation.
public static class AppUpdateBlocks
{
    public const string Algorithm = "gear-v1-16k-64k-128k-sha256";
    public const int MinimumBlock = 16 * 1024, MaximumBlock = 128 * 1024, MaximumRange = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };
    private static readonly ulong[] Gear = CreateGear();
    private static ulong[] CreateGear()
    {
        var table = new ulong[256]; ulong seed = 0x5448454f52444552;
        for (int i = 0; i < table.Length; i++)
        {
            seed = unchecked(seed + 0x9e3779b97f4a7c15); var z = seed;
            z = unchecked((z ^ (z >> 30)) * 0xbf58476d1ce4e5b9);
            z = unchecked((z ^ (z >> 27)) * 0x94d049bb133111eb);
            table[i] = z ^ (z >> 31);
        }
        return table;
    }
    public static async Task<AppUpdateBlockMap> CreateAsync(Stream input, string version, CancellationToken cancellation)
    {
        if (!input.CanSeek || input.Position != 0 || input.Length is < 2 or > AppUpdateIdentity.MaximumInstallerBytes
            || !AppUpdateIdentity.TryVersion(version, out var parsed) || parsed.ToString(3) != version) throw Invalid();
        var blocks = new List<AppUpdateBlock>(); var buffer = new byte[65536]; var chunk = new byte[MaximumBlock];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int size = 0, read; long offset = 0; ulong rolling = 0;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            for (int i = 0; i < read; i++)
            {
                chunk[size++] = buffer[i]; rolling = unchecked((rolling << 1) + Gear[buffer[i]]);
                if (size < MinimumBlock || size < MaximumBlock && (rolling & 0xffff) != 0) continue;
                blocks.Add(new(offset, size, Convert.ToHexString(SHA256.HashData(chunk.AsSpan(0, size)))));
                offset += size; size = 0; rolling = 0;
            }
            cancellation.ThrowIfCancellationRequested();
            if (offset + size > AppUpdateIdentity.MaximumInstallerBytes) throw Invalid();
        }
        if (size > 0) { blocks.Add(new(offset, size, Convert.ToHexString(SHA256.HashData(chunk.AsSpan(0, size))))); offset += size; }
        var map = new AppUpdateBlockMap(1, Algorithm, version, AppUpdateIdentity.InstallerName(version), offset,
            Convert.ToHexString(hash.GetHashAndReset()), blocks.ToArray());
        Validate(map); return map;
    }
    public static AppUpdateBlockMap Parse(byte[] bytes, AppUpdateRelease release)
    {
        if (bytes.Length > AppUpdateIdentity.MaximumBlockMapBytes) throw Invalid();
        try
        {
            var map = JsonSerializer.Deserialize<AppUpdateBlockMap>(bytes, JsonOptions) ?? throw Invalid();
            Validate(map);
            if (map.Version != release.Version || map.Name != AppUpdateIdentity.InstallerName(release.Version)
                || map.Bytes != release.Bytes || !map.Sha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return map;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw Invalid(); }
    }
    public static void Validate(AppUpdateBlockMap map)
    {
        if (map.Schema != 1 || map.Algorithm != Algorithm || !AppUpdateIdentity.TryVersion(map.Version, out var version)
            || version.ToString(3) != map.Version || map.Name != AppUpdateIdentity.InstallerName(map.Version)
            || !AppUpdateIdentity.ValidHash(map.Sha256) || map.Bytes is < 2 or > AppUpdateIdentity.MaximumInstallerBytes
            || map.Blocks is not { Length: > 0 and <= 32769 }) throw Invalid();
        long end = 0;
        for (int i = 0; i < map.Blocks.Length; i++)
        {
            var block = map.Blocks[i];
            if (block is null || block.Offset != end || block.Bytes is <= 0 or > MaximumBlock || !AppUpdateIdentity.ValidHash(block.Sha256)
                || i < map.Blocks.Length - 1 && block.Bytes < MinimumBlock) throw Invalid();
            end += block.Bytes; if (end > map.Bytes) throw Invalid();
        }
        if (end != map.Bytes) throw Invalid();
    }
    public static AppUpdateDeltaPlan Plan(AppUpdateBlockMap current, AppUpdateBlockMap next, long mapBytes)
    {
        Validate(current); Validate(next);
        var lookup = current.Blocks.GroupBy(x => (x.Bytes, Hash: x.Sha256.ToUpperInvariant())).ToDictionary(x => x.Key, x => x.First().Offset);
        var operations = new List<AppUpdateDeltaOperation>(); long downloaded = 0, reused = 0; int ranges = 0;
        foreach (var block in next.Blocks)
        {
            if (lookup.TryGetValue((block.Bytes, block.Sha256.ToUpperInvariant()), out var baseOffset))
            { operations.Add(new(block.Offset, block.Bytes, baseOffset, block.Sha256)); reused += block.Bytes; }
            else
            {
                downloaded += block.Bytes;
                if (operations.LastOrDefault() is { BaseOffset: null } previous && previous.Bytes + block.Bytes <= MaximumRange)
                    operations[^1] = previous with { Bytes = previous.Bytes + block.Bytes };
                else { operations.Add(new(block.Offset, block.Bytes, null, null)); ranges++; }
            }
        }
        // Many tiny HTTP requests or little reuse are slower than a complete download.
        return new(operations.ToArray(), downloaded, reused, reused > 0 && ranges <= 128 && downloaded + mapBytes < next.Bytes * .8);
    }
    public static async Task ReconstructAsync(Stream current, Stream destination, AppUpdateDeltaPlan plan,
        Func<long, int, Stream, CancellationToken, Task> download, IProgress<AppUpdateTransfer> progress, CancellationToken cancellation)
    {
        long transferred = 0; var buffer = new byte[MaximumBlock];
        progress.Report(new(0, plan.DownloadBytes, true));
        foreach (var op in plan.Operations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (destination.Position != op.TargetOffset) throw Invalid();
            if (op.BaseOffset is { } position)
            {
                current.Position = position; await current.ReadExactlyAsync(buffer.AsMemory(0, op.Bytes), cancellation).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer.AsSpan(0, op.Bytes)), Convert.FromHexString(op.Sha256!))) throw Invalid();
                await destination.WriteAsync(buffer.AsMemory(0, op.Bytes), cancellation).ConfigureAwait(false);
            }
            else
            {
                await download(op.TargetOffset, op.Bytes, destination, cancellation).ConfigureAwait(false);
                if (destination.Position != op.TargetOffset + op.Bytes) throw Invalid();
                transferred += op.Bytes; progress.Report(new(transferred, plan.DownloadBytes, true));
            }
        }
        await destination.FlushAsync(cancellation).ConfigureAwait(false);
    }
    private static AppUpdateException Invalid() => new("差量更新信息或旧包校验失败，将改用完整安装包。");
}
