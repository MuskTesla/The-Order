using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Disposable, anonymous public relations. This directory is not a library component
// and can never supply identification evidence or modify the user's library.
public sealed partial class IsolatedBangumiRelationsCache(NativeConfiguration configuration,
    Func<PersistenceStage, Task>? persistenceProbe = null) : IBangumiRelationsCache
{
    public const string DirectoryName = "native-bangumi-relations-v1";
    public const int MaximumFiles = 128, MaximumPayloadBytes = 1024 * 1024;
    public const long MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumEnvelopeBytes = 1500000;
    private readonly SemaphoreSlim serial = new(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private sealed record StoredIdentity(string LocalGameId, string NextMoeId, string SubjectId, string Fingerprint)
    {
        public string AccessScope { get; init; } = "anonymous";
        public BangumiRelationIdentity Current() => new(LocalGameId, "cache-validation", NextMoeId, SubjectId, Fingerprint) { AccessScope = AccessScope };
        public static StoredIdentity From(BangumiRelationIdentity value) => new(value.LocalGameId, value.NextMoeId, value.SubjectId, value.Fingerprint) { AccessScope = value.AccessScope };
    }
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, StoredIdentity Identity, string Hash, byte[] Payload);
    private sealed record Verified(Envelope Envelope, BangumiRelationsSnapshot Snapshot);

    public async Task<BangumiRelationsSnapshot?> ReadAsync(BangumiRelationIdentity identity, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiRelationsService.ValidIdentity(identity)) return null;
        try
        {
            return await Run((root, profile) => Task.FromResult(
                ReadVerified(Path.Combine(root, identity.CacheKey + ".json"), profile) is { } found
                    && found.Envelope.Identity == StoredIdentity.From(identity)
                    && BangumiRelationsService.Valid(identity, found.Snapshot) ? found.Snapshot : null), false, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (CacheFailure(e)) { return null; }
    }

    public async Task WriteAsync(BangumiRelationIdentity identity, BangumiRelationsSnapshot snapshot, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiRelationsService.Valid(identity, snapshot)) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (payload.Length > MaximumPayloadBytes) return;
        try
        {
            await Run(async (root, profile) =>
            {
                var envelope = new Envelope(BangumiRelationsService.SchemaVersion, profile.Manifest.ProfileId,
                    profile.Manifest.StoreId, StoredIdentity.From(identity), Convert.ToHexString(SHA256.HashData(payload)), payload);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
                if (bytes.Length > MaximumEnvelopeBytes) return false;
                string target = Path.Combine(root, identity.CacheKey + ".json");
                // Inspect at most one more than the bound. Unknown or damaged files are
                // counted but never reclaimed, and a flooded directory is not swept.
                var files = new DirectoryInfo(root).EnumerateFiles().Take(MaximumFiles + 1).ToArray();
                if (files.Length > MaximumFiles) return false;
                var prior = files.SingleOrDefault(f => f.FullName.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (prior is not null && ReadVerified(target, profile) is null) return false;
                long total = files.Sum(f => f.Length) - (prior?.Length ?? 0) + bytes.Length;
                int count = files.Length + (prior is null ? 1 : 0);
                var remove = new List<string>();
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= MaximumBytes && count <= MaximumFiles) break;
                    if (file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase) || ReadVerified(file.FullName, profile) is null) continue;
                    remove.Add(file.FullName); total -= file.Length; count--;
                }
                if (total > MaximumBytes || count > MaximumFiles) return false;
                cancellation.ThrowIfCancellationRequested();
                await WindowsAtomicFile.WriteAsync(target, bytes, async stage =>
                {
                    if (persistenceProbe is not null) await persistenceProbe(stage).ConfigureAwait(false);
                    if (stage is PersistenceStage.BeforeTempWrite or PersistenceStage.BeforeReplace)
                        cancellation.ThrowIfCancellationRequested();
                    if (stage != PersistenceStage.BeforeReplace) return;
                    // Recheck before any deletion or replacement; foreign/future/invalid
                    // envelopes remain evidence even when they occupy a known hash name.
                    if (File.Exists(target) && ReadVerified(target, profile) is null)
                        throw new IOException("关联缓存身份已改变，已保留原文件。");
                    foreach (var path in remove)
                    {
                        if (ReadVerified(path, profile) is null) throw new IOException("关联缓存维护条件已改变。");
                        File.Delete(path);
                    }
                }).ConfigureAwait(false);
                // Once the namespace replacement starts, the complete new value wins,
                // even if cancellation arrives immediately afterwards.
                return true;
            }, true, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NativeProfileException or DataIsolationException)
        { throw new IOException("关联缓存资料路径不可用。", e); }
    }

    private static Verified? ReadVerified(string path, NativeProfileStore profile)
    {
        if (!KeyPattern().IsMatch(Path.GetFileNameWithoutExtension(path)) || Path.GetExtension(path) != ".json") return null;
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length is < 1 or > MaximumEnvelopeBytes) return null;
            var value = JsonSerializer.Deserialize<Envelope>(file, JsonOptions);
            if (value is null || value.Version != BangumiRelationsService.SchemaVersion || value.ProfileId != profile.Manifest.ProfileId
                || value.StoreId != profile.Manifest.StoreId || value.Identity is null || value.Payload is null
                || value.Identity.LocalGameId is null || value.Identity.NextMoeId is null || value.Identity.SubjectId is null || value.Identity.Fingerprint is null
                || value.Payload.Length is < 1 or > MaximumPayloadBytes || value.Hash is null
                || Convert.ToHexString(SHA256.HashData(value.Payload)) != value.Hash) return null;
            var identity = value.Identity.Current();
            if (!BangumiRelationsService.ValidIdentity(identity) || identity.CacheKey != Path.GetFileNameWithoutExtension(path)) return null;
            var snapshot = JsonSerializer.Deserialize<BangumiRelationsSnapshot>(value.Payload, JsonOptions);
            return BangumiRelationsService.Valid(identity, snapshot) ? new(value, snapshot!) : null;
        }
        catch (Exception e) when (CacheFailure(e)) { return null; }
    }

    private async Task<T?> Run<T>(Func<string, NativeProfileStore, Task<T>> action, bool create, CancellationToken cancellation)
    {
        // A path-only synthetic configuration has no stable profile/store identity.
        if (configuration.Profile is not NativeProfileStore profile) return default;
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                string parent = NativeProfileAccess.Validate(configuration);
                var held = IsolatedLibraryPersistence.PinDirectoryChain(parent);
                try
                {
                    NativeProfileAccess.Validate(configuration);
                    string root = Path.Combine(parent, DirectoryName);
                    NativeProfilePaths.CheckChain(root);
                    if (!create && !Directory.Exists(root)) return default;
                    if (create) Directory.CreateDirectory(root);
                    var cacheHeld = IsolatedLibraryPersistence.PinDirectoryChain(root);
                    try
                    {
                        NativeProfilePaths.CheckChain(root);
                        cancellation.ThrowIfCancellationRequested();
                        return await action(root, profile).ConfigureAwait(false);
                    }
                    finally { foreach (var handle in cacheHeld) handle.Dispose(); }
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    private static bool CacheFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or NativeProfileException or DataIsolationException or ArgumentException;
    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
}
