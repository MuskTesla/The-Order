using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Borrow the profile's writer lease. Preserve the selected original, not a lossy
// poster thumbnail. An immutable image is admitted before its atomic selection.
// Older binaries ignore this optional companion and continue reading library v1.
public sealed class IsolatedPersonalBannerStore(NativeConfiguration configuration, IRasterCodec codec,
    Func<PersistenceStage, Task>? probe = null) : IPersonalBannerStore, IAsyncDisposable
{
    public const string DirectoryName = "native-personal-artwork-v1";
    public const string KeyPrefix = "local-banner-";
    private const int MaximumFiles = 10000;
    private const long MaximumBytes = 512L * 1024 * 1024;
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, string GameId, string? ImageKey);
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly SemaphoreSlim queue = new(1, 1);
    private bool disposed;

    private static string GameFile(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 256 || gameId.Any(char.IsControl))
            throw new InvalidDataException(UiText.Get("作品编号无效。"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameId))) + ".json";
    }
    private static bool IsKey(string key) => key.Length == KeyPrefix.Length + 64 && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && key.AsSpan(KeyPrefix.Length).ToString().All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static byte[]? ReadFile(string path, int maximum)
    {
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(path);
            if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException(UiText.Get("本地横幅文件大小无效，原文件保留。"));
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }
    private static Envelope? ReadSelection(string root, string gameId, NativeProfileStore profile)
    {
        var bytes = ReadFile(Path.Combine(root, GameFile(gameId)), 4096); if (bytes is null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
            var value = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new JsonException();
            if (value.Version != 1 || value.ProfileId != profile.Manifest.ProfileId || value.StoreId != profile.Manifest.StoreId
                || value.GameId != gameId || value.ImageKey is { } key && !IsKey(key)) throw new JsonException();
            return value;
        }
        catch (JsonException) { throw new InvalidDataException(UiText.Get("本地横幅设置损坏或不属于当前资料，原文件保留。")); }
    }
    public Task<string?> GetAsync(string gameId, CancellationToken cancellation = default) => Run((root, profile) =>
    {
        var selection = ReadSelection(root, gameId, profile);
        return Task.FromResult(selection?.ImageKey is { } key && File.Exists(Path.Combine(root, key + ".image")) ? key : null);
    }, cancellation);
    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellation) => Run((root, _) =>
    {
        if (!IsKey(key)) return Task.FromResult<byte[]?>(null);
        var bytes = ReadFile(Path.Combine(root, key + ".image"), WindowsRasterCodec.MaximumSourceBytes);
        if (bytes is not null && Convert.ToHexString(SHA256.HashData(bytes)) != key[KeyPrefix.Length..])
            throw new InvalidDataException(UiText.Get("本地横幅校验失败，原文件保留。"));
        return Task.FromResult(bytes);
    }, cancellation);
    public Task<PersonalBannerWrite> SaveAsync(string gameId, string? path, CancellationToken cancellation = default) => Run(async (root, profile) =>
    {
        var name = GameFile(gameId);
        _ = ReadSelection(root, gameId, profile); // Never overwrite foreign/future/corrupt selections.
        string? key = null;
        if (path is not null)
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                throw new InvalidDataException(UiText.Get("请选择 PNG、JPEG 或静态 WebP 图片。"));
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (file.Length is < 4 or > WindowsRasterCodec.MaximumSourceBytes) throw new InvalidDataException(UiText.Get("横幅图片不能超过 20 MB。"));
                bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            }
            // A small decode validates the complete bounded static container and original
            // dimensions before admission. The managed file keeps the exact encoded bytes.
            await codec.DecodeAsync(bytes, 512, cancellation).ConfigureAwait(false);
            key = KeyPrefix + Convert.ToHexString(SHA256.HashData(bytes));
            var image = Path.Combine(root, key + ".image");
            var existing = ReadFile(image, WindowsRasterCodec.MaximumSourceBytes);
            if (existing is not null && !existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException(UiText.Get("本地横幅校验失败，原文件保留。"));
            if (existing is null)
            {
                EnsureCapacity(root, bytes.Length + 4096);
                cancellation.ThrowIfCancellationRequested();
                await WindowsAtomicFile.WriteAsync(image, bytes).ConfigureAwait(false);
            }
        }
        if (!File.Exists(Path.Combine(root, name))) EnsureCapacity(root, 4096);
        cancellation.ThrowIfCancellationRequested();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, gameId, key));
        var receipt = await WindowsAtomicFile.WriteAsync(Path.Combine(root, name), encoded, probe).ConfigureAwait(false);
        return new PersonalBannerWrite(key, receipt.Warning);
    }, cancellation);
    private static void EnsureCapacity(string root, long additional)
    {
        var files = new DirectoryInfo(root).EnumerateFiles().Take(MaximumFiles + 1).ToArray();
        if (files.Length >= MaximumFiles || files.Sum(f => f.Length) + additional > MaximumBytes)
            throw new InvalidDataException(UiText.Get("本地横幅缓存已满；原横幅保留。"));
    }
    private async Task<T> Run<T>(Func<string, NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration.Profile is not NativeProfileStore profile) throw new InvalidOperationException("Native profile required for personal artwork");
            return await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var root = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try
                {
                    profile.ValidateAccess(); var artwork = Path.Combine(root, DirectoryName); Directory.CreateDirectory(artwork);
                    var pinned = IsolatedLibraryPersistence.PinDirectoryChain(artwork);
                    try { cancellation.ThrowIfCancellationRequested(); return await action(artwork, profile).ConfigureAwait(false); }
                    finally { foreach (var handle in pinned) handle.Dispose(); }
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await queue.WaitAsync().ConfigureAwait(false);
        try { disposed = true; } finally { queue.Release(); }
    }
}
