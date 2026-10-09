using System.Diagnostics.Metrics;

namespace NetRoute.Gateway.RateLimiting;

public sealed record TierLimit(int Capacity, double RefillPerSecond);

public sealed record RateLimitPolicy
{
    public string Name { get; init; } = string.Empty;
    public int Capacity { get; init; }
    public double RefillPerSecond { get; init; }
    public IReadOnlyDictionary<string, TierLimit>? Tiers { get; init; }

    public TierLimit Resolve(string? tier)
        => tier is not null && Tiers is not null && Tiers.TryGetValue(tier, out var t)
            ? t
            : new TierLimit(Capacity, RefillPerSecond);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Name is required.");
        }

        Check("base", Capacity, RefillPerSecond);
        foreach (var (tier, limit) in Tiers ?? new Dictionary<string, TierLimit>())
        {
            Check($"tier '{tier}'", limit.Capacity, limit.RefillPerSecond);
        }

        return errors;

        void Check(string scope, int capacity, double refill)
        {
            if (capacity < 1)
            {
                errors.Add($"{scope}: Capacity must be >= 1.");
            }

            if (!(refill > 0) || double.IsInfinity(refill))
            {
                errors.Add($"{scope}: RefillPerSecond must be > 0.");
            }
        }
    }
}

public readonly record struct RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan RetryAfter, bool Degraded = false);

public interface IRateLimiter
{
    ValueTask<RateLimitDecision> AcquireAsync(string key, int capacity, double refillPerSecond, CancellationToken ct);
}

public enum FailureMode
{
    LocalFallback,

    FailOpen,

    FailClosed,
}

public static class GatewayMetrics
{
    public static readonly Meter Meter = new("NetRoute");

    public static readonly Counter<long> Decisions = Meter.CreateCounter<long>("netroute.ratelimit.decisions");
    public static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("netroute.ratelimit.fallbacks");
    public static readonly Histogram<double> DecisionMs = Meter.CreateHistogram<double>("netroute.ratelimit.duration", "ms");
}
