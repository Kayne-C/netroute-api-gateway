# ADR 0001 — Distributed rate limiting: a token bucket in a single Redis Lua script

**Status:** Accepted

## Context

Several gateway nodes must apply one limit to the same client. If a "read → compute → write" cycle runs in application code, two nodes can read at the same time and spend the same token (a race), and the limit is exceeded.

## Decision

- Algorithm: **token bucket** (burst capacity plus a sustained refill rate). It has no fixed-window "double burst at the boundary" problem, and unlike a sliding-window log it needs O(1) memory per key.
- Read, refill and spend are one **Lua script**; Redis runs a script atomically on its single thread. Test: two nodes, 4,000 concurrent requests, 200 tokens → exactly 200 admitted.
- The script uses Redis `TIME`, so clock differences between gateway nodes cannot affect the result.
- Keys get a `PEXPIRE` (capacity/refill + 1 s): a refilled bucket is the same as a new one, so nothing is lost and Redis does not grow.
- Key: `{policy}:{subject}`; the subject is the JWT `sub`, else the SHA-256 digest of `X-Api-Key` (the raw key is never stored), else the client IP.

## Consequences

- Every limited request adds one Redis round trip (measurement in the README). That is why there is a 50 ms timeout and a breaker (ADR 0002).
- When a policy changes, existing buckets keep their tokens and are clamped to the new capacity / refill at the new rate.
