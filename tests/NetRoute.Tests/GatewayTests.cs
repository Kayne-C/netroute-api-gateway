using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using NetRoute.Gateway.Config;
using NetRoute.Gateway.RateLimiting;
using Xunit;

namespace NetRoute.Tests;

public class GatewaySecurityTests
{
    private static async Task<(TestGateway Gateway, Backend Backend)> StartAsync()
    {
        var backend = await Backend.StartAsync("b1");
        var gateway = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes =
            [
                TestGateway.Route("secure", "c", "/api", "default"),
                TestGateway.Route("open", "c", "/public", "default", anonymous: true),
            ],
            RateLimitPolicies = [TestGateway.Policy("default", 1000, 1000)],
        });
        return (gateway, backend);
    }

    [Fact]
    public async Task Missing_token_is_rejected_valid_token_is_proxied()
    {
        var (gw, backend) = await StartAsync();
        await using var _ = gw;
        await using var __ = backend;

        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.SendAsync("/api/x")).StatusCode);

        var ok = await gw.SendAsync("/api/x", Tokens.Create("alice", tier: "premium"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var reply = await TestGateway.ReadAsync(ok);
        Assert.Equal("/x", reply.Path);
        Assert.Equal("alice", reply.UserId);
        Assert.Equal("premium", reply.Tier);
    }

    [Fact]
    public async Task Tokens_with_wrong_key_or_expired_are_rejected()
    {
        var (gw, backend) = await StartAsync();
        await using var _ = gw;
        await using var __ = backend;

        var forged = Tokens.Create("mallory", key: "another-key-another-key-another-key-0000");
        var expired = Tokens.Create("alice", lifetime: TimeSpan.FromMinutes(-5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.SendAsync("/api/x", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.SendAsync("/api/x", expired)).StatusCode);
    }

    [Fact]
    public async Task Client_supplied_identity_headers_cannot_impersonate_a_user()
    {
        var (gw, backend) = await StartAsync();
        await using var _ = gw;
        await using var __ = backend;

        using var request = gw.Get("/api/x", Tokens.Create("alice"));
        request.Headers.Add("X-User-Id", "admin");
        request.Headers.Add("X-User-Tier", "premium");
        var reply = await TestGateway.ReadAsync(await gw.Client.SendAsync(request));

        Assert.Equal("alice", reply.UserId);
        Assert.Equal("", reply.Tier);
    }

    [Fact]
    public async Task Anonymous_route_needs_no_token_and_gets_no_identity()
    {
        var (gw, backend) = await StartAsync();
        await using var _ = gw;
        await using var __ = backend;

        using var request = gw.Get("/public/x");
        request.Headers.Add("X-User-Id", "admin");
        var response = await gw.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", (await TestGateway.ReadAsync(response)).UserId);
    }

    [Fact]
    public async Task Gateway_refuses_to_start_without_any_token_validation_setup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var app = NetRoute.Gateway.GatewayHost.Build(["--environment", "Development"], b =>
            {
                b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["NetRoute:Jwt:SigningKey"] = "", ["NetRoute:Jwt:Authority"] = "" });
            });
            await app.DisposeAsync();
        });

        Assert.Contains("Authority", ex.Message);
    }
}

public class GatewayRateLimitTests
{
    [Fact]
    public async Task Exhausted_bucket_returns_429_with_retry_after_and_ratelimit_headers()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "tight")],
            RateLimitPolicies = [TestGateway.Policy("tight", 5, 0.5)],
        });
        var token = Tokens.Create("alice");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 8; i++)
        {
            last = await gw.SendAsync("/api/x", token);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal("5", last!.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("0", last.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.InRange(int.Parse(last.Headers.GetValues("Retry-After").Single()), 1, 3);
        Assert.Equal(5, backend.Hits);
    }

    [Fact]
    public async Task Limits_are_per_caller_not_global()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "tight")],
            RateLimitPolicies = [TestGateway.Policy("tight", 2, 0.01)],
        });
        var alice = Tokens.Create("alice");
        var bob = Tokens.Create("bob");

        await gw.SendAsync("/api/x", alice);
        await gw.SendAsync("/api/x", alice);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await gw.SendAsync("/api/x", alice)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gw.SendAsync("/api/x", bob)).StatusCode);
    }

    [Fact]
    public async Task Tier_claim_selects_a_bigger_bucket()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "tiered")],
            RateLimitPolicies = [TestGateway.Policy("tiered", 3, 0.01, new Dictionary<string, TierLimit> { ["premium"] = new(10, 0.01) })],
        });

        var free = 0;
        var premium = 0;
        for (var i = 0; i < 12; i++)
        {
            free += (await gw.SendAsync("/api/x", Tokens.Create("free-user"))).IsSuccessStatusCode ? 1 : 0;
            premium += (await gw.SendAsync("/api/x", Tokens.Create("vip", tier: "premium"))).IsSuccessStatusCode ? 1 : 0;
        }

        Assert.Equal(3, free);
        Assert.Equal(10, premium);
    }

    [Fact]
    public async Task Unauthenticated_floods_are_throttled_before_they_cost_anything()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "tight")],
            RateLimitPolicies = [TestGateway.Policy("tight", 3, 0.01)],
        });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await gw.SendAsync("/api/x")).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Route_opts_out_with_policy_none()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "none")],
        });
        var token = Tokens.Create("alice");

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await gw.SendAsync("/api/x", token)).StatusCode);
        }
    }

    [Fact]
    public async Task Two_gateway_nodes_with_shared_redis_enforce_one_global_limit()
    {
        await using var redis = new RedisFixture();
        await redis.InitializeAsync();
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        var state = new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
            Routes = [TestGateway.Route("r", "c", "/api", "global")],
            RateLimitPolicies = [TestGateway.Policy("global", 20, 0.01)],
        };
        await using var node1 = await TestGateway.StartAsync(state, redis.ConnectionString);
        await using var node2 = await TestGateway.StartAsync(state, redis.ConnectionString);
        var token = Tokens.Create("alice");

        var admitted = 0;
        for (var i = 0; i < 60; i++)
        {
            var node = i % 2 == 0 ? node1 : node2;
            admitted += (await node.SendAsync("/api/x", token)).IsSuccessStatusCode ? 1 : 0;
        }

        Assert.Equal(20, admitted);
    }

    [Fact]
    public async Task Redis_outage_degrades_to_local_limits_instead_of_failing_requests()
    {
        var backend = await Backend.StartAsync("b1");
        await using var __ = backend;
        await using var gw = await TestGateway.StartAsync(
            new GatewayState
            {
                Clusters = [TestGateway.Cluster("c", "RoundRobin", backend)],
                Routes = [TestGateway.Route("r", "c", "/api", "p")],
                RateLimitPolicies = [TestGateway.Policy("p", 4, 0.01)],
            },
            redis: "127.0.0.1:1,abortConnect=false,connectTimeout=200");
        var token = Tokens.Create("alice");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await gw.SendAsync("/api/x", token)).StatusCode);
        }

        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}

public class GatewayRoutingTests
{
    [Fact]
    public async Task Round_robin_spreads_load_evenly()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var b2 = await Backend.StartAsync("b2");
        await using var b3 = await Backend.StartAsync("b3");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1, b2, b3)],
            Routes = [TestGateway.Route("r", "c", "/api", "none")],
        });
        var token = Tokens.Create("alice");

        for (var i = 0; i < 90; i++)
        {
            Assert.True((await gw.SendAsync("/api/x", token)).IsSuccessStatusCode);
        }

        Assert.Equal([30, 30, 30], [b1.Hits, b2.Hits, b3.Hits]);
    }

    [Fact]
    public async Task Passive_health_ejects_a_dead_destination_so_clients_stop_seeing_errors()
    {
        await using var b1 = await Backend.StartAsync("b1");
        var b2 = await Backend.StartAsync("b2");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1, b2)],
            Routes = [TestGateway.Route("r", "c", "/api", "none")],
        });
        var token = Tokens.Create("alice");

        await b2.DisposeAsync();

        var failuresWhileLearning = 0;
        for (var i = 0; i < 40; i++)
        {
            failuresWhileLearning += (await gw.SendAsync("/api/x", token)).IsSuccessStatusCode ? 0 : 1;
        }

        var failuresAfter = 0;
        for (var i = 0; i < 40; i++)
        {
            failuresAfter += (await gw.SendAsync("/api/x", token)).IsSuccessStatusCode ? 0 : 1;
        }

        Assert.InRange(failuresWhileLearning, 1, 20);
        Assert.Equal(0, failuresAfter);
    }

    [Fact]
    public async Task Unknown_path_is_404_not_a_leak_of_internal_topology()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            Routes = [TestGateway.Route("r", "c", "/api", "none")],
        });

        var response = await gw.SendAsync("/nope", Tokens.Create("alice"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("127.0.0.1", await response.Content.ReadAsStringAsync());
    }
}

public class GatewayAdminTests
{
    private static string Admin => Tokens.Create("root", role: "admin");

    private static HttpRequestMessage Put<T>(string url, T body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: GatewayState.Json) };
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Admin_api_requires_the_admin_role()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState { Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)] });

        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.SendAsync("/admin/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gw.SendAsync("/admin/state", Tokens.Create("alice", role: "user"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gw.SendAsync("/admin/state", Admin)).StatusCode);
    }

    [Fact]
    public async Task New_route_is_live_without_a_restart()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            RateLimitPolicies = [TestGateway.Policy("default", 100, 100)],
        });
        var token = Tokens.Create("alice");
        Assert.Equal(HttpStatusCode.NotFound, (await gw.SendAsync("/orders/1", token)).StatusCode);

        using var put = Put("/admin/routes/orders", TestGateway.Route("ignored", "c", "/orders", "default"), Admin);
        Assert.Equal(HttpStatusCode.OK, (await gw.Client.SendAsync(put)).StatusCode);

        var live = await RetryUntilAsync(() => gw.SendAsync("/orders/1", token), r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal("/1", (await TestGateway.ReadAsync(live)).Path);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/admin/routes/orders");
        delete.Headers.Authorization = new("Bearer", Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await gw.Client.SendAsync(delete)).StatusCode);
        await RetryUntilAsync(() => gw.SendAsync("/orders/1", token), r => r.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Raising_a_policy_limit_applies_to_the_next_request()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            Routes = [TestGateway.Route("r", "c", "/api", "tight")],
            RateLimitPolicies = [TestGateway.Policy("tight", 2, 0.01)],
        });
        var token = Tokens.Create("alice");
        await gw.SendAsync("/api/x", token);
        await gw.SendAsync("/api/x", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await gw.SendAsync("/api/x", token)).StatusCode);

        using var put = Put("/admin/rate-limit-policies/tight", TestGateway.Policy("tight", 1000, 1000), Admin);
        Assert.Equal(HttpStatusCode.OK, (await gw.Client.SendAsync(put)).StatusCode);

        var recovered = await RetryUntilAsync(() => gw.SendAsync("/api/x", token), r => r.IsSuccessStatusCode);
        Assert.True(recovered.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Invalid_changes_are_rejected_and_do_not_touch_the_running_config()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            Routes = [TestGateway.Route("r", "c", "/api", "none")],
            RateLimitPolicies = [TestGateway.Policy("default", 10, 10)],
        });

        using var unknownCluster = Put("/admin/routes/bad", TestGateway.Route("bad", "ghost", "/bad", "none"), Admin);
        using var unknownPolicy = Put("/admin/routes/bad2", TestGateway.Route("bad2", "c", "/bad2", "ghost"), Admin);
        using var badPolicy = Put("/admin/rate-limit-policies/neg", TestGateway.Policy("neg", 0, -1), Admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await gw.Client.SendAsync(unknownCluster)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await gw.Client.SendAsync(unknownPolicy)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await gw.Client.SendAsync(badPolicy)).StatusCode);
        Assert.Single(gw.Store.Current.Routes);
        Assert.Single(gw.Store.Current.RateLimitPolicies);
    }

    [Fact]
    public async Task Policy_body_does_not_need_to_repeat_the_name_from_the_url()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState { Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)] });

        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/rate-limit-policies/fresh")
        {
            Content = new StringContent("""{"capacity":50,"refillPerSecond":5,"tiers":{"gold":{"capacity":500,"refillPerSecond":50}}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", Admin);

        Assert.Equal(HttpStatusCode.OK, (await gw.Client.SendAsync(request)).StatusCode);
        Assert.True(gw.Store.TryGetPolicy("fresh", out var policy));
        Assert.Equal(new TierLimit(500, 50), policy.Resolve("gold"));
    }

    [Fact]
    public async Task Referenced_cluster_and_default_policy_cannot_be_deleted()
    {
        await using var b1 = await Backend.StartAsync("b1");
        await using var gw = await TestGateway.StartAsync(new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            Routes = [TestGateway.Route("r", "c", "/api", "default")],
            RateLimitPolicies = [TestGateway.Policy("default", 10, 10)],
        });

        using var cluster = new HttpRequestMessage(HttpMethod.Delete, "/admin/clusters/c");
        cluster.Headers.Authorization = new("Bearer", Admin);
        using var policy = new HttpRequestMessage(HttpMethod.Delete, "/admin/rate-limit-policies/default");
        policy.Headers.Authorization = new("Bearer", Admin);

        Assert.Equal(HttpStatusCode.Conflict, (await gw.Client.SendAsync(cluster)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await gw.Client.SendAsync(policy)).StatusCode);
    }

    [Fact]
    public async Task Admin_changes_survive_a_restart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"netroute-state-{Guid.NewGuid():N}.json");
        await using var b1 = await Backend.StartAsync("b1");
        var seed = new GatewayState
        {
            Clusters = [TestGateway.Cluster("c", "RoundRobin", b1)],
            RateLimitPolicies = [TestGateway.Policy("default", 100, 100)],
        };

        await using (var first = await TestGateway.StartAsync(seed, statePath: statePath))
        {
            using var put = Put("/admin/routes/persisted", TestGateway.Route("persisted", "c", "/p", "default"), Admin);
            Assert.Equal(HttpStatusCode.OK, (await first.Client.SendAsync(put)).StatusCode);
        }

        await using var second = await TestGateway.StartAsync(seed, statePath: statePath);
        Assert.Contains(second.Store.Current.Routes, r => r.RouteId == "persisted");
        File.Delete(statePath);
    }

    private static async Task<HttpResponseMessage> RetryUntilAsync(Func<Task<HttpResponseMessage>> call, Func<HttpResponseMessage, bool> done)
    {
        HttpResponseMessage response = default!;
        for (var i = 0; i < 40; i++)
        {
            response = await call();
            if (done(response))
            {
                return response;
            }

            await Task.Delay(50);
        }

        return response;
    }
}
