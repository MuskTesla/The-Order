using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Public bibliographic data is disposable. Confirmed publication mappings are
// user choices in a different directory and never participate in cache eviction.
public sealed class IsolatedBookBibliographyStore(NativeConfiguration configuration,
    Func<PersistenceStage, Task>? persistenceProbe = null) : IBookBibliographyCache, IBookPublicationBindings
{
    public const string CacheDirectoryName = "native-book-bibliography-v1";
    public const string BindingsDirectoryName = "native-book-publications-v1";
    public const int MaximumFiles = 128, MaximumFileBytes = 1024 * 1024, MaximumBindings = 64;
    public const long MaximumBytes = 32L * 1024 * 1024;
    private const string CacheKind = "bibliography", BindingKind = "publication-bindings";
    private static readonly ConditionalWeakTable<NativeProfileStore, SemaphoreSlim> Gates = new();
    private readonly SemaphoreSlim serial = configuration.Profile is NativeProfileStore store
        ? Gates.GetValue(store, _ => new(1, 1)) : new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 32 };
    private sealed record Envelope(int Version, string Kind, Guid ProfileId, Guid StoreId, string Key, string Hash, byte[] Payload);
    private sealed record BindingDocument(BookIdentity Source, BookPublicationBinding[] Bindings);
    private static string FileKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    private static string BindingKey(BookIdentity source) => "source:" + source.Key;
    private static bool SourceValid(BookIdentity? source) => source is { IsValid: true } && source.Provider is "Bangumi" or "AniList";
    private static bool CacheKeyValid(string? key) => key is not null && (key.StartsWith("search:", StringComparison.Ordinal)
        && key.Length == 71 && key[7..].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')
        || key.StartsWith("publication:", StringComparison.Ordinal) && BookValidation.NdlId(key[12..]));
    private static bool CacheValid(BookCacheValue? value) => value is not null && BookValidation.Cache(value) && CacheKeyValid(value.Key)
        && (value.Rejected || (value.Search is not null ? value.Key.StartsWith("search:", StringComparison.Ordinal)
            : value.Key == "publication:" + value.Publication!.Id));
    private static bool BindingsValid(BookIdentity source, BookPublicationBinding[]? bindings) => SourceValid(source)
        && bindings is { Length: <= MaximumBindings } && bindings.All(b => b is { SourceIdentity: not null }
            && b.SourceIdentity == source && BookValidation.Binding(b))
        && bindings.Select(b => b.PublicationId).Distinct(StringComparer.Ordinal).Count() == bindings.Length;

    public async Task<BookCacheValue?> ReadAsync(string key, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!CacheKeyValid(key)) return null;
        try
        {
            return await Run(CacheDirectoryName, false, (BookCacheValue?)null,
                (root, profile) => Task.FromResult(ReadCache(Path.Combine(root, FileKey(key)), profile, key)), cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (StorageFailure(e)) { return null; }
    }

    public async Task WriteAsync(BookCacheValue value, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!CacheValid(value)) throw new InvalidDataException("书目缓存格式或编号无效。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (payload.Length > MaximumFileBytes || !CacheValid(Decode<BookCacheValue>(payload))) throw new IOException("书目缓存超过限制或内容无效。");
        await Run(CacheDirectoryName, true, false, async (root, profile) =>
        {
            var bytes = Encode(profile, CacheKind, value.Key, payload);
            var target = Path.Combine(root, FileKey(value.Key));
            var files = new DirectoryInfo(root).EnumerateFiles().Take(MaximumFiles + 1).ToArray();
            if (files.Length > MaximumFiles || files.Any(f => (f.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("书目缓存数量或路径不符合限制，原文件保留。");
            if (Exists(target) && ReadCache(target, profile, value.Key) is null) throw new IOException("原书目缓存不可验证，未覆盖。");
            var prior = files.SingleOrDefault(f => f.FullName.Equals(target, StringComparison.OrdinalIgnoreCase));
            long total = files.Sum(f => f.Length) - (prior?.Length ?? 0) + bytes.Length;
            int count = files.Length + (prior is null ? 1 : 0);
            var remove = new List<string>();
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaximumBytes && count <= MaximumFiles) break;
                if (file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase) || ReadCache(file.FullName, profile) is null) continue;
                remove.Add(file.FullName); total -= file.Length; count--;
            }
            if (total > MaximumBytes || count > MaximumFiles) throw new IOException("书目缓存空间不足，未删除未知文件。");
            await WriteAtomic(target, bytes, () =>
            {
                if (Exists(target) && ReadCache(target, profile, value.Key) is null) throw new IOException("书目缓存身份已改变，原文件保留。");
                foreach (var path in remove)
                {
                    if (ReadCache(path, profile) is null) throw new IOException("书目缓存维护条件已改变。");
                    File.Delete(path);
                }
            }, cancellation).ConfigureAwait(false);
            return true;
        }, cancellation).ConfigureAwait(false);
    }

    public async Task<BookPublicationBinding[]> ReadAsync(BookIdentity source, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!SourceValid(source)) throw new InvalidDataException("书籍来源身份无效。");
        return await Run(BindingsDirectoryName, false, Array.Empty<BookPublicationBinding>(), (root, profile) =>
            Task.FromResult(ReadBindings(Path.Combine(root, FileKey(BindingKey(source))), profile, source)), cancellation).ConfigureAwait(false);
    }

    public async Task WriteAsync(BookIdentity source, BookPublicationBinding[] bindings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BindingsValid(source, bindings)) throw new InvalidDataException("出版关联身份、数量或编号无效。");
        // Serialize the supplied choices before awaiting; no caller-owned mutable
        // array can change which mappings are committed during the write.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new BindingDocument(source, bindings), Options);
        var document = Decode<BindingDocument>(payload);
        if (document is null || !BindingsValid(source, document.Bindings)) throw new InvalidDataException("出版关联内容无效。");
        await Run(BindingsDirectoryName, true, false, async (root, profile) =>
        {
            var key = BindingKey(source); var target = Path.Combine(root, FileKey(key));
            _ = ReadBindings(target, profile, source);
            var bytes = Encode(profile, BindingKind, key, payload);
            await WriteAtomic(target, bytes, () => { _ = ReadBindings(target, profile, source); }, cancellation).ConfigureAwait(false);
            return true;
        }, cancellation).ConfigureAwait(false);
    }

    private async Task WriteAtomic(string target, byte[] bytes, Action recheck, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await WindowsAtomicFile.WriteAsync(target, bytes, async stage =>
        {
            if (persistenceProbe is not null) await persistenceProbe(stage).ConfigureAwait(false);
            if (stage is PersistenceStage.BeforeTempWrite or PersistenceStage.BeforeReplace) cancellation.ThrowIfCancellationRequested();
            if (stage == PersistenceStage.BeforeReplace) recheck();
            // After entering replacement, commit the complete file even if cancellation
            // arrives. Callers must not report the persisted confirmation as rolled back.
        }).ConfigureAwait(false);
    }

    private static byte[] Encode(NativeProfileStore profile, string kind, string key, byte[] payload)
    {
        if (payload.Length > MaximumFileBytes) throw new IOException("书目文件超过大小限制。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, kind, profile.Manifest.ProfileId,
            profile.Manifest.StoreId, key, Convert.ToHexString(SHA256.HashData(payload)), payload), Options);
        if (bytes.Length > MaximumFileBytes) throw new IOException("书目文件超过大小限制。");
        return bytes;
    }
    private static BookCacheValue? ReadCache(string path, NativeProfileStore profile, string? expected = null)
    {
        try
        {
            var envelope = ReadEnvelope(path, profile, CacheKind, expected);
            if (envelope is null) return null;
            var value = Decode<BookCacheValue>(envelope.Payload);
            return CacheValid(value) && value!.Key == envelope.Key ? value : null;
        }
        catch (Exception e) when (StorageFailure(e)) { return null; }
    }
    private static BookPublicationBinding[] ReadBindings(string path, NativeProfileStore profile, BookIdentity source)
    {
        var envelope = ReadEnvelope(path, profile, BindingKind, BindingKey(source));
        if (envelope is null) return [];
        var document = Decode<BindingDocument>(envelope.Payload);
        if (document is null || document.Source != source || !BindingsValid(source, document.Bindings))
            throw new IOException("出版关联内容不可验证，原文件保留。");
        return document.Bindings;
    }
    private static Envelope? ReadEnvelope(string path, NativeProfileStore profile, string kind, string? expected)
    {
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length is < 1 or > MaximumFileBytes) throw new IOException("书目文件大小无效，原文件保留。");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            var value = Decode<Envelope>(bytes);
            if (value is null || value.Version != 1 || value.Kind != kind || value.ProfileId != profile.Manifest.ProfileId
                || value.StoreId != profile.Manifest.StoreId || value.Key is not { Length: > 0 and <= 100 }
                || expected is not null && value.Key != expected || Path.GetFileName(path) != FileKey(value.Key)
                || value.Payload is not { Length: > 0 and <= MaximumFileBytes }
                || value.Hash != Convert.ToHexString(SHA256.HashData(value.Payload)))
                throw new IOException("书目文件身份或校验无效，原文件保留。");
            return value;
        }
        catch (FileNotFoundException) { return null; }
    }
    private static T? Decode<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            CheckUniqueProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(Options);
        }
        catch (JsonException e) { throw new IOException("书目文件格式不可读取，原文件保留。", e); }
    }
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate property."); CheckUniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) CheckUniqueProperties(element);
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }
    private async Task<T> Run<T>(string directory, bool create, T missing, Func<string, NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        if (configuration.Profile is not NativeProfileStore profile) throw new IOException("缺少书目资料身份，不能保存出版确认。");
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var parent = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(parent);
                try
                {
                    profile.ValidateAccess(); var root = Path.Combine(parent, directory); NativeProfilePaths.CheckChain(root);
                    if (!create && !Directory.Exists(root)) return missing;
                    if (create) Directory.CreateDirectory(root);
                    var pinned = IsolatedLibraryPersistence.PinDirectoryChain(root);
                    try
                    {
                        NativeProfilePaths.CheckChain(root); profile.ValidateAccess(); cancellation.ThrowIfCancellationRequested();
                        return await action(root, profile).ConfigureAwait(false);
                    }
                    finally { foreach (var handle in pinned) handle.Dispose(); }
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NativeProfileException or DataIsolationException or JsonException or ArgumentException)
        { throw new IOException("书目资料路径或文件不可用，原内容保留。", e); }
        finally { serial.Release(); }
    }
    private static bool StorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or NativeProfileException or DataIsolationException or ArgumentException;
}
