namespace TheOrder.Infrastructure.Online;

// Shared by every provider/view in this application, including retries. Serial requests plus
// monotonic spacing avoid bursts after slow responses, clock changes, or concurrent page switches.
public sealed class MusicBrainzRateCoordinator(TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static MusicBrainzRateCoordinator Shared { get; } = new();
    private readonly SemaphoreSlim serial = new(1);
    private long lastStart;
    private bool started;
    private DateTimeOffset blockedUntil;
    private TimeProvider Clock => clock ?? TimeProvider.System;
    public async Task<IDisposable> EnterAsync(CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var spacing = started ? TimeSpan.FromSeconds(1.1) - Clock.GetElapsedTime(lastStart) : TimeSpan.Zero;
                var blocked = blockedUntil - Clock.GetUtcNow(); var wait = spacing > blocked ? spacing : blocked;
                if (wait <= TimeSpan.Zero) break;
                await (delay ?? Task.Delay)(wait, cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested(); started = true; lastStart = Clock.GetTimestamp();
            return new Lease(serial);
        }
        catch { serial.Release(); throw; }
    }
    // Called while holding the lease. Never shorten a server-specified cooldown.
    public void Backoff(TimeSpan value)
    {
        var until = Clock.GetUtcNow() + value;
        if (until > blockedUntil) blockedUntil = until;
    }
    private sealed class Lease(SemaphoreSlim serial) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) serial.Release(); }
    }
}
