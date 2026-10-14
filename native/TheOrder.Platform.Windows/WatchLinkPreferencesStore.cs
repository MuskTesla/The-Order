using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Optional profile-bound companion. Required profile components and old UI
// settings are unchanged; the existing exclusive profile lease owns writes.
public sealed class WatchLinkPreferencesStore(NativeConfiguration configuration) : IWatchLinkPreferencesStore, IAsyncDisposable
{
    public const string FileName = "native-watch-links-v1.json";
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, WatchLinkSort Sort);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1); private bool disposed;
    private static WatchLinkPreferenceRead Decode(byte[] bytes, NativeProfileStore profile)
    {
        try
        {
            if (bytes.Length > 4096) throw new JsonException();
            using var doc = JsonDocument.Parse(bytes); var names = new HashSet<string>();
            if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Any(x => !names.Add(x.Name))) throw new JsonException();
            var item = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new JsonException();
            if (item.Version != 1 || item.ProfileId != profile.Manifest.ProfileId || item.StoreId != profile.Manifest.StoreId || !Enum.IsDefined(item.Sort)) throw new JsonException();
            return new(item.Sort);
        }
        catch (JsonException) { return new(Warning: "观看排序设置暂不可读取，原文件保留。", ReadOnly: true); }
    }
    private static byte[]? Read(string file)
    {
        try { using var stream = IsolatedLibrarySource.OpenChecked(file); if (stream.Length > 4096) return new byte[4097]; var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes; }
        catch (FileNotFoundException) { return null; }
    }
    public Task<WatchLinkPreferenceRead> LoadAsync(CancellationToken cancellation = default) => Run(profile =>
    {
        var bytes = Read(Path.Combine(profile.Root, FileName)); return Task.FromResult(bytes is null ? new WatchLinkPreferenceRead() : Decode(bytes, profile));
    }, cancellation);
    public Task SaveAsync(WatchLinkSort sort, CancellationToken cancellation = default) => Run(async profile =>
    {
        if (!Enum.IsDefined(sort)) throw new InvalidDataException("观看排序无效。");
        var file = Path.Combine(profile.Root, FileName); var previous = Read(file);
        if (previous is not null && Decode(previous, profile).ReadOnly) throw new InvalidDataException("原观看排序文件未被覆盖。");
        if (previous is not null) await WindowsAtomicFile.WriteAsync(file + ".bak", previous).ConfigureAwait(false);
        await WindowsAtomicFile.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, sort))).ConfigureAwait(false);
        return true;
    }, cancellation);
    private async Task<T> Run<T>(Func<NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("需要独立的 Native 资料身份。");
            return await Task.Run(async () =>
            {
                var root = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { profile.ValidateAccess(); return await action(profile).ConfigureAwait(false); }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
    public async ValueTask DisposeAsync() { await queue.WaitAsync().ConfigureAwait(false); try { disposed = true; } finally { queue.Release(); } }
}
