namespace TheOrder.Application;

public sealed record MaterialPreference(string Mode = "Standard");
public sealed record MaterialPreferenceRead(MaterialPreference Preference, string? Warning = null, bool ReadOnly = false);
public interface IMaterialPreferencesStore
{
    Task<MaterialPreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(MaterialPreference preference, CancellationToken cancellation = default);
}

// Ordinary settings select the optical path. Windows effects, power, remote-session
// and accessibility policy still decide whether that path can be rendered.
public static class MaterialActivationPolicy
{
    public static bool Known(string mode) => mode is "Auto" or "Advanced" or "Standard" or "Opaque";
    public static string Effective(string requested, NativeProfileKind kind, bool controlledTest)
        => requested is "Auto" or "Advanced" or "Standard" ? requested : "Standard";
}
