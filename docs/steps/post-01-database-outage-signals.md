# Post-01: Honest signals during a database outage

**Goal:** fix the two poor signals the Step 14 drills found when SQL Server was stopped
([step-14 §6.7](step-14-deployment.md)): reads answered `500`, and `/health/ready` gave no
answer within 15 s. **Met:** reads answer `503 database.unavailable`, and readiness answers
`503` naming `sql-server` in about 5 s. Each fix is guarded by a test that was shown to fail
without it.

Decision: [ADR-0037](../adr/0037-database-unavailable-is-503-bounded-readiness.md).

This is post-MVP work, chosen with the owner as item 3 of the post-MVP list. It is not a
learning-path step.

---

## 1. What was built

| Piece | Where | What it does |
|---|---|---|
| `SqlServerFailures.IsUnavailable` | `EKA.Infrastructure/Persistence` | Decides whether an exception means "the database could not be reached" |
| `DatabaseUnavailableExceptionHandler` | `EKA.Api/Errors` | Maps that case to `503`, problem code `database.unavailable` |
| `ReadinessCheckTimeout` (5 s) | `EKA.Infrastructure/DependencyInjection` | Now bounds the SQL check as well as Qdrant's |
| `describeProblem` | `src/EKA.Web/.../problem.ts` | Shows "temporarily unavailable" for that code, not the AI-outage message |

Tests: +6 classifier tests against real failed connections, +2 end-to-end 503 tests (two read
routes), +1 test that every readiness check is time-bounded, one readiness test rewritten to
time its answer, and +1 client test.

---

## 2. Measure before fixing

The write-up in step-14 assumed the slow probe was EF's retry back-off. A 40-line throwaway
program, run on Windows and then **in a Linux .NET container** (the deployment's OS), showed
otherwise:

| Case | Windows | Linux |
|---|---|---|
| Refused port, raw `OpenAsync` | 14.7 s, `SqlException` **1225**, inner `Win32Exception` | 14.6 s, `SqlException` **0**, inner `SocketException` |
| Same through EF with `EnableRetryOnFailure` | 15.9 s, *not retried* | 15.5 s, *not retried* |
| Unresolvable host | — | 15.0 s, **11002**, inner `SocketException` |
| Hung connect, `Connect Timeout=3` | — | 3.0 s, **258**, inner `Win32Exception` |
| Hung connect, token cancelled at 2 s | 2.0 s, `TaskCanceledException` | 2.1 s, `TaskCanceledException` |
| Second attempt within seconds | 142 ms | — |

`IsTransient` was **false in every row.** Four lessons:

1. **The cost is SqlClient's connect timeout, not EF.** EF doesn't classify "refused" as
   transient, so it never retried. Even a port that refuses at once costs 15 s, because
   SqlClient keeps retrying the TCP connect within its timeout.
2. **Cancellation works.** A hung `OpenAsync` stops at the token, so a per-check `Timeout`
   genuinely shortens the probe instead of only labelling it afterwards. Without this row the
   fix could have been cosmetic.
3. **Error numbers aren't portable.** "Refused" is 1225 on Windows and 0 on Linux. A
   number list, the textbook approach, would have missed the deployment's commonest case.
4. **The pool remembers failures.** For about 5 s after a failed open, SqlClient's *blocking
   period* fails new opens immediately (142 ms). Only the first request of an outage pays the
   full timeout.

---

## 3. Fix 1: bound the readiness check

**What:** `HealthCheckRegistration.Timeout = 5 s` on the `sql-server` check, the same bound
Qdrant's check already had (now one shared constant).

**Why:** the checks run in parallel, so the probe answers within about the longest bound. 5 s
matches Compose's probe timeout. A `503` that names the failing check tells the orchestrator,
and a person reading the probe, more than a timeout does.

**How:** `AddDbContextCheck` has no timeout parameter, but its registration has the property,
so `Configure<HealthCheckServiceOptions>` sets it by name. Keeping Microsoft's check rather than
writing our own keeps the side effect warm-up relies on: the check builds the EF model.

**Alternative:** a hand-written check that opens a raw connection and skips EF's retry strategy.
It wins if the probe should test something other than "EF can connect". The timeout already
bounds the retries, so nothing needs it today.

---

## 4. Fix 2: 503 for an unreachable database

**What:** a classifier in Infrastructure, and an exception handler in the API that asks it.

**Why 503:** a `500` says "our bug; retrying won't help". During an outage the request is
fine and will succeed later. A `503` says exactly that, and clients, proxies and dashboards
act on the difference. The Angular client already treated 503 as retryable, but described it
as an AI outage. It now says "temporarily unavailable" for this code, and doesn't claim
"nothing was saved", because a write can fail after the database committed it.

**Why structural:** see §2. The rule is that a `SqlException` caused by an OS network error
(`SocketException` or `Win32Exception`) means unreachable. Two more signals count as well:
`RetryLimitExceededException` (EF gave up on errors it does consider transient) and
`IsTransient` (server-side conditions such as an Azure SQL failover).

**What stays 500:** a failed login (the server answered; it's a configuration fault) and a
failing query (our bug). Tests prove both against the real SQL Server container, because a
503 there would invite clients to retry something that fails every time.

**Why the classifier is in Infrastructure:** only that assembly knows the store is SQL Server.
The API asks "is this unavailability?", not "which SqlClient types exist?".

**Why an exception handler, not `Result`:** no use case can do anything useful with "the
database is down". Mapping it once at the edge follows the same pattern as the domain's
exceptions (ADR-0011).

**Alternatives:**

| Option | Wins when |
|---|---|
| Error-number list | One platform, every number measured on it |
| Circuit breaker on database calls | Outage traffic is high enough that 15 s waits pile up threads |
| `Result` from repositories | A caller can do something useful with "unavailable" |
| Shorter `Connect Timeout` in the connection string | Every query, not only the probe, should give up sooner; it would also cut slow failovers short |

---

## 5. Testing what can't be faked

`SqlException` has no public constructor. A fake built by reflection would only prove the
classifier matches the shape the test author imagined, and the experiment showed that shape
is platform-specific. So every classifier test produces its exception from a **real failed
connection**:
- a refused port (`127.0.0.1,1`);
- a reserved `.invalid` hostname;
- a wrong password against the fixture;
- a query on a missing table.

The tests run on Windows locally and on Linux in CI, and both must pass. That is the point.

The readiness test uses an unroutable address (`10.255.255.1`) with a **30 s** connect timeout,
so only the new bound can end the wait. A network that rejects that address at once would let
the test pass without the bound, so a second test asserts the registration itself.

**Proved by removing the fixes:** with the timeout set to infinite and the handler unregistered,
all four guarding tests failed. The probe took 30 s and the reads returned the old status.
With the fixes restored, all passed.

---

## 6. What went wrong

- **The step-14 diagnosis was half wrong.** It blamed the slow probe on "the connect timeout",
  which was right, and suggested EF retries, which were wrong for this case. Five minutes of
  measuring changed the classifier design from a number list to a structural rule. The lesson
  is to measure the failure on the platform where it happens before writing the rule.
- **The old test passed at 16 s.** `Readiness_fails_when_the_database_is_unreachable` asserted
  only the status code. A test of a timing property has to assert time.
- **Windows DNS isn't bounded by `Connect Timeout`.** Resolving a `.invalid` name took about
  11 s with `Connect Timeout=2`. That's another reason the probe needs its own bound rather
  than trusting the connection string.
- **A tooling slip:** `npx eslint` downloaded an unconfigured ESLint (the client has no lint
  script) and its output meant nothing. The client's real checks are `npm test` and `npm run build`.

---

## 7. The drill on the deployed stack, and what it corrected

Release-checklist drill A4.6, re-run on 2026-11-18 with this build (two API replicas behind nginx):

| | Before (Step 14) | After |
|---|---|---|
| Read through the edge, SQL Server stopped | 500 | **503 `database.unavailable`**: no host and no exception text in the body, trace id present |
| `/health/ready`, each replica | no answer within 15 s | **503 in 5,012 ms and 5,005 ms** |
| Restarts | 0 | 0 |
| Recovery after `docker start` | first read 200 | first 200 about 30 s after SQL Server reported healthy, no restart |

**What the drill caught that the tests didn't: the wait before the 503 was 66 s and 38 s**, not
"up to 15 s" as first written here and in ADR-0037. The log gave the reason:

- In Compose, a stopped container's name stops resolving. SqlClient reports error **11001** ("Name
  or service not known").
- That number is in EF's transient list, unlike the refused port of §2.
- So EF retried five times with back-off, then threw `RetryLimitExceededException`. The
  classifier's branch for that exception handled it, now proved on the real deployment.

My experiment measured a refused port and a `.invalid` name (11002, *not* retried). Neither was
the failure the deployment actually produces. The lesson is the same as in §2, one level
further: measure on the topology where it happens, not only on the OS.

The answer is correct; the wait is the cost. A circuit breaker, or a smaller retry budget for
reads, would shorten it (ADR-0037, alternatives).

## 7a. What is still true (recorded in PROGRESS)

- A request during a Compose database outage waits **40–70 s** before its 503 (EF retries 11001).
- Recovery lags SQL Server's own healthcheck by tens of seconds: database recovery, plus requests
  still inside a retry loop.
- A Qdrant outage during search is still a 500.
- A failure after an answer stream has started stays an `error` event; the status is already sent.

---

## 8. What to take away

1. **5xx codes carry meaning.** 500 is "ours, don't retry"; 503 is "temporary, retry". Getting
   it wrong misleads clients and the people on call alike.
2. **A probe should not retry.** The orchestrator already calls it on a schedule. A probe's job
   is a prompt, named answer.
3. **Classify failures by structure, not by number,** when the numbers depend on the platform.
4. **A timeout only works if the code it cancels listens.** Check that cancellation is honoured
   before relying on it.
5. **Test with real failures.** A fake exception tests your idea of the failure, not the failure.
