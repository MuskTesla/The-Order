using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsLibrarySourcesStore(NativeConfiguration configuration) : ILibrarySourcesStore
{
    private readonly SemaphoreSlim queue = new(1);
    private sealed record Envelope(int Version, string Identity, ImmutableArray<LibrarySource> Sources);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private static string Identity(NativeConfiguration configuration, string root) => configuration.Profile is NativeProfileStore p ? p.Manifest.ProfileId + ":" + p.Manifest.StoreId
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
    public Task<ImmutableArray<LibrarySource>> LoadAsync(CancellationToken cancellation) => Run((root, id) => Read(root, id)?.Sources ?? [], cancellation);
    private static Envelope? Read(string root, string id)
    {
        try
        {
            using var f = IsolatedLibrarySource.OpenChecked(Path.Combine(root, "native-library-sources-v1.json"));
            if (f.Length > 200000) throw new InvalidDataException("库目录配置过大。");
            var value = JsonSerializer.Deserialize<Envelope>(f, Options) ?? throw new InvalidDataException("库目录配置损坏。");
            if (value.Version != 1 || value.Identity != id) throw new InvalidDataException("库目录配置版本或资料身份不匹配，原文件保留。");
            LibrarySourcesService.Validate(value.Sources); return value;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException e) { throw new InvalidDataException("库目录配置损坏，原文件保留。", e); }
    }
    public Task SaveAsync(ImmutableArray<LibrarySource> sources, CancellationToken cancellation) => Run((root, id) =>
    {
        LibrarySourcesService.Validate(sources); _ = Read(root, id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, id, sources), Options);
        if (bytes.Length > 200000) throw new InvalidDataException("库目录配置过大，请减少目录或恢复部分忽略记录。");
        var target = Path.Combine(root, "native-library-sources-v1.json"); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { cancellation.ThrowIfCancellationRequested(); using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); } _ = Read(root, id); File.Move(temporary, target, true); return true; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellation);
    private async Task<T> Run<T>(Func<string, string, T> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => { var root = NativeProfileAccess.Validate(configuration); var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
            try { NativeProfileAccess.Validate(configuration); return action(root, Identity(configuration, root)); } finally { foreach (var h in held) h.Dispose(); } }, cancellation).ConfigureAwait(false); }
        finally { queue.Release(); }
    }
}
