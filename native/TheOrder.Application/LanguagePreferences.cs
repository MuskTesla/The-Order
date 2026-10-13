namespace TheOrder.Application;

public sealed record LanguagePreference(string Language = UiText.System);
public sealed record LanguagePreferenceRead(LanguagePreference Preference, string? Warning = null, bool ReadOnly = false);
public interface ILanguagePreferencesStore
{
    Task<LanguagePreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(LanguagePreference value, CancellationToken cancellation = default);
}
