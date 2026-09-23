namespace Netdb.Observability;

/// <summary>
/// Cross-service constants. Every Netdb service must agree on these values, in
/// every language, or the shared Grafana dashboards cannot join traces, logs
/// and metrics together.
/// </summary>
public static class NetdbTelemetryDefaults
{
    /// <summary>the one request-id header used by every service.</summary>
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>
    /// Baggage key the request id travels under, and the span attribute it is
    /// written to.
    /// </summary>
    public const string RequestIdBaggageKey = "request.id";

    /// <summary>identical health paths everywhere.</summary>
    public const string LivenessPath = "/livez";

    /// <inheritdoc cref="LivenessPath"/>
    public const string ReadinessPath = "/readyz";

    /// <summary>
    /// The OpenTelemetry semantic convention version this package
    /// emits. Attribute names moved between versions - db.statement became
    /// db.query.text, db.system became db.system.name - so the shared
    /// dashboards are built against exactly this version. Bumping it is a
    /// breaking change for them and must happen across all languages at once.
    /// </summary>
    public const string SemconvVersion = "1.43.0";

    /// <summary>
    /// Must be set before instrumentation initialises, otherwise each
    /// library falls back to its own default convention version and the
    /// database and http attribute names diverge between services.
    /// </summary>
    public const string SemconvStabilityOptIn = "database,http";

    /// <summary>
    /// The resource attributes every service must report. A missing
    /// one produces telemetry that cannot be filtered by environment or
    /// region, which is only ever discovered when you need it.
    /// </summary>
    public static readonly string[] RequiredResourceAttributes =
    [
        "service.name",
        "service.version",
        "service.instance.id",
        "deployment.environment.name",
        "cloud.region",
    ];

    /// <summary>
    /// An inbound request id is attacker-controlled at the edge and reaches log
    /// lines and span attributes, so it is length-capped and
    /// character-restricted.
    /// </summary>
    public const int RequestIdMaxLength = 128;

    /// <summary>
    /// ActivitySource names emitted by libraries that instrument themselves,
    /// so no extra package reference is needed to get their spans.
    /// </summary>
    public static readonly string[] WellKnownActivitySources =
    [
        // RabbitMQ.Client 7.x emits these natively.
        "RabbitMQ.Client.Publisher",
        "RabbitMQ.Client.Subscriber",
        // MySqlConnector emits this natively.
        "MySqlConnector",
    ];
}
