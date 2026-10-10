# ADR 0003 — Security defaults

**Status:** Accepted

- **Closed by default:** `FallbackPolicy = RequireAuthenticatedUser`. A route with a forgotten authorization setting is not left open by accident; a public route is marked explicitly with `AuthorizationPolicy: "anonymous"`.
- **Identity headers:** `X-User-Id`, `X-User-Tier` and `X-User-Roles` are written only by the gateway, from the validated token; same-named headers sent by the client are removed on every request.
  Otherwise a caller could impersonate another user toward backends that trust the gateway.
- **Order:** authentication → rate limit → authorization. Floods of rejected or anonymous requests are throttled too (per IP).
- **The gateway validates at startup:** if neither `Authority` nor `SigningKey` is set the process does not start; a symmetric key is accepted only in `Development` (at least 32 characters, HS256 only).
- The **admin API** is bound to a separate `admin` role policy; every write goes through YARP's validator and the gateway's cross-checks (unknown cluster / policy).
