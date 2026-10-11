using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Disposable derived public catalog/CG choices. Independent from the profile component contract;
// bounded, identity-bound and physically pinned, like the existing thumbnail cache.
public sealed partial class IsolatedCatalogPresentationCache(NativeConfiguration configuration) : ICatalogPresentationCache
{
    private const long MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumFiles = 128;
    private readonly SemaphoreSlim serial = new(1);
    private sealed record Envelope(int Version, string Identity, string Hash, byte[] Payload);
    private static string FileKey(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private string Identity(string root) => configuration.Profile is NativeProfileStore store
        ? store.Manifest.ProfileId.ToString("N") + ":" + store.Manifest.StoreId.ToString("N")
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
    public Task<CatalogPresentationEntry?> ReadAsync(string id, CancellationToken cancellation)
        => Run<CatalogPresentationEntry?>((root, identity) => {
            try
            {
                using var file = IsolatedLibrarySource.OpenChecked(Path.Combine(root, FileKey(id) + ".json"));
                if (file.Length is < 1 or > 1500000) return null;
                var data = JsonSerializer.Deserialize<Envelope>(file);
                if (data is null || data.Payload is null || data.Version != 1 || data.Identity != identity || data.Payload.Length > CatalogPresentationService.MaximumEntryBytes ||
                    Convert.ToHexString(SHA256.HashData(data.Payload)) != data.Hash) return null;
                var entry = JsonSerializer.Deserialize<CatalogPresentationEntry>(data.Payload);
                return entry is not null && CatalogPresentationService.Valid(entry, id) ? entry : null;
            }
            catch (Exception e) when (e is FileNotFoundException or JsonException or InvalidDataException or ArgumentException) { return null; }
        }, id, cancellation);
    public Task WriteAsync(CatalogPresentationEntry entry, CancellationToken cancellation)
        => Run((root, identity) => {
            if (!CatalogPresentationService.Valid(entry, entry.Work.Id)) return false;
            var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
            if (payload.Length > CatalogPresentationService.MaximumEntryBytes) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, identity, Convert.ToHexString(SHA256.HashData(payload)), payload));
            var files = new DirectoryInfo(root).EnumerateFiles("*.json").Take(MaximumFiles + 1).ToArray();
            if (files.Length > MaximumFiles) return false; // Never turn a flooded cache into an unbounded sweep.
            string target = Path.Combine(root, FileKey(entry.Work.Id) + ".json");
            long total = files.Sum(f => f.Length); int count = files.Length;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (total + bytes.Length <= MaximumBytes && count < MaximumFiles) break;
                if (!KeyPattern().IsMatch(Path.GetFileNameWithoutExtension(file.Name))) continue;
                using (IsolatedLibrarySource.OpenChecked(file.FullName)) { }
                total -= file.Length; count--; File.Delete(file.FullName);
            }
            try
            {
                using var prior = IsolatedLibrarySource.OpenChecked(target);
                // A foreign identity or future cache envelope is evidence, not permission to replace it.
                try { var old = JsonSerializer.Deserialize<Envelope>(prior); if (old is not null && (old.Version != 1 || old.Identity != identity)) return false; }
                catch (JsonException) { /* A damaged cache of this algorithm is regenerable. */ }
            }
            catch (FileNotFoundException) { }
            string temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                // Once the atomic replacement starts, finish it; cancellation cannot publish half a cache.
                File.Move(temporary, target, true); return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, entry.Work.Id, cancellation);
    private async Task<T> Run<T>(Func<string, string, T> action, string id, CancellationToken cancellation)
    {
        if (!CatalogPresentationService.ValidId(id)) throw new InvalidDataException("Invalid public work identifier");
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => {
                var profile = NativeProfileAccess.Validate(configuration);
                var held = IsolatedLibraryPersistence.PinDirectoryChain(profile);
                try
                {
                    NativeProfileAccess.Validate(configuration);
                    string root = Path.Combine(profile, "native-catalog-" + CatalogPresentationService.Algorithm);
                    Directory.CreateDirectory(root);
                    var cacheHeld = IsolatedLibraryPersistence.PinDirectoryChain(root);
                    try { cancellation.ThrowIfCancellationRequested(); return action(root, Identity(profile)); }
                    finally { foreach (var handle in cacheHeld) handle.Dispose(); }
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
}
