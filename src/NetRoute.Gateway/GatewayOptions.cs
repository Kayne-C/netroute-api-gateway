using NetRoute.Gateway.RateLimiting;

namespace NetRoute.Gateway;

public sealed class GatewayOptions
{
    public const string Section = "NetRoute";

    public string ConfigFile { get; set; } = "netroute.routes.json";

    public string? StatePath { get; set; }

    public string DefaultRateLimitPolicy { get; set; } = "default";

    public bool TrustForwardedHeaders { get; set; }
}

public sealed class RedisLimiterOptions
{
    public const string Section = "NetRoute:RateLimit:Redis";

    public string? ConnectionString { get; set; }

    public string KeyPrefix { get; set; } = "nr:rl:";

    public int CommandTimeoutMs { get; set; } = 50;

    public int ConnectTimeoutMs { get; set; } = 2000;

    public FailureMode OnFailure { get; set; } = FailureMode.LocalFallback;

    public int BreakerCooldownSeconds { get; set; } = 5;
}

public sealed class JwtOptions
{
    public const string Section = "NetRoute:Jwt";

    public string? Authority { get; set; }

    public string? Audience { get; set; }

    public string? Issuer { get; set; }

    public string? SigningKey { get; set; }
}
