using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Netdb.Observability;

/// <summary>
/// One health contract for every service, so a single Docker or Kubernetes
/// probe config and one dashboard panel work everywhere.
/// </summary>
/// <remarks>
/// This sits on <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> rather
/// than replacing it, so the whole <c>AspNetCore.HealthChecks.*</c> ecosystem
/// (MySql, Redis, RabbitMQ, …) works unchanged and checks can take
/// constructor dependencies. All this package adds is the two paths and a
/// response shape identical to the Go and Node services.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>
    /// Tag marking a check as a readiness dependency. Only tagged checks run
    /// on <c>/readyz</c>.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Maps <c>/livez</c> and <c>/readyz</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/livez</c> runs no checks at all: a liveness probe that touches the
    /// database restarts the app whenever the database blips, turning a
    /// dependency outage into an outage plus a restart loop.
    /// </para>
    /// <para>
    /// <c>/readyz</c> returns 503 when a check registered as critical fails,
    /// and 200 with <c>"degraded"</c> when only non-critical ones do - which is
    /// exactly the framework's Unhealthy/Degraded split.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapNetdbHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(NetdbTelemetryDefaults.LivenessPath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = NetdbHealthResponse.WriteAsync,
        })
            .AllowAnonymous()
            .ExcludeFromDescription();

        endpoints.MapHealthChecks(NetdbTelemetryDefaults.ReadinessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = NetdbHealthResponse.WriteAsync,
        })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }
}

/// <summary>Registration helpers for dependency probes.</summary>
public static class NetdbHealthChecksBuilderExtensions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Registers a readiness probe from a delegate, for the common case where
    /// a whole <see cref="IHealthCheck"/> class is overkill. Keep it cheap -
    /// SELECT 1, PING.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Name reported in the response body.</param>
    /// <param name="probe">Throws when the dependency is unhealthy.</param>
    /// <param name="critical">
    /// When false, a failure is reported but leaves the service ready - it maps
    /// to Degraded rather than Unhealthy. Use for dependencies the service
    /// degrades without rather than dies without.
    /// </param>
    /// <param name="timeout">
    /// Per-check timeout, enforced by the framework. Defaults to 3s so one
    /// hung dependency cannot hold the whole probe open.
    /// </param>
    public static IHealthChecksBuilder AddDependency(
        this IHealthChecksBuilder builder,
        string name,
        Func<CancellationToken, Task> probe,
        bool critical = true,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            _ => new DelegateHealthCheck(probe),
            critical ? HealthStatus.Unhealthy : HealthStatus.Degraded,
            [HealthEndpoints.ReadyTag],
            timeout ?? DefaultTimeout));
    }

    private sealed class DelegateHealthCheck(Func<CancellationToken, Task> probe) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await probe(cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    $"{context.Registration.Name} check timed out");
            }
            catch (Exception ex)
            {
                // Only the message, never the exception: connection failures
                // routinely carry the connection string, and this response is
                // reachable without authentication.
                return new HealthCheckResult(context.Registration.FailureStatus, ex.Message);
            }
        }
    }
}

/// <summary>
/// Writes the health response in the shape the Go and Node services emit, so
/// one dashboard panel and one probe parser cover every language.
/// </summary>
public static class NetdbHealthResponse
{
    /// <summary>Serialises <paramref name="report"/> to the response.</summary>
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", Overall(report.Status));

            // Omitted entirely when empty, so /livez answers {"status":"ok"}
            // exactly as the Go and Node liveness handlers do.
            if (report.Entries.Count > 0)
            {
                writer.WriteStartObject("checks");
                foreach (var (name, entry) in report.Entries)
                {
                    writer.WriteStartObject(name);
                    writer.WriteString("status", entry.Status == HealthStatus.Healthy ? "ok" : "error");

                    var error = entry.Description ?? entry.Exception?.Message;
                    if (entry.Status != HealthStatus.Healthy && !string.IsNullOrEmpty(error))
                    {
                        writer.WriteString("error", error);
                    }

                    writer.WriteNumber("duration_ms", (long)entry.Duration.TotalMilliseconds);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray()).ConfigureAwait(false);
    }

    private static string Overall(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "ok",
        HealthStatus.Degraded => "degraded",
        _ => "error",
    };
}
