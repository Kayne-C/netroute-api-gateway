using OpenTelemetry;
using Yarp.ReverseProxy.Transforms;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRoute.Gateway.Config;
using NetRoute.Gateway.RateLimiting;
using NetRoute.Gateway.Security;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;

namespace NetRoute.Gateway;

public static class GatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var gateway = builder.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
        builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddGatewaySecurity(builder.Configuration, builder.Environment);
        AddRateLimiting(builder);

        var seed = Path.IsPathRooted(gateway.ConfigFile) ? gateway.ConfigFile : Path.Combine(AppContext.BaseDirectory, gateway.ConfigFile);
        builder.Services.AddSingleton(StateStore.Load(seed, gateway.StatePath));
        builder.Services.AddSingleton<IProxyConfigProvider, DynamicProxyConfigProvider>();
        builder.Services.AddReverseProxy()
            .AddTransforms(ctx => ctx.AddRequestTransform(SecurityExtensions.StampIdentity));

        builder.Services.AddHealthChecks();
        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddAspNetCoreInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddMeter(GatewayMetrics.Meter.Name, "Yarp.ReverseProxy"));
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        var app = builder.Build();

        if (gateway.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
        }

        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<RateLimitMiddleware>();
        app.UseAuthorization();

        app.MapHealthChecks("/healthz").AllowAnonymous();
        app.MapAdminApi();
        if (app.Environment.IsDevelopment())
        {
            MapDevToken(app);
        }

        app.MapReverseProxy();
        return app;
    }

    private static void AddRateLimiting(WebApplicationBuilder builder)
    {
        var redisOptions = builder.Configuration.GetSection(RedisLimiterOptions.Section).Get<RedisLimiterOptions>() ?? new RedisLimiterOptions();
        builder.Services.AddSingleton<InMemoryRateLimiter>();

        if (string.IsNullOrWhiteSpace(redisOptions.ConnectionString))
        {
            builder.Services.AddSingleton<IRateLimiter>(sp => sp.GetRequiredService<InMemoryRateLimiter>());
            return;
        }

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisOptions.ConnectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = redisOptions.ConnectTimeoutMs;
            options.AsyncTimeout = redisOptions.CommandTimeoutMs;
            options.SyncTimeout = redisOptions.CommandTimeoutMs;
            return ConnectionMultiplexer.Connect(options);
        });

        builder.Services.AddSingleton<IRateLimiter>(sp => new ResilientRateLimiter(
            new RedisRateLimiter(sp.GetRequiredService<IConnectionMultiplexer>(), redisOptions.KeyPrefix),
            sp.GetRequiredService<InMemoryRateLimiter>(),
            redisOptions.OnFailure,
            TimeSpan.FromSeconds(redisOptions.BreakerCooldownSeconds),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ResilientRateLimiter>>()));
    }

    private static void MapDevToken(WebApplication app)
    {
        app.MapGet("/dev/token", (string sub, string? tier, string? role, IConfiguration config) =>
        {
            var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>();
            if (string.IsNullOrWhiteSpace(jwt?.SigningKey))
            {
                return Results.Problem("No SigningKey configured.");
            }

            var claims = new List<Claim> { new("sub", sub) };
            if (tier is not null)
            {
                claims.Add(new Claim("tier", tier));
            }

            foreach (var r in (role ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("role", r));
            }

            var token = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false }.WriteToken(new JwtSecurityToken(
                jwt.Issuer,
                jwt.Audience,
                claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256)));
            return Results.Ok(new { token });
        }).AllowAnonymous();
    }
}
