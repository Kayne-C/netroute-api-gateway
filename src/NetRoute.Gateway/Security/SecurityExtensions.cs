using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace NetRoute.Gateway.Security;

public static class SecurityExtensions
{
    public const string AdminPolicy = "admin";

    public static readonly string[] IdentityHeaders = ["X-User-Id", "X-User-Tier", "X-User-Roles"];

    public static IServiceCollection AddGatewaySecurity(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.Authority) && string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            throw new InvalidOperationException(
                $"Configure {JwtOptions.Section}:Authority (OIDC) or {JwtOptions.Section}:SigningKey (development only).");
        }

        if (jwt.SigningKey is { Length: < 32 })
        {
            throw new InvalidOperationException($"{JwtOptions.Section}:SigningKey must be at least 32 characters.");
        }

        if (!env.IsDevelopment() && string.IsNullOrWhiteSpace(jwt.Authority))
        {
            throw new InvalidOperationException("A symmetric SigningKey is only allowed in Development; configure an Authority.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(jwt.Authority))
                {
                    o.Authority = jwt.Authority;
                }

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer) || !string.IsNullOrWhiteSpace(jwt.Authority),
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(jwt.Audience),
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = string.IsNullOrWhiteSpace(jwt.SigningKey) ? null : [SecurityAlgorithms.HmacSha256],
                    IssuerSigningKey = string.IsNullOrWhiteSpace(jwt.SigningKey)
                        ? null
                        : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                };
            });

        services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole("admin"));
        });

        return services;
    }

    public static ValueTask StampIdentity(Yarp.ReverseProxy.Transforms.RequestTransformContext context)
    {
        var headers = context.ProxyRequest.Headers;
        foreach (var name in IdentityHeaders)
        {
            headers.Remove(name);
        }

        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            if (user.FindFirstValue("sub") is { } sub)
            {
                headers.TryAddWithoutValidation("X-User-Id", sub);
            }

            if (user.FindFirstValue("tier") is { } tier)
            {
                headers.TryAddWithoutValidation("X-User-Tier", tier);
            }

            var roles = user.FindAll("role").Select(c => c.Value).ToArray();
            if (roles.Length > 0)
            {
                headers.TryAddWithoutValidation("X-User-Roles", string.Join(',', roles));
            }
        }

        return ValueTask.CompletedTask;
    }
}
