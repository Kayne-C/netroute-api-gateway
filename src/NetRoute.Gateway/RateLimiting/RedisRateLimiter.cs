using StackExchange.Redis;

namespace NetRoute.Gateway.RateLimiting;

public sealed class RedisRateLimiter(IConnectionMultiplexer redis, string keyPrefix) : IRateLimiter
{
    private const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local capacity = tonumber(@capacity)
        local refill = tonumber(@refill)
        local data = redis.call('HMGET', @key, 'tokens', 'ts')
        local tokens = tonumber(data[1])
        local ts = tonumber(data[2])
        if tokens == nil then
          tokens = capacity
          ts = now
        end
        tokens = math.min(capacity, tokens + math.max(0, now - ts) * refill / 1000)
        local allowed = 0
        local retry = 0
        if tokens >= 1 then
          tokens = tokens - 1
          allowed = 1
        else
          retry = math.ceil((1 - tokens) / refill * 1000)
        end
        redis.call('HSET', @key, 'tokens', tostring(tokens), 'ts', now)
        redis.call('PEXPIRE', @key, math.ceil(capacity / refill * 1000) + 1000)
        return {allowed, math.floor(tokens), retry}
        """;

    private static readonly LuaScript Prepared = LuaScript.Prepare(Script);

    public async ValueTask<RateLimitDecision> AcquireAsync(string key, int capacity, double refillPerSecond, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var result = (RedisResult[])(await Prepared.EvaluateAsync(
            db,
            new { key = (RedisKey)(keyPrefix + key), capacity, refill = refillPerSecond }))!;

        var allowed = (long)result[0] == 1;
        return new RateLimitDecision(allowed, capacity, (int)(long)result[1], TimeSpan.FromMilliseconds((long)result[2]));
    }
}
