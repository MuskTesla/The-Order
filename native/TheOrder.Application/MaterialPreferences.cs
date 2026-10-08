namespace TheOrder.Application;

public sealed record MaterialPreference(string Mode = "Standard");
public sealed record MaterialPreferenceRead(MaterialPreference Preference, string? Warning = null, bool ReadOnly = false);
public interface IMaterialPreferencesStore
{
    Task<MaterialPreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(MaterialPreference preference, CancellationToken cancellation = default);
}

// Persisted intent never authorizes the optical path. Qualification is per launch,
// requires a real Test manifest, and is independent of system effects policy.
public static class MaterialActivationPolicy
{
    public static bool Known(string mode) => mode is "Auto" or "Advanced" or "Standard" or "Opaque";
    public static string Effective(string requested, NativeProfileKind kind, bool controlledTest)
        => !Known(requested) ? "Standard" : requested is "Auto" or "Advanced"
            ? kind == NativeProfileKind.Test && controlledTest ? requested : "Standard" : requested;
}
