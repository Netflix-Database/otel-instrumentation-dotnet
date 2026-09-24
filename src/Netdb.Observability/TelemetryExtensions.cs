using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Netdb.Observability;

/// <summary>Optional hooks for service-specific instrumentation.</summary>
public sealed class NetdbTelemetryOptions
{
    /// <summary>
    /// Extra tracing setup, for instrumentation this package does not take a
    /// dependency on - for example
    /// <c>AddRedisInstrumentation(multiplexer)</c>.
    /// </summary>
    public Action<TracerProviderBuilder>? ConfigureTracing { get; set; }

    /// <summary>Extra metrics setup.</summary>
    public Action<MeterProviderBuilder>? ConfigureMetrics { get; set; }

    /// <summary>
    /// Additional ActivitySource names to collect, for services that create
    /// their own.
    /// </summary>
    public IReadOnlyCollection<string> AdditionalActivitySources { get; set; } = [];

    /// <summary>
    /// Paths excluded from HTTP tracing. The health endpoints are always
    /// excluded - probes fire constantly and would otherwise dominate both
    /// trace volume and the request-rate panels.
    /// </summary>
    public IReadOnlyCollection<string> IgnoredPaths { get; set; } = [];

    /// <summary>
    /// Extra Serilog setup, applied after this package's own. Use it to layer
    /// a service's <c>ReadFrom.Configuration(builder.Configuration)</c>, extra
    /// filters or extra sinks on top - the redaction enricher, the correlation
    /// enricher and the OTLP sink are already in place and are not lost.
    /// </summary>
    public Action<Serilog.LoggerConfiguration>? ConfigureLogging { get; set; }

    /// <summary>
    /// Skips this package's console sink, for services that declare their own
    /// sinks in appsettings and would otherwise log to the console twice. The
    /// OTLP sink is always kept.
    /// </summary>
    public bool SuppressDefaultConsoleSink { get; set; }
}

/// <summary>Wires OpenTelemetry and Serilog the same way in every service.</summary>
public static class TelemetryExtensions
{
    /// <summary>
    /// Adds traces, metrics and logs over OTLP/gRPC, plus Serilog with
    /// redaction and trace correlation.
    /// </summary>
    /// <remarks>
    /// The configuration is validated first, so a service with bad
    /// resource attributes fails at boot instead of exporting telemetry nobody
    /// can group.
    /// </remarks>
    /// <exception cref="TelemetryConfigurationException">
    /// Thrown with every configuration problem listed.
    /// </exception>
    public static IHostApplicationBuilder AddNetdbTelemetry(
        this IHostApplicationBuilder builder,
        Action<NetdbTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Must happen before the providers are built, because the
        // instrumentation reads it as it initialises.
        TelemetryConfig.ApplySemconvStabilityOptIn();

        var config = TelemetryConfig.Load();
        var options = new NetdbTelemetryOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(config);

        ConfigureSerilog(builder, config, options);

        // No exporters at all in development. Serilog is still wired
        // above, so dev keeps pretty console logs.
        if (config.Disabled) return builder;

        var ignoredPaths = new HashSet<string>(
            options.IgnoredPaths.Append(NetdbTelemetryDefaults.LivenessPath)
                                .Append(NetdbTelemetryDefaults.ReadinessPath),
            StringComparer.Ordinal);

        builder.Services
            .AddOpenTelemetry()
            // Built from the validated attribute set rather than left
            // to the env detector, so a typo fails at startup instead of
            // silently producing an unlabelled service.
            .ConfigureResource(resource => resource.AddAttributes(
                config.ResourceAttributes.Select(kv =>
                    new KeyValuePair<string, object>(kv.Key, kv.Value))))
            .WithTracing(tracing =>
            {
                // Parent-based, so a sampled trace stays sampled
                // across service hops - otherwise distributed traces come back
                // with holes in them.
                tracing.SetSampler(new ParentBasedSampler(
                    new TraceIdRatioBasedSampler(config.SamplingRatio)));

                tracing.AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ignoredPaths.Contains(ctx.Request.Path.Value ?? string.Empty));

                tracing.AddHttpClientInstrumentation();
                tracing.AddSqlClientInstrumentation();

                // Libraries that instrument themselves need
                // only their ActivitySource collected, not a package reference.
                foreach (var source in NetdbTelemetryDefaults.WellKnownActivitySources)
                {
                    tracing.AddSource(source);
                }
                foreach (var source in options.AdditionalActivitySources)
                {
                    tracing.AddSource(source);
                }

                tracing.AddOtlpExporter();
                options.ConfigureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                // GC, heap and thread-pool metrics, as standard
                // instruments so one dashboard panel covers every service.
                metrics.AddRuntimeInstrumentation();
                metrics.AddOtlpExporter();
                options.ConfigureMetrics?.Invoke(metrics);
            });

        return builder;
    }

    /// <summary>
    /// Serilog everywhere, with the same field names the Node and Go services
    /// emit.
    /// </summary>
    private static void ConfigureSerilog(
        IHostApplicationBuilder builder,
        TelemetryConfig config,
        NetdbTelemetryOptions options)
    {
        var logLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") switch
        {
            "trace" or "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "info" or "information" => LogEventLevel.Information,
            "warn" or "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => config.Disabled ? LogEventLevel.Debug : LogEventLevel.Information,
        };

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            // Before any sink, so console and OTLP both see censored
            // values.
            .Enrich.With(new RedactingEnricher())
            // trace_id and span_id on every line.
            .Enrich.FromLogContext()
            .Enrich.With(new TraceCorrelationEnricher());

        if (config.Disabled)
        {
            // Human-readable output for local development.
            if (!options.SuppressDefaultConsoleSink)
            {
                logger = logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }
        }
        else
        {
            if (!options.SuppressDefaultConsoleSink)
            {
                logger = logger.WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter());
            }

            // Logs over OTLP/gRPC like traces and metrics. Never suppressed:
            // dropping it would take the service off the shared dashboard,
            // which is the whole point of this package.
            logger = logger.WriteTo.OpenTelemetry(o =>
            {
                o.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                o.ResourceAttributes = config.ResourceAttributes
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            });
        }

        // Applied last, so a service can raise or lower levels, add filters, or
        // read its own sinks from appsettings without losing the redaction
        // enricher or the OTLP sink above.
        options.ConfigureLogging?.Invoke(logger);

        Log.Logger = logger.CreateLogger();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger);
    }
}
