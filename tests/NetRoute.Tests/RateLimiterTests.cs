using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NetRoute.Gateway.RateLimiting;
using StackExchange.Redis;
using Xunit;

namespace NetRoute.Tests;

public class InMemoryRateLimiterTests
{
    [Fact]
    public async Task Allows_burst_up_to_capacity_then_rejects()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());

        var results = new List<RateLimitDecision>();
        for (var i = 0; i < 12; i++)
        {
            results.Add(await limiter.AcquireAsync("k", 10, 1, default));
        }

        Assert.Equal(10, results.Count(r => r.Allowed));
        Assert.All(results.Skip(10), r => Assert.False(r.Allowed));
        Assert.Equal(0, results[^1].Remaining);
    }

    [Fact]
    public async Task Refills_at_the_configured_rate()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiter(time);
        for (var i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("k", 10, 5, default);
        }

        Assert.False((await limiter.AcquireAsync("k", 10, 5, default)).Allowed);

        time.Advance(TimeSpan.FromSeconds(1));
        var admitted = 0;
        for (var i = 0; i < 8; i++)
        {
            admitted += (await limiter.AcquireAsync("k", 10, 5, default)).Allowed ? 1 : 0;
        }

        Assert.Equal(5, admitted);
    }

    [Fact]
    public async Task Never_refills_above_capacity()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiter(time);
        await limiter.AcquireAsync("k", 3, 100, default);
        time.Advance(TimeSpan.FromHours(1));

        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            admitted += (await limiter.AcquireAsync("k", 3, 100, default)).Allowed ? 1 : 0;
        }

        Assert.Equal(3, admitted);
    }

    [Fact]
    public async Task Rejection_reports_how_long_to_wait()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());
        await limiter.AcquireAsync("k", 1, 2, default);

        var denied = await limiter.AcquireAsync("k", 1, 2, default);

        Assert.False(denied.Allowed);
        Assert.InRange(denied.RetryAfter.TotalMilliseconds, 400, 500);
    }

    [Fact]
    public async Task Keys_are_isolated()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());
        await limiter.AcquireAsync("a", 1, 1, default);

        Assert.False((await limiter.AcquireAsync("a", 1, 1, default)).Allowed);
        Assert.True((await limiter.AcquireAsync("b", 1, 1, default)).Allowed);
    }

    [Fact]
    public async Task Concurrent_callers_never_exceed_capacity()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());

        var tasks = Enumerable.Range(0, 2000).Select(_ => Task.Run(async () => (await limiter.AcquireAsync("k", 100, 1, default)).Allowed));
        var admitted = (await Task.WhenAll(tasks)).Count(a => a);

        Assert.Equal(100, admitted);
    }

    [Fact]
    public async Task Idle_buckets_are_evicted()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiter(time);
        for (var i = 0; i < 50; i++)
        {
            await limiter.AcquireAsync($"k{i}", 5, 1, default);
        }

        time.Advance(TimeSpan.FromMinutes(11));
        await limiter.AcquireAsync("fresh", 5, 1, default);

        Assert.Equal(1, limiter.BucketCount);
    }
}

public class RateLimitPolicyTests
{
    [Fact]
    public void Tier_overrides_base_limits_and_unknown_tier_falls_back()
    {
        var policy = new RateLimitPolicy
        {
            Name = "p",
            Capacity = 10,
            RefillPerSecond = 1,
            Tiers = new Dictionary<string, TierLimit> { ["premium"] = new(100, 50) },
        };

        Assert.Equal(new TierLimit(100, 50), policy.Resolve("premium"));
        Assert.Equal(new TierLimit(10, 1), policy.Resolve("unknown"));
        Assert.Equal(new TierLimit(10, 1), policy.Resolve(null));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    [InlineData(10, double.NaN)]
    public void Invalid_limits_are_rejected(int capacity, double refill)
    {
        var policy = new RateLimitPolicy { Name = "p", Capacity = capacity, RefillPerSecond = refill };

        Assert.NotEmpty(policy.Validate());
    }
}

public class ResilientRateLimiterTests
{
    private sealed class Flaky : IRateLimiter
    {
        public bool Down { get; set; }
        public int Calls { get; private set; }

        public ValueTask<RateLimitDecision> AcquireAsync(string key, int capacity, double refillPerSecond, CancellationToken ct)
        {
            Calls++;
            return Down
                ? throw new TimeoutException("boom")
                : ValueTask.FromResult(new RateLimitDecision(true, capacity, capacity - 1, TimeSpan.Zero));
        }
    }

    private static ResilientRateLimiter Create(Flaky primary, FailureMode mode, FakeTimeProvider time)
        => new(primary, new InMemoryRateLimiter(time), mode, TimeSpan.FromSeconds(5), time, NullLogger<ResilientRateLimiter>.Instance);

    [Fact]
    public async Task Healthy_store_is_used_and_not_marked_degraded()
    {
        var limiter = Create(new Flaky(), FailureMode.LocalFallback, new FakeTimeProvider());

        var d = await limiter.AcquireAsync("k", 10, 1, default);

        Assert.True(d.Allowed);
        Assert.False(d.Degraded);
    }

    [Fact]
    public async Task Local_fallback_still_enforces_limits_and_flags_degraded()
    {
        var primary = new Flaky { Down = true };
        var limiter = Create(primary, FailureMode.LocalFallback, new FakeTimeProvider());

        var decisions = new List<RateLimitDecision>();
        for (var i = 0; i < 5; i++)
        {
            decisions.Add(await limiter.AcquireAsync("k", 3, 1, default));
        }

        Assert.Equal(3, decisions.Count(d => d.Allowed));
        Assert.All(decisions, d => Assert.True(d.Degraded));
    }

    [Fact]
    public async Task Breaker_stops_hammering_a_dead_store_then_recovers_after_cooldown()
    {
        var time = new FakeTimeProvider();
        var primary = new Flaky { Down = true };
        var limiter = Create(primary, FailureMode.LocalFallback, time);

        for (var i = 0; i < 20; i++)
        {
            await limiter.AcquireAsync("k", 100, 1, default);
        }

        Assert.Equal(1, primary.Calls);
        Assert.True(limiter.BreakerOpen);

        primary.Down = false;
        time.Advance(TimeSpan.FromSeconds(6));

        var d = await limiter.AcquireAsync("k", 100, 1, default);
        Assert.False(d.Degraded);
        Assert.False(limiter.BreakerOpen);
    }

    [Fact]
    public async Task Fail_open_admits_and_fail_closed_rejects()
    {
        var open = Create(new Flaky { Down = true }, FailureMode.FailOpen, new FakeTimeProvider());
        var closed = Create(new Flaky { Down = true }, FailureMode.FailClosed, new FakeTimeProvider());

        Assert.True((await open.AcquireAsync("k", 1, 1, default)).Allowed);
        Assert.False((await closed.AcquireAsync("k", 1, 1, default)).Allowed);
    }
}
