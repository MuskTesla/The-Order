using System.Text.Json;
using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Separate native UI preferences, never legacy settings.json, credentials or per-game fields.
public sealed class IsolatedUiPreferencesStore(NativeConfiguration configuration) : IUiPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };
    private sealed record StoredPreferences(NativeTheme Theme, string Layout, string Sort, ImmutableArray<SavedFilter> SavedFilters);
    private sealed record Envelope(int Version, StoredPreferences Preferences);
    private readonly SemaphoreSlim queue = new(1, 1);
    public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => RunAsync(root =>
    {
        var file = Path.Combine(root, "native-ui.json");
        try
        {
            using var stream = IsolatedLibrarySource.OpenChecked(file);
            if (stream.Length > 200000) throw new InvalidDataException("界面偏好过大，旧文件未覆盖。");
            var envelope = JsonSerializer.Deserialize<Envelope>(stream, Options) ?? throw new InvalidDataException("界面偏好无效。");
            if (envelope.Version != 1 || envelope.Preferences is null) throw new InvalidDataException("不支持此界面偏好版本。");
            var value = envelope.Preferences;
            var preferences = new ClientPreferences(value.Theme, value.Layout, value.Sort, value.SavedFilters);
            PreferenceValidation.Validate(preferences); return preferences;
        }
        catch (FileNotFoundException) { return new(); }
        catch (JsonException error) { throw new InvalidDataException("界面偏好格式损坏，旧文件未覆盖。", error); }
    }, cancellation);
    public Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation) => RunAsync(root =>
    {
        PreferenceValidation.Validate(preferences);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, new(preferences.Theme, preferences.Layout, preferences.Sort, preferences.Filters)), Options);
        if (bytes.Length > 200000) throw new InvalidDataException("界面偏好过大，未保存。");
        var target = Path.Combine(root, "native-ui.json"); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        void Check() { try { using var held = IsolatedLibrarySource.OpenChecked(target); } catch (FileNotFoundException) { } }
        Check();
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            Check(); File.Move(temporary, target, overwrite: true); return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellation);
    private async Task<T> RunAsync<T>(Func<string, T> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var root = IsolatedLibrarySource.ValidateDirectory(configuration.DataDirectory);
                var handles = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { IsolatedLibrarySource.ValidateDirectory(root); return action(root); }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
}
