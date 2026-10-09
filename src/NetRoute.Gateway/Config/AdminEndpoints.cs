using NetRoute.Gateway.RateLimiting;
using NetRoute.Gateway.Security;
using Yarp.ReverseProxy.Configuration;

namespace NetRoute.Gateway.Config;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(SecurityExtensions.AdminPolicy);

        admin.MapGet("/state", (StateStore store) => store.Current);

        admin.MapPut("/routes/{id}", async (string id, RouteConfig route, StateStore store, IConfigValidator validator) =>
        {
            route = route with { RouteId = id };
            var errors = (await validator.ValidateRouteAsync(route)).Select(e => e.Message).ToList();
            if (route.ClusterId is null || store.Current.Clusters.All(c => c.ClusterId != route.ClusterId))
            {
                errors.Add($"Unknown cluster '{route.ClusterId}'.");
            }

            if (route.Metadata is { } meta
                && meta.TryGetValue(RateLimitMiddleware.PolicyMetadataKey, out var policy)
                && !string.Equals(policy, RateLimitMiddleware.NoLimit, StringComparison.OrdinalIgnoreCase)
                && !store.TryGetPolicy(policy, out _))
            {
                errors.Add($"Unknown rate-limit policy '{policy}'.");
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["route"] = [.. errors] });
            }

            store.Update(s => s with { Routes = Upsert(s.Routes, route, r => r.RouteId) });
            return Results.Ok(route);
        });

        admin.MapDelete("/routes/{id}", (string id, StateStore store) =>
        {
            if (store.Current.Routes.All(r => r.RouteId != id))
            {
                return Results.NotFound();
            }

            store.Update(s => s with { Routes = [.. s.Routes.Where(r => r.RouteId != id)] });
            return Results.NoContent();
        });

        admin.MapPut("/clusters/{id}", async (string id, ClusterConfig cluster, StateStore store, IConfigValidator validator) =>
        {
            cluster = cluster with { ClusterId = id };
            var errors = (await validator.ValidateClusterAsync(cluster)).Select(e => e.Message).ToList();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cluster"] = [.. errors] });
            }

            store.Update(s => s with { Clusters = Upsert(s.Clusters, cluster, c => c.ClusterId) });
            return Results.Ok(cluster);
        });

        admin.MapDelete("/clusters/{id}", (string id, StateStore store) =>
        {
            if (store.Current.Clusters.All(c => c.ClusterId != id))
            {
                return Results.NotFound();
            }

            if (store.Current.Routes.Any(r => r.ClusterId == id))
            {
                return Results.Conflict(new { error = $"Cluster '{id}' is still referenced by a route." });
            }

            store.Update(s => s with { Clusters = [.. s.Clusters.Where(c => c.ClusterId != id)] });
            return Results.NoContent();
        });

        admin.MapPut("/rate-limit-policies/{name}", (string name, RateLimitPolicy policy, StateStore store) =>
        {
            policy = policy with { Name = name };
            var errors = policy.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = [.. errors] });
            }

            store.Update(s => s with { RateLimitPolicies = Upsert(s.RateLimitPolicies, policy, p => p.Name) });
            return Results.Ok(policy);
        });

        admin.MapDelete("/rate-limit-policies/{name}", (string name, StateStore store, Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions> options) =>
        {
            if (string.Equals(name, options.CurrentValue.DefaultRateLimitPolicy, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = "The default policy cannot be deleted." });
            }

            if (store.Current.Routes.Any(r => r.Metadata?.GetValueOrDefault(RateLimitMiddleware.PolicyMetadataKey) == name))
            {
                return Results.Conflict(new { error = $"Policy '{name}' is still referenced by a route." });
            }

            store.Update(s => s with { RateLimitPolicies = [.. s.RateLimitPolicies.Where(p => p.Name != name)] });
            return Results.NoContent();
        });

        return app;
    }

    private static IReadOnlyList<T> Upsert<T>(IReadOnlyList<T> items, T item, Func<T, string?> id)
    {
        var key = id(item);
        return [.. items.Where(i => id(i) != key), item];
    }
}
