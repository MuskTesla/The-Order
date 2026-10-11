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
    internal static byte[] Encode(ClientPreferences preferences)
    {
        PreferenceValidation.Validate(preferences);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, new(preferences.Theme, preferences.Layout, preferences.Sort, preferences.Filters)), Options);
    }
    internal static ClientPreferences Decode(byte[] bytes)
    {
        if (bytes.Length > 200000) throw new InvalidDataException(UiText.Get("界面偏好过大。"));
        Envelope envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, new JsonSerializerOptions(Options) {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true }) ?? throw new InvalidDataException(UiText.Get("界面偏好无效。")); }
        catch (JsonException e)
        {
            try { using var document = JsonDocument.Parse(bytes); }
            catch (JsonException) { throw new InvalidDataException(UiText.Get("界面偏好损坏。"), e); }
            throw new NativeProfileException("UnsupportedPreferenceSchema");
        }
        if (envelope.Version != 1 || envelope.Preferences is null) throw new NativeProfileException("UnsupportedPreferenceVersion");
        var value = envelope.Preferences;
        var preferences = new ClientPreferences(value.Theme, value.Layout, value.Sort, value.SavedFilters);
        PreferenceValidation.Validate(preferences); return preferences;
    }
    public Task<ClientPreferences> LoadAsync(CancellationToken cancellation) => RunAsync(root =>
    {
        if (configuration.Profile is { } profile) return Decode(profile.ReadComponent("native-ui.json") ?? throw new InvalidDataException("Missing preferences"));
        var file = Path.Combine(root, "native-ui.json");
        try
        {
            using var stream = IsolatedLibrarySource.OpenChecked(file);
            if (stream.Length > 200000) throw new InvalidDataException(UiText.Get("界面偏好过大，旧文件未覆盖。"));
            var envelope = JsonSerializer.Deserialize<Envelope>(stream, Options) ?? throw new InvalidDataException(UiText.Get("界面偏好无效。"));
            if (envelope.Version != 1 || envelope.Preferences is null) throw new InvalidDataException(UiText.Get("不支持此界面偏好版本。"));
            var value = envelope.Preferences;
            var preferences = new ClientPreferences(value.Theme, value.Layout, value.Sort, value.SavedFilters);
            PreferenceValidation.Validate(preferences); return preferences;
        }
        catch (FileNotFoundException) { return new(); }
        catch (JsonException error) { throw new InvalidDataException(UiText.Get("界面偏好格式损坏，旧文件未覆盖。"), error); }
    }, cancellation);
    public Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation) => RunAsync(root =>
    {
        PreferenceValidation.Validate(preferences);
        var bytes = Encode(preferences);
        if (bytes.Length > 200000) throw new InvalidDataException(UiText.Get("界面偏好过大，未保存。"));
        if (configuration.Profile is { } profile) { profile.WriteComponent("native-ui.json", bytes); return true; }
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
                var root = NativeProfileAccess.Validate(configuration);
                var handles = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { NativeProfileAccess.Validate(configuration); return action(root); }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
}
