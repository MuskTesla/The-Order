using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Platform.Windows;

namespace TheOrder.WinUI;

public sealed partial class MainWindow
{
    private bool audioSmokeFixture;
    private ProcessIdentity? audioSmokeIdentity;

    // This marked Test-only seam observes an externally started, project-owned
    // silent source. It cannot start games, accept paths or open another Profile.
    private async Task RunAudioGuardFixtureAsync(JsonElement request)
    {
        if (Program.Profile is not { } profile || profile.Manifest.ProfileKind != NativeProfileKind.Test
            || visualFixtureTimer is null || fixtureDirectory is null || p1Stopping)
            throw new InvalidOperationException("Isolated Test window required.");
        var action = request.GetProperty("value").GetString();
        if (action is not ("start" or "probe" or "stop")) throw new InvalidDataException("Unsupported audio Test action.");
        int? evidencePid = audioSmokeIdentity?.Pid;
        if (action == "start")
        {
            var text = request.GetProperty("id").GetString() ?? "";
            if (text.Length is 0 or > 10 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || pid <= 0 || pid == Environment.ProcessId) throw new InvalidDataException("Synthetic audio PID required.");
            var directory = Path.Combine(profile.Root, "harmless-audio");
            var marker = Path.Combine(directory, ".the-order-audio-proof-source");
            var expected = Path.Combine(directory, "AudioProof.exe");
            if (!Directory.Exists(directory) || !File.Exists(marker) || !File.Exists(expected)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Marked Test audio source required.");
            using var source = Process.GetProcessById(pid);
            if (source.HasExited || !string.Equals(source.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Exact synthetic audio image required.");
            var identity = new ProcessIdentity(pid, source.StartTime.ToUniversalTime().ToFileTimeUtc(), expected, null);
            if (!new WindowsGameAudioPlatform().IsCurrent(identity)) throw new InvalidOperationException("Synthetic audio generation changed.");
            if (audioSmokeIdentity is { } previous && previous != identity)
                throw new InvalidOperationException("Stop the previous Test audio observation first.");
            audioSmokeIdentity = identity; audioSmokeFixture = true; evidencePid = pid;
            await backgroundAudio.UpdateAsync(identity, true);
        }
        else if (action == "probe")
        {
            if (!audioSmokeFixture || audioSmokeIdentity is not { } identity)
                throw new InvalidOperationException("Start the marked Test audio observation first.");
            // The guard itself validates the retained PID/creation/image again;
            // an exited source is released, never replaced by a reused PID.
            await backgroundAudio.UpdateAsync(identity, true);
        }
        else
        {
            try { await backgroundAudio.UpdateAsync(null, false); }
            finally { audioSmokeFixture = false; audioSmokeIdentity = null; }
        }
        await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, "audio-guard-evidence.json"), JsonSerializer.Serialize(new
        {
            action, active = backgroundAudio.Active, failure = backgroundAudio.Failure, targetPID = evidencePid,
            applicationPID = Environment.ProcessId, testingOnly = true, guardOpensProfile = false,
            exactMarkedSource = true, profileKind = profile.Manifest.ProfileKind.ToString()
        }));
    }
}
