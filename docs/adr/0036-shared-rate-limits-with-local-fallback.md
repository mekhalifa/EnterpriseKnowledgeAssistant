# ADR-0036: Rate limits shared through Redis, with a per-instance fallback

- **Status:** Accepted
- **Date:** 2026-11-17
- **Deciders:** project owner

## Context

Step 12 (ADR-0032) put per-user token buckets in front of the endpoints that cost money (ask,
search, upload). They lived in each process's memory, a limitation recorded then: with N API
instances each grants the full rate, so a user's real limit is N times the configured one.
Step 14's deployment runs **two** API instances on purpose, which makes that limitation real.

The question with a shared store is not only *where* the buckets live but *what happens when
the store is down*, because the limiter sits in front of every question a user asks.

## Decision

1. **Buckets in Redis when `ConnectionStrings:Redis` is set**, per process otherwise (laptop,
   test suite). The store is a backing service attached by configuration.
2. **`RedisRateLimiting`** (MIT, built on `System.Threading.RateLimiting`, atomic Lua token
   bucket) provides the Redis limiter; partitioning stays ours (tenant + subject, as before).
   `StackExchange.Redis` is pinned to the newest 2.x, the major the library was built against.
3. **Graceful degradation:** each partition is a `FallbackRateLimiter` that asks Redis and, if
   the connection is down or the call fails, takes a token from an in-memory bucket with the
   same settings. A disconnected multiplexer skips Redis without waiting; Redis timeouts are
   500 ms, not the 5 s default. The fallback is logged at most once a minute.
4. **Redis is on `/health/ready` as Degraded, never Unhealthy.** The instance can still serve
   without it, so it must not leave rotation; the report says why limits are looser.
5. The API starts while Redis is down (`AbortOnConnectFail=false`) and reconnects by itself.

## Consequences

### Positive

- With Redis up, a user's burst is one burst across all instances (measured in Step 14's load
  test: 10 of 40 simultaneous searches admitted across two instances).
- A Redis outage does not become an outage of the product: questions keep working, at most N
  times faster than the limit, and the monthly token budget (ADR-0032) still caps spending.

### Negative / costs we accept

- **One network round trip per limited request** (sub-millisecond on the same host).
- **Limits are looser during a Redis outage**, by a factor of the instance count.
- **A third-party library** in the request path, small but maintained by one person. It is
  wrapped by our own limiter type, so replacing it touches one file.
- Bucket state is lost when Redis restarts (no persistence configured): every user's bucket
  refills. For seconds-scale limits that is harmless.

## Alternatives considered

- **Fail closed** (refuse limited requests when Redis is down). Wins when abuse or cost is
  worse than downtime — a public, anonymous API with no budget behind it.
- **Fail open** (no limit when Redis is down). Simpler; wins only when the limit protects
  something that has another guard. Here the budget is soft, so it was not enough alone.
- **Sticky sessions** at the proxy so each user always reaches one instance. No new service,
  but uneven load and limits that move when an instance restarts.
- **A gateway that rate-limits** (Azure API Management, an ingress controller, Envoy). Wins in
  a platform that already has one; keyed on the token's subject it does the same job outside
  the application.
- **Hand-written Lua over StackExchange.Redis.** No dependency; ~80 lines of concurrency code
  to own and test. Wins if the library goes unmaintained.

## Revisit when

- The library stops supporting the current .NET release.
- Limits need to be different per tenant or plan (they are configuration today).
- A gateway with rate limiting enters the platform.
