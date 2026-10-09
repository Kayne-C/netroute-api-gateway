using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NetRoute.Gateway.Config;
using Yarp.ReverseProxy.Model;

namespace NetRoute.Gateway.RateLimiting;

public sealed class RateLimitMiddleware(
    RequestDelegate next,
    IRateLimiter limiter,
    StateStore store,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<RateLimitMiddleware> logger)
{
    public const string PolicyMetadataKey = "RateLimitPolicy";
    public const string NoLimit = "none";

    private static readonly RateLimitPolicy Fallback = new() { Name = "builtin", Capacity = 100, RefillPerSecond = 100 };

    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>();
        if (route is null)
        {
            await next(context);
            return;
        }

        var policyName = route.Config.Metadata is { } meta && meta.TryGetValue(PolicyMetadataKey, out var named)
            ? named
            : options.CurrentValue.DefaultRateLimitPolicy;

        if (string.Equals(policyName, NoLimit, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!store.TryGetPolicy(policyName, out var policy))
        {
            logger.LogWarning("Rate-limit policy '{Policy}' (route {Route}) not found; using built-in default", policyName, route.Config.RouteId);
            policy = Fallback;
        }

        var tier = context.User.FindFirstValue("tier");
        var limit = policy.Resolve(tier);
        var key = $"{policy.Name}:{ResolveSubject(context)}";

        var decision = await limiter.AcquireAsync(key, limit.Capacity, limit.RefillPerSecond, context.RequestAborted);
        GatewayMetrics.Decisions.Add(
            1,
            new KeyValuePair<string, object?>("policy", policy.Name),
            new KeyValuePair<string, object?>("outcome", decision.Allowed ? "allowed" : "limited"),
            new KeyValuePair<string, object?>("degraded", decision.Degraded));

        var headers = context.Response.Headers;
        headers["RateLimit-Limit"] = decision.Limit.ToString();
        headers["RateLimit-Remaining"] = decision.Remaining.ToString();

        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        var retrySeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        headers["Retry-After"] = retrySeconds.ToString();
        headers["RateLimit-Reset"] = retrySeconds.ToString();
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Rate limit exceeded",
            detail: $"Policy '{policy.Name}' allows {limit.Capacity} burst / {limit.RefillPerSecond:0.##} per second.")
            .ExecuteAsync(context);
    }

    internal static string ResolveSubject(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue("sub") is { Length: > 0 } sub)
        {
            return "u:" + sub;
        }

        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) && apiKey.ToString() is { Length: > 0 } raw)
        {
            return "k:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
