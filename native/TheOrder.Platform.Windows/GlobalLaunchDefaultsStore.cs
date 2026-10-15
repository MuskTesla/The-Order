using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Optional native component. Same profile lease, directory pins and atomic writer;
// no new manifest requirement, profile migration, or remote cache dependency.
public sealed class GlobalLaunchDefaultsStore(NativeConfiguration configuration, Func<PersistenceStage, Task>? probe = null)
    : IGlobalLaunchDefaultsStore, IAsyncDisposable
{
    public const string FileName = "native-global-launch-v1.json";
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, GlobalLaunchDefaults Defaults);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1);
    private bool disposed;
    private static GlobalLaunchDefaultsRead Decode(byte[] bytes, NativeProfileStore profile)
    {
        try
        {
            if (bytes.Length > 131072) throw new InvalidDataException();
            using var doc = JsonDocument.Parse(bytes);
            CheckDuplicates(doc.RootElement);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Version", out var v)
                || !v.TryGetInt32(out var version) || version != 1)
                return new(new(), "全局启动设置版本不受支持，原文件保留；继承设置的启动已暂停。", true);
            var e = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new JsonException();
            if (e.ProfileId != profile.Manifest.ProfileId || e.StoreId != profile.Manifest.StoreId)
                return new(new(), "全局启动设置不属于当前资料，原文件保留；继承设置的启动已暂停。", true);
            LaunchPolicy.Validate(e.Defaults);
            return new(e.Defaults);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        { return new(new(), "全局启动设置无法安全读取，原文件保留；继承设置的启动已暂停。", true); }
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException(); CheckDuplicates(p.Value); }
    }
    private static byte[]? Read(string path)
    {
        try { using var f = IsolatedLibrarySource.OpenChecked(path); if (f.Length > 131072) return new byte[131073]; var bytes = new byte[checked((int)f.Length)]; f.ReadExactly(bytes); return bytes; }
        catch (FileNotFoundException) { return null; }
    }
    public Task<GlobalLaunchDefaultsRead> LoadAsync(CancellationToken cancellation = default) => Run(p => {
        var bytes = Read(Path.Combine(p.Root, FileName)); return Task.FromResult(bytes is null ? new GlobalLaunchDefaultsRead(new()) : Decode(bytes, p));
    }, cancellation);
    public Task SaveAsync(GlobalLaunchDefaults value, CancellationToken cancellation = default)
    {
        LaunchPolicy.Validate(value);
        return Run(async p => {
            var path = Path.Combine(p.Root, FileName); var before = Read(path);
            if (before is not null) {
                var previous = Decode(before, p); if (previous.ReadOnly) throw new InvalidDataException(previous.Warning);
                await WindowsAtomicFile.WriteAsync(path + ".bak", before).ConfigureAwait(false);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, p.Manifest.ProfileId, p.Manifest.StoreId, value));
            await WindowsAtomicFile.WriteAsync(path, bytes, probe).ConfigureAwait(false); return true;
        }, cancellation);
    }
    private async Task<T> Run<T>(Func<NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("Native profile required");
            return await Task.Run(async () => {
                var root = NativeProfileAccess.Validate(configuration); var pins = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { profile.ValidateAccess(); return await action(profile).ConfigureAwait(false); }
                finally { foreach (var pin in pins) pin.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        } finally { queue.Release(); }
    }
    public async ValueTask DisposeAsync() { await queue.WaitAsync().ConfigureAwait(false); try { disposed = true; } finally { queue.Release(); } }
}
