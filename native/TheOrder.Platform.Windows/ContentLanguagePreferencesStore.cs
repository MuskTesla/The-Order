using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Optional native companion using the same profile lease, path pins and atomic
// writer as other settings. Does not alter native-language-v1, library or decisions.
public sealed class ContentLanguagePreferencesStore(NativeConfiguration configuration, Func<PersistenceStage, Task>? probe = null)
    : IContentLanguagePreferencesStore, IAsyncDisposable
{
    public const string FileName = "native-content-language-v1.json";
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, string Preferred, string Fallback);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1);
    private bool disposed;
    private static ContentLanguagePreferenceRead Decode(byte[] bytes, NativeProfileStore profile)
    {
        if (bytes.Length > 4096) return new(new(), "资料语言偏好文件过大，原文件保留。", true);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in document.RootElement.EnumerateObject()) if (!names.Add(p.Name)) throw new JsonException();
            if (!document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || names.Any(x => x is not ("Version" or "ProfileId" or "StoreId" or "Preferred" or "Fallback")))
                return new(new(), "资料语言偏好版本不受支持，原文件保留。", true);
            var value = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new JsonException();
            if (value.ProfileId != profile.Manifest.ProfileId || value.StoreId != profile.Manifest.StoreId)
                return new(new(), "资料语言偏好不属于当前资料，原文件保留。", true);
            var preference = new ContentLanguagePreference(value.Preferred, value.Fallback);
            if (!ContentLanguage.Valid(preference)) return new(new(), "资料语言选项不受支持，原文件保留。", true);
            return new(preference);
        }
        catch (JsonException) { return new(new(), "资料语言偏好格式损坏，仍使用默认选项；可重新选择。"); }
    }
    private static byte[]? Read(string path)
    {
        try { using var f = IsolatedLibrarySource.OpenChecked(path); if (f.Length > 4096) return new byte[4097]; var b = new byte[checked((int)f.Length)]; f.ReadExactly(b); return b; }
        catch (FileNotFoundException) { return null; }
    }
    public Task<ContentLanguagePreferenceRead> LoadAsync(CancellationToken cancellation = default) => Run(async p =>
    { var b = Read(Path.Combine(p.Root, FileName)); await Task.CompletedTask; return b is null ? new(new()) : Decode(b, p); }, cancellation);
    public Task SaveAsync(ContentLanguagePreference value, CancellationToken cancellation = default)
    {
        if (!ContentLanguage.Valid(value)) throw new ArgumentException("Unsupported content language");
        return Run(async p =>
        {
            var target = Path.Combine(p.Root, FileName); var previous = Read(target);
            if (previous is not null)
            {
                var read = Decode(previous, p); if (read.ReadOnly) throw new InvalidDataException(read.Warning);
                if (read.Warning is null) await WindowsAtomicFile.WriteAsync(target + ".bak", previous).ConfigureAwait(false);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, p.Manifest.ProfileId, p.Manifest.StoreId, value.Preferred, value.Fallback));
            await WindowsAtomicFile.WriteAsync(target, bytes, probe).ConfigureAwait(false);
            return true;
        }, cancellation);
    }
    private async Task<T> Run<T>(Func<NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("Native profile required");
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
