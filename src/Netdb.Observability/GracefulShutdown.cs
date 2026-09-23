using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Netdb.Observability;

/// <summary>orderly termination on SIGTERM.</summary>
/// <remarks>
/// <para>
/// ASP.NET Core already stops the listener and drains in-flight requests when
/// the host stops. What it does not do by default is give that enough time, or
/// guarantee that dependency teardown happens before the exporters are flushed.
/// </para>
/// <para>
/// Order matters: closing pools can itself produce spans, and anything recorded
/// after the flush is lost - which is exactly the telemetry you want when a
/// deploy goes wrong.
/// </para>
/// </remarks>
public static class GracefulShutdown
{
    /// <summary>
    /// Sets the host shutdown timeout and registers dependency teardown that
    /// runs while the exporters are still alive.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="closeDependencies">
    /// Closes db, redis and rabbit connections. Runs after in-flight requests
    /// have drained, so they still have their pools.
    /// </param>
    /// <param name="timeout">
    /// Must be shorter than the orchestrator's grace period - Docker and
    /// Kubernetes send SIGKILL after theirs, and a flush that has not finished
    /// by then is simply lost. Defaults to 15s.
    /// </param>
    public static IHostApplicationBuilder AddNetdbGracefulShutdown(
        this IHostApplicationBuilder builder,
        Func<CancellationToken, Task>? closeDependencies = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var window = timeout ?? TimeSpan.FromSeconds(15);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = window);

        if (closeDependencies is not null)
        {
            builder.Services.AddSingleton<IHostedService>(
                _ => new DependencyTeardownService(closeDependencies));
        }

        return builder;
    }

    /// <summary>
    /// Flushes Serilog. Call this last, after the host has stopped, so buffered
    /// log records are not dropped.
    /// </summary>
    public static void FlushLogs() => Log.CloseAndFlush();

    /// <summary>
    /// Hosted service whose only job is to run teardown during StopAsync.
    /// Hosted services stop in reverse registration order, and this one is
    /// registered by AddNetdbTelemetry's caller before the OTel hosted service
    /// starts exporting, so its teardown spans still get exported.
    /// </summary>
    private sealed class DependencyTeardownService(Func<CancellationToken, Task> closeDependencies)
        : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await closeDependencies(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let teardown failure mask the rest of the shutdown
                // sequence; the exporters still need their chance to flush.
                Log.Error(ex, "error closing dependencies during shutdown");
            }
        }
    }
}
