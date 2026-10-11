namespace TheOrder.Platform.Windows;

// Optional bounded lifecycle journal. Only controlled level/type identifiers,
// no paths, exception messages, credentials or user content. No held OS handles.
public sealed class NativeLifecycleJournal(NativeProfileStore profile)
{
    private readonly object gate = new();
    public void Write(string level, string? type = null)
    {
        lock (gate)
        {
            try
            {
                if (level.Length > 32 || type?.Length > 128 || level.Any(c => !char.IsAsciiLetter(c)) || type?.Any(c => !char.IsAsciiLetterOrDigit(c)) == true) return;
                profile.ValidateAccess();
                var root = NativeProfileAccess.Validate(profile.Configuration());
                var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try
                {
                    var path = Path.Combine(root, "native-runtime.log"); var previous = "";
                    if (File.Exists(path))
                    {
                        using var input = IsolatedLibrarySource.OpenChecked(path);
                        if (input.Length < 65536) using (var reader = new StreamReader(input)) previous = reader.ReadToEnd();
                    }
                    var line = $"{DateTimeOffset.UtcNow:O} {Environment.ProcessId} {level} {type ?? "Lifecycle"}\n";
                    WindowsAtomicFile.WriteAsync(path, System.Text.Encoding.UTF8.GetBytes(previous + line)).GetAwaiter().GetResult();
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }
            catch { /* Logging cannot prevent a safe startup/exit. */ }
        }
    }
}
