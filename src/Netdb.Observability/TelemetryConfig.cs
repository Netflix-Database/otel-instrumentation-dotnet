using System.Globalization;

namespace Netdb.Observability;

/// <summary>
/// Thrown when the telemetry environment is misconfigured. Carries every
/// problem, not just the first - fixing environment variables one redeploy at a
/// time is miserable.
/// </summary>
public sealed class TelemetryConfigurationException(IReadOnlyList<string> problems)
    : Exception(BuildMessage(problems))
{
    /// <summary>Every problem found, in the order they were checked.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;

    private static string BuildMessage(IReadOnlyList<string> problems)
    {
        var lines = problems.Select(p => "  - " + p);
        return "Invalid telemetry configuration:" + Environment.NewLine
            + string.Join(Environment.NewLine, lines) + Environment.NewLine
            + "See .env.example for the full set of required variables.";
    }
}

/// <summary>Validated telemetry configuration.</summary>
public sealed class TelemetryConfig
{
    /// <summary>true in development, where no collector is running.</summary>
    public bool Disabled { get; init; }

    /// <summary>the OTLP/gRPC endpoint. No other protocol is supported.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>parent-based ratio. 1.0 keeps every trace.</summary>
    public double SamplingRatio { get; init; } = 1.0;

    /// <summary>the validated attribute set.</summary>
    public IReadOnlyDictionary<string, string> ResourceAttributes { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Convenience accessor for <c>service.name</c>.</summary>
    public string ServiceName { get; init; } = "unknown_service";

    /// <summary>
    /// Validates the environment at startup so a misconfigured service
    /// fails immediately and loudly, rather than running for a week and
    /// producing telemetry nobody can group by environment.
    /// </summary>
    /// <exception cref="TelemetryConfigurationException">
    /// Thrown with every problem listed.
    /// </exception>
    public static TelemetryConfig Load(IDictionary<string, string?>? environment = null)
    {
        string? Get(string key) => environment is null
            ? Environment.GetEnvironmentVariable(key)
            : environment.TryGetValue(key, out var v) ? v : null;

        // Development needs none of the production variables.
        if (string.Equals(Get("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new TelemetryConfig
            {
                Disabled = true,
                ServiceName = Get("OTEL_SERVICE_NAME") ?? "unknown_service",
            };
        }

        var problems = new List<string>();

        var endpoint = Get("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            problems.Add("OTEL_EXPORTER_OTLP_ENDPOINT is required");
        }

        // The full resource attribute set, or the dashboards cannot slice
        // by environment, region or instance.
        var attributes = ParseResourceAttributes(Get("OTEL_RESOURCE_ATTRIBUTES"));
        var serviceNameEnv = Get("OTEL_SERVICE_NAME");
        if (!string.IsNullOrWhiteSpace(serviceNameEnv) && !attributes.ContainsKey("service.name"))
        {
            attributes["service.name"] = serviceNameEnv;
        }

        foreach (var key in NetdbTelemetryDefaults.RequiredResourceAttributes)
        {
            if (!attributes.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"OTEL_RESOURCE_ATTRIBUTES is missing \"{key}\"");
            }
        }

        // One strategy everywhere, so per-endpoint error rates on the
        // dashboard are comparable between services.
        var ratioRaw = Get("OTEL_TRACES_SAMPLER_ARG");
        if (string.IsNullOrWhiteSpace(ratioRaw)) ratioRaw = "1.0";
        if (!double.TryParse(ratioRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            || ratio < 0 || ratio > 1)
        {
            problems.Add($"OTEL_TRACES_SAMPLER_ARG must be a number in [0,1], got \"{ratioRaw}\"");
            ratio = 1.0;
        }

        if (problems.Count > 0) throw new TelemetryConfigurationException(problems);

        return new TelemetryConfig
        {
            Disabled = false,
            Endpoint = endpoint!,
            SamplingRatio = ratio,
            ResourceAttributes = attributes,
            ServiceName = attributes["service.name"],
        };
    }

    private static Dictionary<string, string> ParseResourceAttributes(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = pair.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq < 1) continue;
            result[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        return result;
    }

    /// <summary>
    /// Sets <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> if unset, merging
    /// rather than replacing an existing value.
    /// </summary>
    /// <remarks>
    /// The OpenTelemetry .NET instrumentation reads this when it initialises,
    /// so this must run before the tracer provider is built. Set it in the
    /// deployment environment as well; setting it twice is harmless, setting it
    /// nowhere is a silent divergence between services.
    /// </remarks>
    public static void ApplySemconvStabilityOptIn()
    {
        const string key = "OTEL_SEMCONV_STABILITY_OPT_IN";
        var existing = Environment.GetEnvironmentVariable(key);

        if (string.IsNullOrWhiteSpace(existing))
        {
            Environment.SetEnvironmentVariable(key, NetdbTelemetryDefaults.SemconvStabilityOptIn);
            return;
        }

        var values = existing
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var needed in NetdbTelemetryDefaults.SemconvStabilityOptIn.Split(','))
        {
            if (!values.Contains(needed, StringComparer.Ordinal)) values.Add(needed);
        }

        Environment.SetEnvironmentVariable(key, string.Join(',', values));
    }
}
