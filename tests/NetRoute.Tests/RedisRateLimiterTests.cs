using NetRoute.Gateway.RateLimiting;
using Xunit;

namespace NetRoute.Tests;

[Collection("redis")]
public class RedisRateLimiterTests(RedisFixture redis)
{
    [Fact]
    public async Task Two_gateway_nodes_share_one_bucket_and_never_overshoot()
    {
        using var nodeA = redis.Connect();
        using var nodeB = redis.Connect();
        var a = new RedisRateLimiter(nodeA, "t1:");
        var b = new RedisRateLimiter(nodeB, "t1:");

        var tasks = Enumerable.Range(0, 4000).Select(i => Task.Run(async () =>
            (await (i % 2 == 0 ? a : b).AcquireAsync("client", 200, 0.001, default)).Allowed));
        var admitted = (await Task.WhenAll(tasks)).Count(x => x);

        Assert.Equal(200, admitted);
    }

    [Fact]
    public async Task Refills_using_the_server_clock()
    {
        using var conn = redis.Connect();
        var limiter = new RedisRateLimiter(conn, "t2:");
        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync("k", 5, 10, default);
        }

        Assert.False((await limiter.AcquireAsync("k", 5, 10, default)).Allowed);

        await Task.Delay(600);
        Assert.True((await limiter.AcquireAsync("k", 5, 10, default)).Allowed);
    }

    [Fact]
    public async Task Rejection_carries_retry_after()
    {
        using var conn = redis.Connect();
        var limiter = new RedisRateLimiter(conn, "t3:");
        await limiter.AcquireAsync("k", 1, 2, default);

        var denied = await limiter.AcquireAsync("k", 1, 2, default);

        Assert.False(denied.Allowed);
        Assert.InRange(denied.RetryAfter.TotalMilliseconds, 1, 500);
    }

    [Fact]
    public async Task Idle_keys_expire_so_redis_does_not_grow_forever()
    {
        using var conn = redis.Connect();
        var limiter = new RedisRateLimiter(conn, "t4:");
        await limiter.AcquireAsync("k", 5, 100, default);

        var ttl = await conn.GetDatabase().KeyTimeToLiveAsync("t4:k");

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalMilliseconds, 500, 1100);
    }

    [Fact]
    public async Task Resilient_limiter_degrades_when_redis_is_unreachable()
    {
        using var dead = StackExchange.Redis.ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=100,asyncTimeout=100");
        var time = TimeProvider.System;
        var limiter = new ResilientRateLimiter(
            new RedisRateLimiter(dead, "x:"),
            new InMemoryRateLimiter(time),
            FailureMode.LocalFallback,
            TimeSpan.FromSeconds(5),
            time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilientRateLimiter>.Instance);

        var d = await limiter.AcquireAsync("k", 2, 1, default);

        Assert.True(d.Allowed);
        Assert.True(d.Degraded);
    }
}
