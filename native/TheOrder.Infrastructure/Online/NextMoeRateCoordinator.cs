using System.Net.Http.Headers;
using System.Globalization;
using System.Text.RegularExpressions;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Online;

// One scheduler per profile, shared by catalog and personal-data reads/writes. No credential hashes.
public sealed class NextMoeRateCoordinator(TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly object gate = new();
    private long generation = -1;
    private DateTimeOffset next, blocked, quotaEnd;
    private double period;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    public async Task WaitAsync(long identity, NextMoeAccessMode mode, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (gate)
            {
                if (generation != identity) { generation = identity; next = blocked = quotaEnd = default; period = mode switch { NextMoeAccessMode.Anonymous => 2, NextMoeAccessMode.Account => .6, _ => 1 }; }
                if (quotaEnd > Now) throw new OnlineException(OnlineFailure.QuotaExceeded, quotaEnd - Now);
                wait = (next > blocked ? next : blocked) - Now;
                if (wait <= TimeSpan.Zero) { next = Now.AddSeconds(period); return; }
            }
            await (delay ?? Task.Delay)(wait, cancellation).ConfigureAwait(false);
        }
    }
    public void Observe(long identity, HttpResponseHeaders headers, string? code)
    {
        lock (gate)
        {
            if (generation != identity) return;
            if (headers.TryGetValues("RateLimit-Policy", out var policies))
                foreach (var part in string.Join(",", policies).Split(','))
                {
                    var match = Regex.Match(part, @"^\s*(\d+)\s*;\s*w=(\d+)\s*$", RegexOptions.CultureInvariant);
                    if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var limit)
                        && double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var window)
                        && limit > 0 && limit <= 10000000 && window is > 0 and <= 86400 && window <= 60) period = window / limit;
                }
            var retry = headers.RetryAfter?.Delta ?? headers.RetryAfter?.Date - Now;
            if (headers.TryGetValues("RateLimit", out var limits))
            {
                var text = string.Join(",", limits);
                var remaining = Number(text, "remaining"); var reset = Number(text, "reset");
                if (remaining == 0 && reset is >= 0 and <= 86400) blocked = Now.AddSeconds(reset.Value);
            }
            if (code == "QUOTA_EXCEEDED")
            {
                quotaEnd = retry is { TotalSeconds: > 0 and <= 86400 } value ? Now + value : new DateTimeOffset(Now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            }
            else if (code == "RATE_LIMITED") blocked = Now.AddSeconds(Math.Clamp(retry?.TotalSeconds ?? 60, .1, 86400) + Random.Shared.NextDouble() * .25);
        }
    }
    private static double? Number(string value, string key)
    {
        var match = Regex.Match(value, @"(?:^|,)\s*" + key + @"=(\d+(?:\.\d+)?)\s*(?:,|$)", RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
