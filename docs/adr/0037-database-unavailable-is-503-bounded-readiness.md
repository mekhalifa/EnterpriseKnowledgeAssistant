# ADR-0037: An unreachable database is a 503, recognised structurally; every readiness check is time-bounded

- **Status:** Accepted
- **Date:** 2026-11-18
- **Deciders:** project owner

## Context

The Step 14 drills stopped SQL Server and recorded two poor signals ([step-14 §6.7](../steps/step-14-deployment.md)):

1. **Reads answered `500`.** That says "our bug; retrying won't help". Neither is true during an
   outage.
2. **`/health/ready` gave no answer within 15 s.** Compose's probe gives up after 5 s, so the
   orchestrator learned "no answer" rather than "SQL Server is down".

Measurements taken before deciding (SqlClient 6.1.7, EF Core 10.0.15; [post-01](../steps/post-01-database-outage-signals.md)):

- A failed connect costs SqlClient's **connect timeout (15 s default)**, even when the port
  refuses at once. EF does not retry these errors.
- A cancelled token stops a hung `OpenAsync` at once (2.0 s for a 2 s token).
- The same failure has **different error numbers on each platform**. A refused connection is
  `1225` on Windows and `0` on Linux. An unresolvable host is `11002`, and a connect timeout is
  `258`. `SqlException.IsTransient` is `false` for all of them.
- What every case shares: the `SqlException` wraps an operating-system network error
  (`SocketException` on Linux, `Win32Exception` on Windows and for timeouts).

## Decision

1. **`SqlServerFailures.IsUnavailable(Exception)`** lives in `EKA.Infrastructure`, the only
   assembly that knows the store is SQL Server. It walks the exception chain and returns true
   for any of these:
   - `RetryLimitExceededException` (EF exhausted its transient retries);
   - a `SqlException` whose inner exception is a `SocketException` or `Win32Exception`;
   - a `SqlException` with `IsTransient` set.

   Everything else, including a failed login and a failing query, is not "unavailable".
2. **`DatabaseUnavailableExceptionHandler`** in `EKA.Api` maps those cases to `503`, problem code
   `database.unavailable`, with a generic detail. It is registered after the bad-request and
   domain handlers, and logs at Warning with the exception. The client shows a "temporarily
   unavailable" message for that code, not its AI-outage message.
3. **Every dependency check on `/health/ready` has `DependencyInjection.ReadinessCheckTimeout`
   (5 s).** `AddDbContextCheck` has no timeout parameter, so the registration's `Timeout` is set
   through `Configure<HealthCheckServiceOptions>`. A test asserts that every ready-tagged
   dependency check carries the bound.

## Consequences

### Positive

- Clients, proxies and dashboards can tell an outage (503, retryable) from a defect (500).
- The probe answers 503 within about 5 s and names `sql-server`, inside Compose's probe timeout.
- The rule holds on both platforms, and real exceptions on both prove it (the Linux shapes were
  measured in a container; CI runs the tests on Linux).

### Negative / costs we accept

- **A request during an outage waits before its 503: 40–70 s on the Compose stack.** *Corrected
  on 2026-11-18 by the drill on the deployed stack; the first version of this ADR said "up to
  15 s".* That figure was right for a refused port, which EF does not retry. In Compose, though, a
  stopped `sqlserver` container makes its name unresolvable (error 11001). That error *is* in EF's
  transient list, so the request waits through five retries with back-off and ends in
  `RetryLimitExceededException`. Measured: 66 s and 38 s. SqlClient's pool *blocking period*
  doesn't shorten this, because each retry waits out its back-off. A circuit breaker, or a
  shorter retry budget for reads, would fix it; see below.
- **The rule is broad.** Any network-level error counts, including a misconfigured hostname, which
  is our fault but now reads as "temporarily unavailable". The readiness probe fails in both
  cases, so the misconfiguration stays visible.
- **No `Retry-After` header.** Any value would be invented, and the client reads it only on 429.
- A Qdrant outage during search is still a 500 (out of scope; same pattern would apply).
- A failure after an answer stream has started cannot change the status; it stays an `error` event.

## Alternatives considered

| Option | Wins when |
|---|---|
| A list of `SqlException` numbers (EF's transient list or our own) | Only one platform matters and every number has been measured on it. Linux reports `0` for "refused", so a list misses the commonest case here |
| A circuit breaker on database calls, failing fast while open | Request volume during an outage is high enough that 40–70 s waits exhaust threads or connections, or users notice them. On one host with a handful of users it is machinery without a measured need; the corrected wait makes this the first thing to revisit |
| A `Result` error from every repository | Callers could do something useful with "unavailable". None can; it adds a branch to every use case for an edge mapping |
| A custom readiness check opening a raw connection, bypassing EF's retry strategy | The probe should test something other than "EF can connect". The timeout already bounds retries; the maintained check also builds the EF model that warm-up relies on |
| A shorter `Connect Timeout` in the connection string | Every query, not only the probe, should give up sooner. It would also cut requests short during a slow failover, which the retry strategy exists to survive |

## Revisit when

- The deployment leaves one host, or traffic during an outage is high enough that requests pile up
  behind the connect timeout: consider a circuit breaker.
- A second store (Qdrant, a blob store) needs the same mapping: generalise into a port, rather than
  adding a second handler.
- SqlClient starts flagging connection failures as `IsTransient` consistently: the structural rule
  may become unnecessary.
