using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsSessionClock : ISessionClock
{
    private readonly object gate = new();
    private readonly Func<(long Awake, long Before, long After)> read;
    private long epoch, lastAwake = -1, offsetUpper = long.MinValue;
    public WindowsSessionClock() : this(NativeRead) { }
    internal WindowsSessionClock(Func<(long Awake, long Before, long After)> read) => this.read = read;
    public void Discontinuity() { lock (gate) epoch++; }
    public SessionClockStamp Read()
    {
        lock (gate)
        {
            var (awake, before, after) = read();
            if (awake < 0 || before < 0 || after < before) throw new IOException("Windows awake clock 无效。");
            long lower = before - awake, upper = after - awake;
            // Conservative bracket plus 1ms quantization allowance; false discontinuities lose time, never add sleep.
            if (lastAwake >= 0 && (awake < lastAwake || lower > offsetUpper + 10_000)) epoch++;
            lastAwake = awake; offsetUpper = upper;
            return new(awake, epoch, DateTimeOffset.UtcNow);
        }
    }
    private static (long, long, long) NativeRead()
    {
        QueryInterruptTimePrecise(out var before); QueryUnbiasedInterruptTimePrecise(out var awake); QueryInterruptTimePrecise(out var after);
        return (checked((long)awake), checked((long)before), checked((long)after));
    }
    // Import the documented Windows 10 API-set contract; these precise exports are not forwarded
    // by kernel32 on every supported Windows build.
    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")] private static extern void QueryUnbiasedInterruptTimePrecise(out ulong time);
    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")] private static extern void QueryInterruptTimePrecise(out ulong time);
}
