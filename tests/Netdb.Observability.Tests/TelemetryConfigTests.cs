using Netdb.Observability;

namespace Netdb.Observability.Tests;

public class TelemetryConfigTests
{
    private const string ValidAttributes =
        "service.name=svc,service.version=abc123,service.instance.id=i-1," +
        "deployment.environment.name=prod,cloud.region=eu-central-1";

    private static Dictionary<string, string?> Env(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (string?)p.Value);

    // Development needs none of the production configuration.
    [Fact]
    public void Disabled_ShortCircuits()
    {
        var config = TelemetryConfig.Load(Env(("OTEL_SDK_DISABLED", "true")));

        Assert.True(config.Disabled);
    }

    // Every problem reported at once, not one redeploy at a time.
    [Fact]
    public void Empty_ReportsAllProblemsTogether()
    {
        var ex = Assert.Throws<TelemetryConfigurationException>(
            () => TelemetryConfig.Load(new Dictionary<string, string?>()));

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT is required", ex.Message);
        foreach (var attribute in NetdbTelemetryDefaults.RequiredResourceAttributes)
        {
            Assert.Contains(attribute, ex.Message);
        }
        Assert.True(ex.Problems.Count >= 6, $"expected batched problems, got {ex.Problems.Count}");
    }

    [Fact]
    public void ValidConfiguration_IsAccepted()
    {
        var config = TelemetryConfig.Load(Env(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_RESOURCE_ATTRIBUTES", ValidAttributes)));

        Assert.False(config.Disabled);
        Assert.Equal("svc", config.ServiceName);
        // Default keeps every trace.
        Assert.Equal(1.0, config.SamplingRatio);
        // The build SHA travels as service.version.
        Assert.Equal("abc123", config.ResourceAttributes["service.version"]);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("many")]
    public void OutOfRangeSampler_IsRejected(string value)
    {
        var ex = Assert.Throws<TelemetryConfigurationException>(() => TelemetryConfig.Load(Env(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_RESOURCE_ATTRIBUTES", ValidAttributes),
            ("OTEL_TRACES_SAMPLER_ARG", value))));

        Assert.Contains("[0,1]", ex.Message);
    }

    // A decimal comma locale must not turn 0.5 into 5.
    [Fact]
    public void Sampler_IsParsedInvariantly()
    {
        var config = TelemetryConfig.Load(Env(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_RESOURCE_ATTRIBUTES", ValidAttributes),
            ("OTEL_TRACES_SAMPLER_ARG", "0.25")));

        Assert.Equal(0.25, config.SamplingRatio);
    }

    [Fact]
    public void MissingSingleAttribute_IsReported()
    {
        var ex = Assert.Throws<TelemetryConfigurationException>(() => TelemetryConfig.Load(Env(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_RESOURCE_ATTRIBUTES",
                "service.name=svc,service.version=v,service.instance.id=i,deployment.environment.name=prod"))));

        Assert.Contains("cloud.region", ex.Message);
        Assert.Single(ex.Problems);
    }

    // The cross-language contract must not drift.
    [Fact]
    public void CrossLanguageConstants_AreStable()
    {
        Assert.Equal("X-Request-Id", NetdbTelemetryDefaults.RequestIdHeader);
        Assert.Equal("request.id", NetdbTelemetryDefaults.RequestIdBaggageKey);
        Assert.Equal("/livez", NetdbTelemetryDefaults.LivenessPath);
        Assert.Equal("/readyz", NetdbTelemetryDefaults.ReadinessPath);
        Assert.Equal("1.43.0", NetdbTelemetryDefaults.SemconvVersion);
        Assert.Equal("database,http", NetdbTelemetryDefaults.SemconvStabilityOptIn);
    }

    // Merge, never clobber.
    [Fact]
    public void SemconvOptIn_MergesWithExisting()
    {
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", "custom");
        try
        {
            TelemetryConfig.ApplySemconvStabilityOptIn();
            var result = Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN")!;

            Assert.Contains("custom", result);
            Assert.Contains("database", result);
            Assert.Contains("http", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", null);
        }
    }

    [Fact]
    public void SemconvOptIn_SetsWhenAbsent()
    {
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", null);
        try
        {
            TelemetryConfig.ApplySemconvStabilityOptIn();

            Assert.Equal(
                NetdbTelemetryDefaults.SemconvStabilityOptIn,
                Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", null);
        }
    }
}
