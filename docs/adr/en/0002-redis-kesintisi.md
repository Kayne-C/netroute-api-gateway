# ADR 0002 — Behaviour during a Redis outage: a breaker and a selectable mode

**Status:** Accepted

## Context

The rate limiter depends on Redis. If Redis slows down or goes away, every request waits for the timeout and the gateway itself becomes the outage.

## Decision

- A hard ceiling for the Redis call: `CommandTimeoutMs` (default 50 ms).
- On failure a **breaker opens** (`BreakerCooldownSeconds`, default 5 s): for that period Redis is not contacted and decisions are made locally. Test: 20 requests against a dead store make a single attempt.
- The mode is selectable (`OnFailure`):
  - `LocalFallback` (default): in-memory limits per node. Protection continues, but the effective total limit becomes `N × limit`.
  - `FailOpen`: admit everything (availability over protection).
  - `FailClosed`: reject everything (protection over availability).
- Degraded decisions are recorded in the `netroute.ratelimit.fallbacks` metric and a `degraded` tag; a warning is logged only on the transition (no log flood).

## Consequences

The default mode is not "right", it is a **deliberate trade-off**; which mode is correct depends on the workload (`FailClosed` for a payments API, `FailOpen` for public content).
