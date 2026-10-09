using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NetRoute.Gateway;
using NetRoute.Gateway.Config;
using NetRoute.Gateway.RateLimiting;
using Yarp.ReverseProxy.Configuration;

namespace NetRoute.Tests;

public static class Tokens
{
    public const string Key = "test-signing-key-test-signing-key-123456";
    public const string Issuer = "tests";
    public const string Audience = "netroute";

    public static string Create(string sub, string? tier = null, string? role = null, string? key = null, TimeSpan? lifetime = null)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (tier is not null)
        {
            claims.Add(new Claim("tier", tier));
        }

        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }

        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(5);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            notBefore: now.AddMinutes(-10),
            expires: now.Add(life),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? Key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class Backend : IAsyncDisposable
{
    private readonly WebApplication _app;

    private Backend(WebApplication app, string name)
    {
        _app = app;
        Name = name;
        Address = app.Urls.First();
    }

    public string Name { get; }
    public string Address { get; }
    public int Hits;

    public static async Task<Backend> StartAsync(string name)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        Backend? self = null;
        app.MapGet("/healthz", () => Results.Ok("ok"));
        app.MapGet("/{**rest}", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.Hits);
            return Results.Json(new
            {
                instance = name,
                path = ctx.Request.Path.Value,
                userId = ctx.Request.Headers["X-User-Id"].ToString(),
                tier = ctx.Request.Headers["X-User-Tier"].ToString(),
            });
        });
        await app.StartAsync();
        self = new Backend(app, name);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record EchoReply(string Instance, string Path, string UserId, string Tier);

public sealed class TestGateway : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestGateway(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
        Store = app.Services.GetRequiredService<StateStore>();
    }

    public HttpClient Client { get; }
    public StateStore Store { get; }
    public string Address => Client.BaseAddress!.ToString();

    public static ClusterConfig Cluster(string id, string policy, params Backend[] backends) => new()
    {
        ClusterId = id,
        LoadBalancingPolicy = policy,
        HealthCheck = new HealthCheckConfig
        {
            Passive = new PassiveHealthCheckConfig { Enabled = true, Policy = "TransportFailureRate", ReactivationPeriod = TimeSpan.FromMinutes(5) },
        },
        Destinations = backends.ToDictionary(b => b.Name, b => new DestinationConfig { Address = b.Address }),
    };

    public static RouteConfig Route(string id, string cluster, string path, string? policy = null, bool anonymous = false) => new()
    {
        RouteId = id,
        ClusterId = cluster,
        AuthorizationPolicy = anonymous ? "anonymous" : null,
        Match = new RouteMatch { Path = path + "/{**rest}" },
        Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = path }],
        Metadata = policy is null ? null : new Dictionary<string, string> { [RateLimitMiddleware.PolicyMetadataKey] = policy },
    };

    public static RateLimitPolicy Policy(string name, int capacity, double refill, IReadOnlyDictionary<string, TierLimit>? tiers = null)
        => new() { Name = name, Capacity = capacity, RefillPerSecond = refill, Tiers = tiers };

    public static async Task<TestGateway> StartAsync(
        GatewayState state,
        string? redis = null,
        string? statePath = null,
        FailureMode failureMode = FailureMode.LocalFallback)
    {
        var seed = Path.Combine(Path.GetTempPath(), $"netroute-seed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(state, GatewayState.Json));

        var app = GatewayHost.Build(["--environment", "Development"], builder =>
        {
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRoute:ConfigFile"] = seed,
                ["NetRoute:StatePath"] = statePath,
                ["NetRoute:DefaultRateLimitPolicy"] = "default",
                ["NetRoute:Jwt:SigningKey"] = Tokens.Key,
                ["NetRoute:Jwt:Issuer"] = Tokens.Issuer,
                ["NetRoute:Jwt:Audience"] = Tokens.Audience,
                ["NetRoute:RateLimit:Redis:ConnectionString"] = redis,
                ["NetRoute:RateLimit:Redis:OnFailure"] = failureMode.ToString(),
            });
        });
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new TestGateway(app, client);
    }

    public HttpRequestMessage Get(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(string path, string? token = null)
    {
        using var request = Get(path, token);
        return await Client.SendAsync(request);
    }

    public static async Task<EchoReply> ReadAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<EchoReply>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
