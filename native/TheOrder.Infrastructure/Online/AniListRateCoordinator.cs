namespace TheOrder.Infrastructure.Online;

// AniList currently publishes a degraded 30/min limit. All instances share one
// monotonic queue; requests, retries and relation navigation cannot form bursts.
public sealed class AniListRateCoordinator(TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static AniListRateCoordinator Shared { get; } = new();
    private readonly SemaphoreSlim serial = new(1);
    private long lastStart; private bool started;
    private DateTimeOffset blockedUntil;
    private TimeSpan spacing = TimeSpan.FromSeconds(2.1);
    private TimeProvider Clock => clock ?? TimeProvider.System;
    public async Task<IDisposable> EnterAsync(CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var wait = started ? spacing - Clock.GetElapsedTime(lastStart) : TimeSpan.Zero;
                if (blockedUntil - Clock.GetUtcNow() > wait) wait = blockedUntil - Clock.GetUtcNow();
                if (wait <= TimeSpan.Zero) break;
                await (delay ?? Task.Delay)(wait, cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested(); started = true; lastStart = Clock.GetTimestamp(); return new Lease(serial);
        }
        catch { serial.Release(); throw; }
    }
    public void Observe(int? limit, int? remaining, DateTimeOffset? reset, TimeSpan? retry)
    {
        if (limit is > 0) spacing = TimeSpan.FromSeconds(Math.Max(2.1, 60.0 / limit.Value + .1));
        var until = retry is { } wait ? Clock.GetUtcNow() + wait : remaining == 0 ? reset ?? Clock.GetUtcNow().AddMinutes(1) : default;
        if (remaining == 0 && reset > until) until = reset.Value;
        if (until > blockedUntil) blockedUntil = until;
    }
    private sealed class Lease(SemaphoreSlim queue) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Release(); } }
}
