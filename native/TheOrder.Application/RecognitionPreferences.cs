namespace TheOrder.Application;
public sealed record RecognitionPreference(bool UseOnline = true, bool IncludePe = true);
public sealed record RecognitionPreferenceRead(RecognitionPreference Preference, string? Warning = null, bool ReadOnly = false);
public interface IRecognitionPreferencesStore
{
    Task<RecognitionPreferenceRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(RecognitionPreference preference, CancellationToken cancellation = default);
}
