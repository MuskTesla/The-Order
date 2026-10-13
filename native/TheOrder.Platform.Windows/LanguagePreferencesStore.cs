using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Optional companion: never changes native-ui v1, required capabilities, writer
// version or bootstrap. Older native binaries ignore this file. The profile's
// existing exclusive writer lease owns it; adapters neither acquire nor release it.
public sealed class LanguagePreferencesStore(NativeConfiguration configuration,
    Func<PersistenceStage, Task>? probe = null) : ILanguagePreferencesStore, IAsyncDisposable
{
    public const string FileName = "native-language-v1.json";
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, string Language);
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1);
    private bool disposed;

    private static LanguagePreferenceRead Decode(byte[] bytes, NativeProfileStore profile)
    {
        if (bytes.Length > 4096) return new(new(), "语言设置文件过大，已跟随系统；原文件保留。", true);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw new JsonException();
            if (!document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number) || number != 1)
                return new(new(), "语言设置版本不受支持，已跟随系统；原文件保留。", true);
            if (names.Any(name => name is not ("Version" or "ProfileId" or "StoreId" or "Language")))
                return new(new(), "语言设置包含不受支持的字段，已跟随系统；原文件保留。", true);
            var value = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new JsonException();
            if (value.ProfileId != profile.Manifest.ProfileId || value.StoreId != profile.Manifest.StoreId)
                return new(new(), "语言设置不属于当前资料，已跟随系统；原文件保留。", true);
            if (!UiText.ValidChoice(value.Language)) return new(new(), "语言设置包含不受支持的语言，已跟随系统；原文件保留。", true);
            return new(new(value.Language));
        }
        catch (JsonException) { return new(new(), "语言设置格式损坏，已跟随系统；可重新选择。"); }
    }
    private static byte[]? Read(string path)
    {
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length > 4096) return new byte[4097];
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }
    public Task<LanguagePreferenceRead> LoadAsync(CancellationToken cancellation = default) => Run(async profile =>
    {
        var bytes = Read(Path.Combine(profile.Root, FileName));
        await Task.CompletedTask;
        return bytes is null ? new(new()) : Decode(bytes, profile);
    }, cancellation);

    public Task SaveAsync(LanguagePreference preference, CancellationToken cancellation = default)
    {
        if (!UiText.ValidChoice(preference.Language)) throw new ArgumentException("Unsupported UI language");
        return Run(async profile =>
        {
            var target = Path.Combine(profile.Root, FileName);
            var previous = Read(target);
            if (previous is not null)
            {
                var read = Decode(previous, profile);
                if (read.ReadOnly) throw new InvalidDataException(read.Warning);
                // Only a valid old value becomes last-good. Corruption is retained
                // until an explicit user selection replaces the optional main.
                if (read.Warning is null)
                    await WindowsAtomicFile.WriteAsync(target + ".bak", previous).ConfigureAwait(false);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, preference.Language));
            // Once admitted, finish the atomic commit without cancellation.
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
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("Native profile required for language preferences");
            return await Task.Run(async () =>
            {
                var root = NativeProfileAccess.Validate(configuration);
                var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { profile.ValidateAccess(); return await action(profile).ConfigureAwait(false); }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await queue.WaitAsync().ConfigureAwait(false);
        try { disposed = true; } finally { queue.Release(); }
        // Do not dispose the borrowed profile lease or a semaphore with queued callers.
    }
}
