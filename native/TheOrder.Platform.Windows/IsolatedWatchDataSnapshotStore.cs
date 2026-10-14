using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Public, disposable dataset cache only; the existing profile lease/path pins
// protect atomic writes. Library, account tokens and profile schemas are untouched.
public sealed class IsolatedWatchDataSnapshotStore(NativeConfiguration configuration) : IBangumiWatchSnapshotStore, IAsyncDisposable
{
    public const string FileName = "native-bangumi-watch-data-v1.json";
    private const int MaximumBytes = 12 * 1024 * 1024;
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, BangumiWatchSnapshot Snapshot, string Hash);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1); private bool disposed;
    private static byte[]? Read(string path)
    {
        try { using var stream = IsolatedLibrarySource.OpenChecked(path); if (stream.Length is < 1 or > MaximumBytes) throw new InvalidDataException(); var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes; }
        catch (FileNotFoundException) { return null; }
    }
    private static BangumiWatchSnapshot Decode(byte[] bytes, NativeProfileStore profile)
    {
        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(x => x.Name).Distinct().Count() != root.EnumerateObject().Count()) throw new InvalidDataException();
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new InvalidDataException();
        if (envelope.Version != 1 || envelope.ProfileId != profile.Manifest.ProfileId || envelope.StoreId != profile.Manifest.StoreId || envelope.Snapshot is null
            || envelope.Hash != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Snapshot, Options)))) throw new InvalidDataException("放送数据缓存身份不一致。");
        _ = new BangumiWatchDataIndex(envelope.Snapshot); return envelope.Snapshot;
    }
    public Task<BangumiWatchSnapshot?> ReadAsync(CancellationToken cancellation) => Run(profile =>
    {
        var bytes = Read(Path.Combine(profile.Root, FileName)); return Task.FromResult(bytes is null ? null : Decode(bytes, profile));
    }, cancellation);
    public Task WriteAsync(BangumiWatchSnapshot value, CancellationToken cancellation) => Run(async profile =>
    {
        _ = new BangumiWatchDataIndex(value); var path = Path.Combine(profile.Root, FileName); var old = Read(path);
        if (old is not null) _ = Decode(old, profile); // Unknown or foreign caches are preserved, never overwritten.
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, value, Convert.ToHexString(SHA256.HashData(payload))), Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("放送数据缓存超过大小限制。");
        cancellation.ThrowIfCancellationRequested(); await WindowsAtomicFile.WriteAsync(path, bytes).ConfigureAwait(false); return true;
    }, cancellation);
    private async Task<T> Run<T>(Func<NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("需要 Native 资料身份。");
            return await Task.Run(async () =>
            {
                var root = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { profile.ValidateAccess(); return await action(profile).ConfigureAwait(false); }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
    public async ValueTask DisposeAsync() { await queue.WaitAsync().ConfigureAwait(false); try { disposed = true; } finally { queue.Release(); } }
}
