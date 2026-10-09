using System.Text.Json;
using System.Text.Json.Serialization;
using NetRoute.Gateway.RateLimiting;
using Yarp.ReverseProxy.Configuration;

namespace NetRoute.Gateway.Config;

public sealed record GatewayState
{
    public IReadOnlyList<RouteConfig> Routes { get; init; } = [];
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = [];
    public IReadOnlyList<RateLimitPolicy> RateLimitPolicies { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
