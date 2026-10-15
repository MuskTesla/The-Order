using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Public display data and user-confirmed edition choices live outside library.json. The existing
// profile identity/path pins/atomic writer protect this disposable cache exactly like relations.
public sealed class IsolatedMusicBrainzCache(NativeConfiguration configuration,
    Func<PersistenceStage, Task>? persistenceProbe = null) : IMusicBrainzCache, IMediaIdentityDecisionOwner
{
    public IMediaIdentityDecisions IdentityDecisions { get; } = new IsolatedMediaIdentityDecisions(configuration, persistenceProbe);
    public const string DirectoryName = "native-musicbrainz-v1";
    private const int MaximumFiles = 128, MaximumPayload = 4 * 1024 * 1024, MaximumEnvelope = 6 * 1024 * 1024;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim serial = new(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, string Key, string Hash, byte[] Payload);
    private static string FileKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public async Task<MusicBrainzCacheValue?> ReadAsync(string key, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (key is null || key.Length > 100) return null;
        try { return await Run((root, profile) => Task.FromResult(ReadVerified(Path.Combine(root, FileKey(key) + ".json"), profile, key)), false, cancellation).ConfigureAwait(false); }
        catch (Exception e) when (Failure(e)) { return null; }
    }
    public async Task WriteAsync(MusicBrainzCacheValue value, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!MusicBrainzValidation.Cache(value)) throw new InvalidDataException("音乐缓存格式无效。");
        if (value.Binding is null && IdentityDecisions is IsolatedMediaIdentityDecisions userData)
            await userData.ArchiveLegacyAsync(MediaIdentityPurpose.MusicRelease, cancellation).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumPayload) throw new IOException("音乐缓存超过大小限制。");
        try
        {
            var stored = await Run(async (root, profile) =>
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId,
                    value.Key, Convert.ToHexString(SHA256.HashData(payload)), payload), JsonOptions);
                if (bytes.Length > MaximumEnvelope) return false;
                var target = Path.Combine(root, FileKey(value.Key) + ".json");
                var files = new DirectoryInfo(root).EnumerateFiles().Take(MaximumFiles + 1).ToArray();
                if (files.Length > MaximumFiles) return false;
                var prior = files.SingleOrDefault(f => f.FullName.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (prior is not null && ReadVerified(target, profile, value.Key) is null) return false;
                long total = files.Sum(f => f.Length) - (prior?.Length ?? 0) + bytes.Length;
                int count = files.Length + (prior is null ? 1 : 0); var remove = new List<string>();
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= MaximumBytes && count <= MaximumFiles) break;
                    // Confirmed choices are tiny and should outlive the metadata cache. Unknown files are preserved.
                    if (file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase) || ReadVerified(file.FullName, profile) is not { } verified
                        || value.Binding is not null && verified.Binding is not null) continue;
                    remove.Add(file.FullName); total -= file.Length; count--;
                }
                if (total > MaximumBytes || count > MaximumFiles) return false;
                cancellation.ThrowIfCancellationRequested();
                await WindowsAtomicFile.WriteAsync(target, bytes, async stage =>
                {
                    if (persistenceProbe is not null) await persistenceProbe(stage).ConfigureAwait(false);
                    if (stage is PersistenceStage.BeforeTempWrite or PersistenceStage.BeforeReplace) cancellation.ThrowIfCancellationRequested();
                    if (stage != PersistenceStage.BeforeReplace) return;
                    if (File.Exists(target) && ReadVerified(target, profile, value.Key) is null) throw new IOException("音乐缓存身份已改变，保留原文件。");
                    foreach (var path in remove)
                    { if (ReadVerified(path, profile) is not { } verified || value.Binding is not null && verified.Binding is not null) throw new IOException("音乐缓存维护条件已改变。"); File.Delete(path); }
                }).ConfigureAwait(false);
                return true;
            }, true, cancellation).ConfigureAwait(false);
            if (!stored) throw new IOException("音乐缓存不可写，未保存此发行版选择。");
        }
        catch (Exception e) when (e is NativeProfileException or DataIsolationException)
        { throw new IOException("音乐缓存资料路径不可用。", e); }
    }
    internal static MusicBrainzCacheValue? ReadVerified(string path, NativeProfileStore profile, string? expected = null)
    {
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length is < 1 or > MaximumEnvelope) return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(file, JsonOptions);
            if (envelope is null || envelope.Version != 1 || envelope.ProfileId != profile.Manifest.ProfileId || envelope.StoreId != profile.Manifest.StoreId
                || envelope.Key is null || envelope.Key.Length > 100 || expected is not null && envelope.Key != expected
                || Path.GetFileName(path) != FileKey(envelope.Key) + ".json" || envelope.Payload is null || envelope.Payload.Length is < 1 or > MaximumPayload
                || envelope.Hash != Convert.ToHexString(SHA256.HashData(envelope.Payload))) return null;
            var value = JsonSerializer.Deserialize<MusicBrainzCacheValue>(envelope.Payload, JsonOptions);
            return value?.Key == envelope.Key && MusicBrainzValidation.Cache(value) ? value : null;
        }
        catch (Exception e) when (Failure(e)) { return null; }
    }
    private async Task<T?> Run<T>(Func<string, NativeProfileStore, Task<T>> action, bool create, CancellationToken cancellation)
    {
        if (configuration.Profile is not NativeProfileStore profile) return default;
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var parent = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(parent);
                try
                {
                    NativeProfileAccess.Validate(configuration); var root = Path.Combine(parent, DirectoryName); NativeProfilePaths.CheckChain(root);
                    if (!create && !Directory.Exists(root)) return default;
                    if (create) Directory.CreateDirectory(root);
                    var pinned = IsolatedLibraryPersistence.PinDirectoryChain(root);
                    try { NativeProfilePaths.CheckChain(root); cancellation.ThrowIfCancellationRequested(); return await action(root, profile).ConfigureAwait(false); }
                    finally { foreach (var handle in pinned) handle.Dispose(); }
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    private static bool Failure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or NativeProfileException or DataIsolationException or ArgumentException;
}
