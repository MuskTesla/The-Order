namespace TheOrder.Application;

public interface IGameAudioChannel : IDisposable
{
    string Id { get; }
    long ExternalChanges { get; }
    // This refers to the retained session object, never to a fresh lookup by PID.
    bool IsOriginalSession { get; }
    bool Muted { get; }
    void SetMuted(bool value);
}
public interface IGameAudioPlatform
{
    AudioEnumerationStatus EnumerationStatus => AudioEnumerationStatus.Complete;
    bool IsCurrent(ProcessIdentity process);
    bool? IsForeground(ProcessIdentity process);
    IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity process);
}
public enum AudioEnumerationStatus { Complete, Partial, Failed }
// Only mute a confirmed process and restore changes made by this controller.
// A user's mixer adjustment invalidates our restoration ownership.
public sealed class BackgroundAudioController(IGameAudioPlatform platform) : IDisposable
{
    private sealed record Owned(ProcessIdentity Process, IGameAudioChannel Channel, long Version, bool Changed);
    private readonly Dictionary<string, Owned> owned = new(StringComparer.Ordinal);
    public string? Failure { get; private set; }
    public int OwnedCount => owned.Values.Count(item => item.Changed);
    public int ObservedCount => owned.Count;
    public void Update(ProcessIdentity? process, bool enabled)
    {
        Failure = null;
        var current = process is not null && enabled && platform.IsCurrent(process);
        var background = current && platform.IsForeground(process!) == false;
        foreach (var key in owned.Keys.ToArray())
            if (!background || owned[key].Process != process) Release(key);
        if (!background) return;
        IReadOnlyList<IGameAudioChannel> channels;
        try { channels = platform.Channels(process!); }
        catch { Failure = "AudioUnavailable"; return; }
        bool complete = platform.EnumerationStatus == AudioEnumerationStatus.Complete;
        if (!complete) Failure ??= "AudioUnavailable";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            if (!seen.Add(channel.Id)) { channel.Dispose(); continue; }
            if (owned.TryGetValue(channel.Id, out var existing))
            {
                channel.Dispose();
                // An external override is respected until this background interval ends.
                // Keep the lease, so we do not repeatedly force the mixer back to mute.
                if (existing.Channel.ExternalChanges != existing.Version) continue;
                continue;
            }
            try
            {
                if (owned.Count >= 128) { Failure = "AudioCapacity"; channel.Dispose(); continue; }
                var version = channel.ExternalChanges;
                if (platform.IsCurrent(process!) && channel.IsOriginalSession)
                {
                    bool changed = !channel.Muted;
                    // Own the lease before mutating. A failed COM reply must not
                    // strand a successfully changed session without a restore path.
                    // Initially muted sessions are observed without taking write
                    // ownership, so a later user unmute is respected this interval.
                    owned.Add(channel.Id, new(process!, channel, version, changed));
                    if (changed)
                    {
                        try { channel.SetMuted(true); }
                        catch { Failure = "AudioUnavailable"; Release(channel.Id); }
                    }
                }
                else { channel.Dispose(); continue; }
            }
            catch { Failure = "AudioUnavailable"; channel.Dispose(); }
        }
        // Missing from a partial enumeration is not proof a session disappeared.
        if (complete) foreach (var key in owned.Keys.Except(seen, StringComparer.Ordinal).ToArray()) Release(key);
    }
    private void Release(string key)
    {
        var item = owned[key];
        try
        {
            // A process can exit while its rendering session's mute setting persists.
            // Restore only our retained exact session, even after that process exits.
            // Never enumerate a replacement process with a reused PID to restore it.
            if (item.Changed && item.Channel.ExternalChanges == item.Version && item.Channel.IsOriginalSession && item.Channel.Muted)
                item.Channel.SetMuted(false);
            owned.Remove(key); item.Channel.Dispose();
        }
        catch { Failure = "AudioRestoreFailed"; } // Retain ownership for the next bounded retry.
    }
    public void Dispose()
    {
        Failure = null;
        for (int attempt = 0; attempt < 3 && owned.Count > 0; attempt++)
            foreach (var key in owned.Keys.ToArray()) Release(key);
        Failure = owned.Count > 0 ? "AudioRestoreFailed" : null;
        foreach (var item in owned.Values) try { item.Channel.Dispose(); } catch { }
        owned.Clear();
    }
}
