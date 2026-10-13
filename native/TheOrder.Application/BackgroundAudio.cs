namespace TheOrder.Application;

public interface IGameAudioChannel : IDisposable
{
    string Id { get; }
    long ExternalChanges { get; }
    bool Muted { get; }
    void SetMuted(bool value);
}
public interface IGameAudioPlatform
{
    bool IsCurrent(ProcessIdentity process);
    bool? IsForeground(ProcessIdentity process);
    IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity process);
}
// Only mute a confirmed process and restore changes made by this controller.
// A user's mixer adjustment invalidates our restoration ownership.
public sealed class BackgroundAudioController(IGameAudioPlatform platform) : IDisposable
{
    private sealed record Owned(ProcessIdentity Process, IGameAudioChannel Channel, long Version);
    private readonly Dictionary<string, Owned> owned = new(StringComparer.Ordinal);
    public void Update(ProcessIdentity? process, bool enabled)
    {
        var current = process is not null && enabled && platform.IsCurrent(process);
        var background = current && platform.IsForeground(process!) == false;
        foreach (var key in owned.Keys.ToArray())
            if (!background || owned[key].Process != process) Release(key);
        if (!background) return;
        var channels = platform.Channels(process!); var seen = new HashSet<string>(StringComparer.Ordinal);
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
                var version = channel.ExternalChanges;
                if (!channel.Muted && platform.IsCurrent(process!)) channel.SetMuted(true);
                else { channel.Dispose(); continue; }
                owned.Add(channel.Id, new(process!, channel, version));
            }
            catch { channel.Dispose(); }
        }
        foreach (var key in owned.Keys.Except(seen, StringComparer.Ordinal).ToArray()) Release(key);
    }
    private void Release(string key)
    {
        var item = owned[key]; owned.Remove(key);
        try
        {
            if (platform.IsCurrent(item.Process) && item.Channel.ExternalChanges == item.Version && item.Channel.Muted)
                item.Channel.SetMuted(false);
        }
        catch { /* Disconnected audio cannot keep shutdown or game tracking alive. */ }
        finally { item.Channel.Dispose(); }
    }
    public void Dispose() { foreach (var key in owned.Keys.ToArray()) Release(key); }
}
