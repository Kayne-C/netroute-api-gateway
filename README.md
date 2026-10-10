# NetRoute — YARP API Gateway and Distributed Rate Limiting Engine

**English** · [Türkçe](README.tr.md)

An API gateway for microservices built on **.NET 10 + YARP**. One entry point for routing, JWT validation, **dynamic** Redis-backed rate limiting and load balancing.
Routes and rate-limit policies change at runtime, without a restart.

## What it does

| Area | Approach | Evidence |
|---|---|---|
| **Central routing** | YARP; routes, clusters and policies live in one state store (`StateStore`) and are applied live through a custom `IProxyConfigProvider` | Test: a route added through the admin API is live without a restart, and returns 404 once deleted |
| **Distributed rate limiting** | Token bucket as a **single Lua script** in Redis, atomic; the clock is Redis `TIME`, so node clock skew does not matter | Test: two gateway nodes, 4,000 concurrent requests against a 200-token bucket → **exactly 200** admitted (no overshoot) |
| **Dynamic policy** | `PUT /admin/rate-limit-policies/{name}` takes effect on the next request; tiered limits (free/premium) from the JWT `tier` claim | Test: a blocked client recovers once the limit is raised |
| **Redis outage** | 50 ms timeout, circuit breaker, selectable `LocalFallback` / `FailOpen` / `FailClosed`; requests **do not fail**, protection degrades | Test: with Redis unreachable, limits continue per node; the breaker probes a dead store once, then recovers after the cooldown |
| **JWT validation** | Validated at the edge (signature, lifetime, issuer/audience). Identity reaches backends as `X-User-*` headers; same-named headers sent by clients are **stripped** | Test: forged or expired tokens get 401; an `X-User-Id: admin` spoof has no effect |
| **Closed by default** | Every route that is not explicitly marked `anonymous` requires a token; the gateway **refuses to start** without token-validation settings | Test |
| **Load balancing** | YARP `RoundRobin`, `PowerOfTwoChoices`, `LeastRequests`, …; active (`/healthz`) and passive (transport failure rate) health checks | Test: 90 requests over 3 backends → 30/30/30; **zero errors** once a dead backend is learned |
| **Limiting before authentication** | Rate limiting runs before authorization, so floods of token-less requests are throttled per IP | Test |
| **Observability** | OpenTelemetry (ASP.NET Core, `Yarp.ReverseProxy`, and `NetRoute` metrics: decisions, fallbacks, decision time), OTLP export | — |

## Quick start

```bash
docker compose up -d --build            # gateway + 2 backends + Redis
TOKEN=$(curl -s "localhost:5100/dev/token?sub=ayse&tier=premium" | jq -r .token)
curl localhost:5100/api/echo/ -H "authorization: Bearer $TOKEN"    # alternates echo-1 / echo-2
curl -i localhost:5100/public/                                      # anonymous route, RateLimit-* headers

# change a limit live
ADMIN=$(curl -s "localhost:5100/dev/token?sub=root&role=admin" | jq -r .token)
curl -X PUT localhost:5100/admin/rate-limit-policies/public -H "authorization: Bearer $ADMIN" \
     -H 'content-type: application/json' -d '{"capacity":5,"refillPerSecond":1}'
```

Without Docker: `dotnet run --project src/NetRoute.Gateway` (an in-memory single-node limiter when Redis is not configured). Two backends:
`INSTANCE_ID=echo-1 ASPNETCORE_URLS=http://127.0.0.1:5201 dotnet run --project src/NetRoute.SampleService`.

## Admin API (requires the `admin` role)

| | |
|---|---|
| `GET /admin/state` | Routes, clusters, policies |
| `PUT/DELETE /admin/routes/{id}` | Validated by YARP's own validator; unknown cluster or policy → 400 |
| `PUT/DELETE /admin/clusters/{id}` | A cluster still used by a route cannot be deleted (409) |
| `PUT/DELETE /admin/rate-limit-policies/{name}` | The default policy cannot be deleted (409) |

A route selects a policy with `metadata.RateLimitPolicy` (`none` = unlimited). Sample configuration: [netroute.routes.json](src/NetRoute.Gateway/netroute.routes.json).

## Measurements

Same machine (loopback): gateway, 2 backends and Redis 8 (Docker), 64 concurrent closed-loop clients, 10 s, Release build:

| Scenario | rps | p50 | p95 | p99 |
|---|---|---|---|---|
| Direct to one backend | 28,237 | 2.22 ms | 3.30 ms | 3.93 ms |
| Gateway → anonymous route (Redis limiter) | 35,222 | 1.74 ms | 2.62 ms | 3.81 ms |
| Gateway → JWT + Redis limiter, 2 backends | 35,220 | 1.75 ms | 2.56 ms | 3.22 ms |

**This table does not show that the gateway is "faster".** The client, gateway and backends share one CPU, and the gateway spreads load over two backends, which is why it
beats a single backend. The only meaningful reading: with the Redis limiter and JWT validation enabled, one machine sustains ~35k requests/s at p99 < 4 ms, so the gateway is not the bottleneck.
It was not measured over a real network, on separate machines, or against real backend latency. Tool: [bench/NetRoute.LoadTest](bench/NetRoute.LoadTest).

## Tests

```bash
dotnet test --solution NetRoute.slnx    # the Redis tests need Docker (Testcontainers)
```

43 tests: token bucket (limits, refill, concurrency, memory cleanup), the Redis Lua script (two nodes, TTL, outage), breaker and fallback modes, JWT and security,
end-to-end routing, limiting, load balancing, failover and the admin API. Without Docker the Redis tests **fail loudly instead of being skipped**.

## Architecture

```
client ─▶ [Authentication (JWT)] ─▶ [RateLimitMiddleware] ─▶ [Authorization] ─▶ YARP ─▶ backends
                                          │                                         ▲
                                          ▼                                         │
                                  Redis (Lua token bucket)             StateStore ──┘ (live routes/clusters)
                                  └ outage: breaker → local limiter         ▲
                                                                     Admin API
```

Decisions: [docs/adr/en](docs/adr/en).

## Known limits (an honest list)

- Admin API changes are kept in a **per-node** local file; configuration is not synchronized between gateway nodes
  (rate-limit state is shared, routes and policies are not). In production, configuration should be distributed from a single source (Git, Redis, a config service).
- With `LocalFallback`, a Redis outage makes the effective total limit `nodes × limit`. This is a deliberate trade-off.
- JWT validation was tested with a symmetric key; it was **not** tried against a real OIDC provider (`Authority`).
- Passive health checks count only transport failures (connection/timeout); they do not eject a backend that is up but returns 5xx. There is no request retry.
- Tested against a single Redis instance; Redis Cluster and Sentinel were not tried (the script touches one key, so cluster compatibility is expected).
- `/dev/token` exists only in the `Development` environment; docker-compose deliberately runs in it.
- Performance was measured on one machine under the constraints above.
