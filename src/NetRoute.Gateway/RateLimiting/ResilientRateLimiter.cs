using System.Diagnostics;

namespace NetRoute.Gateway.RateLimiting;

public sealed class ResilientRateLimiter(
    IRateLimiter primary,
    IRateLimiter fallback,
    FailureMode mode,
    TimeSpan cooldown,
    TimeProvider time,
    ILogger<ResilientRateLimiter> logger) : IRateLimiter
{
    private long _openUntil;

    public bool BreakerOpen => time.GetTimestamp() < Volatile.Read(ref _openUntil);

    public async ValueTask<RateLimitDecision> AcquireAsync(string key, int capacity, double refillPerSecond, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!BreakerOpen)
            {
                try
                {
                    return await primary.AcquireAsync(key, capacity, refillPerSecond, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var wasClosed = !BreakerOpen;
                    Volatile.Write(ref _openUntil, time.GetTimestamp() + (long)(cooldown.TotalSeconds * time.TimestampFrequency));
                    if (wasClosed)
                    {
                        logger.LogWarning(ex, "Rate-limit store unavailable; degrading to {Mode} for {Cooldown}", mode, cooldown);
                    }
                }
            }

            GatewayMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("mode", mode.ToString()));
            return mode switch
            {
                FailureMode.FailOpen => new RateLimitDecision(true, capacity, capacity, TimeSpan.Zero, Degraded: true),
                FailureMode.FailClosed => new RateLimitDecision(false, capacity, 0, cooldown, Degraded: true),
                _ => (await fallback.AcquireAsync(key, capacity, refillPerSecond, ct)) with { Degraded = true },
            };
        }
        finally
        {
            GatewayMetrics.DecisionMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
