using TheOrder.Domain;

namespace TheOrder.Application;

public readonly record struct SessionClockStamp(long Awake100ns, long SuspendEpoch, DateTimeOffset Utc);
public interface ISessionClock
{
    SessionClockStamp Read();
    void Discontinuity();
}
public interface ISessionPowerEvents { event Action<bool>? SuspendedChanged; }
public sealed record SessionRecoveryIdentity(int Version, string SessionId, string GameId, string InstallationId,
    uint BootId, FilesystemIdentity ProfileIdentity, ProcessIdentity Process,
    PathObservation Folder, PathObservation Executable, PathObservation WorkingDirectory);
public interface ISessionRecoveryTokens
{
    uint? BootId { get; }
    FilesystemIdentity ProfileIdentity { get; }
    string Seal(SessionRecoveryIdentity value);
    SessionRecoveryIdentity? Open(string value);
}
public sealed record SessionEngineSnapshot(PlaySession? Session, SessionTracking Tracking, SessionTiming Timing,
    bool Durable, long Durable100ns, ProcessIdentity? Current, string? Error);

// Not a clock itself: counts only intervals whose two endpoint observations establish continuity.
public sealed class SessionAccumulator
{
    public const long CheckpointTicks = 55 * NativeSession.TicksPerSecond;
    public const long UncommittedLimit = 60 * NativeSession.TicksPerSecond;
    public long Accumulated { get; private set; }
    public long Durable { get; private set; }
    public SessionClockStamp? Baseline { get; private set; }
    public bool AtLimit => Accumulated - Durable >= UncommittedLimit;
    public bool AtCapacity => Accumulated >= NativeSession.MaximumTicks;
    public SessionAccumulator(long durable = 0) { Accumulated = Durable = durable; }
    public void Freeze() => Baseline = null;
    public void Acknowledge(long value)
    {
        if (value < Durable || value > Accumulated) throw new SessionConflictException();
        Durable = value; Freeze();
    }
    public void Observe(SessionClockStamp before, SessionClockStamp after, bool permitted)
    {
        if (!permitted || before.SuspendEpoch != after.SuspendEpoch || after.Awake100ns < before.Awake100ns) { Freeze(); return; }
        if (Baseline is { } last && last.SuspendEpoch == before.SuspendEpoch && before.Awake100ns >= last.Awake100ns)
            Add(before.Awake100ns - last.Awake100ns);
        Baseline = AtLimit || AtCapacity ? null : after;
    }
    public void Bridge(SessionClockStamp previous, SessionClockStamp before, SessionClockStamp after)
    {
        if (previous.SuspendEpoch == before.SuspendEpoch && before.Awake100ns >= previous.Awake100ns
            && before.Awake100ns - previous.Awake100ns <= 6 * NativeSession.TicksPerSecond)
        { Baseline = previous; Observe(before, after, true); }
        else { Freeze(); Observe(before, after, true); }
    }
    private void Add(long ticks)
    {
        var available = Math.Min(UncommittedLimit - (Accumulated - Durable), NativeSession.MaximumTicks - Accumulated);
        Accumulated = checked(Accumulated + Math.Min(ticks, available));
    }
}
