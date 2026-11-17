# ADR-0035: Observability — OpenTelemetry over OTLP, built-in logging, no Serilog

- **Status:** Accepted
- **Date:** 2026-11-17
- **Deciders:** project owner

## Context

Step 14 asks for "Serilog + OpenTelemetry" and the three pillars of observability. Until now
the two hosts wrote plain-text console logs and nothing else: no traces, no metrics, and no
way to follow one upload from the API's request through the worker's job to the model call.

Two constraints mattered more than the library names in the plan:

1. **Log filters are a security control here.** `Logging:LogLevel:Microsoft.Extensions.AI` is
   pinned to `Information` because at `Trace` the AI middleware logs whole prompts and answers,
   i.e. document text (Step 12, threat model; `LogHygieneTests`). Whatever writes the logs must
   keep honouring that section.
2. **Export must be configuration.** The same build runs on a laptop with no collector and in
   Compose with one (twelve-factor).

Wiring Serilog in showed the conflict with (1): its host integration (`UseSerilog` /
`AddSerilog`) **replaces** `ILoggerFactory`, so the `Logging:LogLevel` rules stop applying and
levels move to a separate `Serilog:MinimumLevel` section. The pinned AI level would silently
stop working unless duplicated, and a duplicated security rule is one that drifts.

## Decision

1. **Logs stay on Microsoft.Extensions.Logging.** The console formatter is configuration:
   `Logging:Console:FormatterName` is `json` in `appsettings.json` (one JSON object per line,
   scopes included, UTC timestamps) and `simple` in Development. OpenTelemetry is added as one
   more logging provider, so every filter applies to every sink.
2. **Traces and metrics with OpenTelemetry** (`EKA.Infrastructure/Observability/Telemetry.cs`,
   one call per host): ASP.NET Core (API only, `/health` excluded), HttpClient, SqlClient,
   runtime metrics, ASP.NET Core / Kestrel / rate-limiting meters, the Microsoft.Extensions.AI
   middleware (`UseOpenTelemetry`, source `EKA.AI`, **sensitive data off**), and an
   `EKA.Ingestion` span around each ingestion job.
3. **OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set**, and nowhere otherwise (`UseOtlpExporter`,
   which reads the standard `OTEL_*` variables for all three signals).
4. **Traces start only at entry points.** A custom root sampler records a trace for a Server
   span (an HTTP request) or a Consumer span (a dequeued job) and drops every other parentless
   span; `ParentBasedSampler` makes children follow. Without it the worker produced a trace per
   second: Hangfire polling its queue.
5. **The Compose deployment uses the standalone Aspire dashboard** as its OTLP endpoint and UI.

## Consequences

### Positive

- One upload is one trace in the worker: the job span with its SQL, Qdrant and embedding spans
  beneath it; one question is one trace in the API with the model call's tokens on it.
- The log-level rule that keeps document text out of logs covers the OTLP sink automatically.
- Seven fewer packages than the Serilog plan; nothing to configure twice.
- Logs, traces and metrics share `service.name`, `deployment.environment.name` and trace ids,
  so the dashboard links a log line to its trace.

### Negative / costs we accept

- **No log enrichment ecosystem.** Serilog's enrichers, destructuring policies and sinks
  (Seq, Elasticsearch, rolling files) are not available; OTLP is the only export path.
- **No bootstrap logger.** A crash before the host is built (a bad configuration file) is
  written by the runtime as plain text, not JSON.
- **The Aspire dashboard keeps data in memory** and is a single container: fine for one host
  and for learning, not a monitoring system. Production needs an OTLP collector and a durable
  backend (Azure Monitor, Grafana stack, Honeycomb...).
- The worker and API traces are **not linked** across the Hangfire hand-off (no context
  propagation through the job arguments); an upload is two traces, joined by the document id
  tag.
- `EKA.Infrastructure` now carries host-level telemetry wiring. A `ServiceDefaults` project
  (the Aspire pattern) would be the cleaner home once a third host exists.

## Alternatives considered

- **Serilog as the logger, OpenTelemetry for traces and metrics** (the plan). Wins when the
  team already runs Seq or Elasticsearch, wants enrichers, or needs file sinks — and is willing
  to move every level rule, including the AI one, to `Serilog:MinimumLevel` and test it there.
- **Serilog added as an `ILoggerProvider`** (`builder.Logging.AddSerilog`), which keeps MEL
  filtering. Works, but then Serilog contributes only formatting the built-in JSON formatter
  already provides, and its request-logging middleware needs its own DI wiring.
- **Vendor SDKs** (Application Insights classic, Datadog tracer). Win when the organisation has
  standardised on one and OTLP ingestion is not available; they lock the code to the vendor.
- **Jaeger + Prometheus + Grafana in Compose** instead of the Aspire dashboard. Wins when
  dashboards and alerting must persist; three more services and their configuration.

## Revisit when

- A third process needs the same wiring (move it to a ServiceDefaults project).
- A log sink other than OTLP is required, or log volume makes sampling/enrichment necessary.
- Traces need to follow a document across the API → worker boundary (propagate
  `traceparent` through the job).
