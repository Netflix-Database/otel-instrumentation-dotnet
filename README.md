# otel-instrumentation-dotnet

Shared OpenTelemetry, logging, health and request-id wiring for Netdb's .NET
services. Services depend on `Netdb.Observability` instead of configuring the
SDK themselves, so every service emits the same span attributes, the same log
schema and the same metrics — which is what makes one Grafana dashboard work
across all of them.

Siblings: `otel-instrumentation-next` (Node) and `otel-instrumentation-go`. All
three are kept in deliberate lockstep: same header, same baggage key, same
health paths, same semconv version, same log field names.

Targets `net10.0`, which every actively developed service is on. The legacy
Netdb-Hub worker tasks (net5.0/net6.0/net8.0) cannot consume it and are not
expected to.

## Installation

```bash
dotnet add package Netdb.Observability
```

## Usage

```csharp
using Netdb.Observability;

var builder = WebApplication.CreateBuilder(args);

// Throws with every configuration problem listed.
builder.AddNetdbTelemetry();

builder.Services.AddHealthChecks()
    .AddDependency("db",     ct => db.PingAsync(ct))
    .AddDependency("redis",  ct => redis.PingAsync(ct))
    .AddDependency("search", ct => search.PingAsync(ct), critical: false);

// Drains requests, then closes pools, then flushes exporters.
builder.AddNetdbGracefulShutdown(async ct =>
{
    await db.CloseAsync();
    await redis.CloseAsync();
});

var app = builder.Build();

// Early - before authentication, before anything that logs.
app.UseNetdbRequestId();

app.MapNetdbHealth();

app.Run();
GracefulShutdown.FlushLogs();
```

## Health endpoints

This builds on `Microsoft.Extensions.Diagnostics.HealthChecks` rather than
replacing it, so the whole `AspNetCore.HealthChecks.*` ecosystem works
unchanged and checks can take constructor dependencies:

```csharp
builder.Services.AddHealthChecks()
    .AddMySql(connectionString, tags: [HealthEndpoints.ReadyTag])   // community package
    .AddDependency("redis", ct => redis.PingAsync(ct));             // or a delegate
```

`MapNetdbHealth()` mounts `/livez` and `/readyz`. All this package adds is the
two paths and a response body identical to the Go and Node services.

`/livez` runs **no checks at all** — a liveness probe that touches the database
restarts the app whenever the database blips, turning a dependency outage into
an outage plus a restart loop. Its body is `{"status":"ok"}` with no `checks`
key.

`/readyz` runs only checks tagged `ready`, which `AddDependency` applies for
you. A `critical: false` dependency registers as `Degraded` instead of
`Unhealthy`, so the framework's own status mapping gives 503 for a critical
failure and 200 + `"degraded"` for the rest. Each check has a 3s timeout by
default, enforced by the framework, so one hung dependency cannot hold the
probe open.

Both paths are excluded from HTTP tracing automatically; probes fire constantly
and would otherwise dominate trace volume and the request-rate panels.

A probe's exception **message** is surfaced but never the exception itself —
connection failures routinely carry the connection string, and these endpoints
are reachable without authentication.

## Request id and baggage

```csharp
// Public edge: inbound baggage is dropped entirely.
app.UseNetdbRequestId();

// Internal service: keep specific keys from callers you trust.
app.UseNetdbRequestId(o =>
{
    o.TrustInboundBaggage = true;
    o.AllowedBaggageKeys = ["tenant.id"];
});

var id = httpContext.GetRequestId();
```

An inbound `X-Request-Id` is reused only if it is at most 128 characters and
matches `[A-Za-z0-9_.:-]+`. It reaches log lines and span attributes, so an
unchecked value is a log-forging vector and an unbounded metric cardinality
source — a comma alone would corrupt the baggage header.

Inbound baggage can never overwrite the request id, even from a trusted caller:
a compromised internal service should not be able to relabel everyone else's
traces.

`traceparent` is deliberately left alone. Dropping it would break distributed
traces, and unlike baggage it is structurally validated and carries no
free-form values.

## Errors on spans

```csharp
Activity.Current.RecordError(ex);
// or
await ActivityErrors.WithErrorRecordingAsync(() => handler(request));
```

`AddException` alone leaves the status Unset, so the span is not counted as a
failure and "error rate per endpoint" under-reports. These helpers always do
both.

## Logging

Serilog, with `trace_id`, `span_id` and `request.id` on every line.
The field names are the snake_case ones the Node and Go services emit, not
Serilog's PascalCase convention — they must match exactly or one Grafana query
cannot span all three languages.

Secrets are redacted before anything reaches a sink, under a contract shared
verbatim with the other two libraries — a secret that leaks in one language must
leak in all three, or the weakest service decides what ends up in the log
backend:

1. A name is **normalised** before matching: lowercased, with `-`, `_`, `.` and
   spaces removed. `api_key`, `apiKey`, `X-API-KEY` and `Api.Key` all reduce to
   `apikey`. HTTP header names are hyphenated and headers are the most common
   accidental leak.
2. The normalised name is **substring-matched** against `password`, `passwd`,
   `secret`, `token`, `apikey`, `authorization`, `cookie`, `credential`. The
   usual leak is a field that gained a secret months after the logging call was
   written, so `sessionToken` and `db_credential` match too.
3. A match replaces the **entire value** with `[redacted]`, whatever its type —
   the contents of a matching key are never inspected.
4. Matching applies at **every depth** up to 8, not only to top-level fields.

A value with nothing to censor is passed through as the instance it arrived as,
so clean log lines keep their exact shape and cost one walk with no allocation.
Only the branches holding a secret are rebuilt.

The walk covers destructured values — `{@User}`, dictionaries and sequences —
and runs in an enricher rather than per-sink formatting, so the console and the
OTLP sink both see the censored values.

In development (`OTEL_SDK_DISABLED=true`) Serilog writes a plain console
template and no OTLP sink is attached.

## Configuration

Validated at startup by `TelemetryConfig.Load()`, which reports every problem at
once rather than one redeploy at a time. See `.env.example`.

| Variable | Notes |
|---|---|
| `OTEL_SDK_DISABLED` | `true` in dev. Nothing else is then required. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | The collector endpoint. Use the gRPC port (4317) - the exporters are gRPC. |
| `OTEL_RESOURCE_ATTRIBUTES` | Must contain `service.name`, `service.version`, `service.instance.id`, `deployment.environment.name`, `cloud.region`. |
| `OTEL_TRACES_SAMPLER_ARG` | Parent-based ratio, 0.0–1.0, default 1.0. Parsed invariantly, so a decimal-comma locale cannot turn 0.5 into 5. |
| `OTEL_SEMCONV_STABILITY_OPT_IN` | Set to `database,http` automatically; your value is merged. |
| `LOG_LEVEL` | Defaults to `debug` when disabled, `information` otherwise. |

## Semantic conventions

Targets **semconv 1.43.0**, matching the Node and Go packages, and sets
`OTEL_SEMCONV_STABILITY_OPT_IN=database,http` before the providers are built.

Attribute names moved between versions — `db.statement` became `db.query.text`,
`db.system` became `db.system.name`. The shared dashboards are built against
this version, so bumping it is a breaking change for them and must happen across
all three languages at once.

Set the variable in the deployment environment as well. Setting it twice is
harmless; setting it nowhere is a silent divergence between services.

## What is instrumented

ASP.NET Core, HttpClient, SqlClient, .NET runtime metrics, and the
ActivitySources that RabbitMQ.Client 7.x and MySqlConnector emit natively — no extra package reference needed for those.

StackExchange.Redis is **not** included. Its instrumentation package is still
beta and needs the `IConnectionMultiplexer` instance, so services wire it
themselves:

```csharp
builder.AddNetdbTelemetry(o =>
    o.ConfigureTracing = t => t.AddRedisInstrumentation(multiplexer));
```

The same hook takes any other instrumentation, and
`AdditionalActivitySources` collects sources a service creates itself.

## Repository notes

`nuget.config` clears inherited package sources. Without it, an unreachable
private feed configured machine-wide fails the build with NU1900, because the
project treats warnings as errors.

`dotnet.config` opts into the Microsoft.Testing.Platform runner, which xunit.v3
4.x requires on the .NET 10 SDK.
