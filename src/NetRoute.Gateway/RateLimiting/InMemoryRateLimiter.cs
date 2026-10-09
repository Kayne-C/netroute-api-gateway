using System.Collections.Concurrent;

namespace NetRoute.Gateway.RateLimiting;

public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastTicks;
        public long LastSeenTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _nextSweepTicks;

    public int BucketCount => _buckets.Count;

    public ValueTask<RateLimitDecision> AcquireAsync(string key, int capacity, double refillPerSecond, CancellationToken ct)
    {
        var now = time.GetTimestamp();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = capacity, LastTicks = now });

        RateLimitDecision decision;
        lock (bucket)
        {
            var elapsedSeconds = Math.Max(0, time.GetElapsedTime(bucket.LastTicks, now).TotalSeconds);
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsedSeconds * refillPerSecond);
            bucket.LastTicks = now;
            bucket.LastSeenTicks = now;

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                decision = new RateLimitDecision(true, capacity, (int)bucket.Tokens, TimeSpan.Zero);
            }
            else
            {
                var wait = TimeSpan.FromSeconds((1 - bucket.Tokens) / refillPerSecond);
                decision = new RateLimitDecision(false, capacity, 0, wait);
            }
        }

        Sweep(now);
        return ValueTask.FromResult(decision);
    }

    private void Sweep(long now)
    {
        var next = Volatile.Read(ref _nextSweepTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextSweepTicks, now + time.TimestampFrequency * 60, next) != next)
        {
            return;
        }

        var idle = time.TimestampFrequency * 600;
        foreach (var (key, bucket) in _buckets)
        {
            if (now - Volatile.Read(ref bucket.LastSeenTicks) > idle)
            {
                _buckets.TryRemove(key, out _);
            }
        }
    }
}
