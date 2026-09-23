using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Serilog.Core;

namespace Netdb.Observability.Tests;

/// <summary>Collects events in memory so assertions can inspect them.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}

public class RedactionTests
{
    // Secrets must never reach any sink.
    //
    // This table is the shared redaction contract, and the same names are
    // asserted in otel-instrumentation-go and otel-instrumentation-next. A name
    // that redacts in one language must redact in all three.
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("passwd")]
    [InlineData("userPassword")]
    [InlineData("secret")]
    [InlineData("ClientSecret")]
    [InlineData("client_secret")]
    [InlineData("sessionSecret")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("Refresh-Token")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("X-API-KEY")]          // hyphenated header spelling
    [InlineData("x-api-key")]
    [InlineData("Api.Key")]            // dotted spelling
    [InlineData("Authorization")]
    [InlineData("proxy-authorization")]
    [InlineData("Cookie")]
    [InlineData("set-cookie")]
    [InlineData("credential")]
    [InlineData("Credentials")]
    [InlineData("db_credential")]
    public void SecretPropertyNames_AreRedacted(string name)
    {
        Assert.True(RedactingEnricher.ShouldRedact(name), $"{name} should be redacted");
    }

    [Theory]
    [InlineData("username")]
    [InlineData("user_id")]
    [InlineData("email")]
    [InlineData("duration_ms")]
    [InlineData("trace_id")]
    [InlineData("span_id")]
    [InlineData("request.id")]
    [InlineData("service.name")]
    [InlineData("status_code")]
    [InlineData("message")]
    public void NonSecretPropertyNames_AreKept(string name)
    {
        Assert.False(RedactingEnricher.ShouldRedact(name), $"{name} should not be redacted");
    }

    [Fact]
    public void Normalise_StripsSeparatorsAndCase()
    {
        // The "x" prefix survives - matching is substring-based, so "xapikey"
        // still contains "apikey" and is redacted.
        Assert.Equal("xapikey", RedactingEnricher.Normalise("X-API-KEY"));
        Assert.Equal("apikey", RedactingEnricher.Normalise("api_key"));
        Assert.Equal("apikey", RedactingEnricher.Normalise("Api.Key"));
        Assert.Equal("clientsecret", RedactingEnricher.Normalise("Client Secret"));
    }

    // The enricher must censor the value, not merely flag it.
    [Fact]
    public void Enricher_ReplacesValueInEmittedEvent()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new RedactingEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "login {Password} {Username} {Authorization}",
            "hunter2", "yannick", "Bearer abc");

        var evt = Assert.Single(sink.Events);
        Assert.Equal(
            RedactingEnricher.Placeholder,
            ((ScalarValue)evt.Properties["Password"]).Value);
        Assert.Equal(
            RedactingEnricher.Placeholder,
            ((ScalarValue)evt.Properties["Authorization"]).Value);
        Assert.Equal("yannick", ((ScalarValue)evt.Properties["Username"]).Value);

        var rendered = evt.RenderMessage();
        Assert.DoesNotContain("hunter2", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abc", rendered, StringComparison.Ordinal);
    }

    /// <summary>Everything the sink would see, as one string.</summary>
    private static string RenderAll(LogEvent evt) =>
        evt.RenderMessage() + string.Concat(evt.Properties.Select(p => $"{p.Key}={p.Value}"));

    private static ILogger Redacting(CollectingSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new RedactingEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

    // Rule 4 of the contract: a secret nested inside a destructured object must
    // be censored, not passed through because the top-level name looked clean.
    [Fact]
    public void Enricher_RedactsNestedProperties()
    {
        var sink = new CollectingSink();
        using var logger = (Logger)Redacting(sink);

        logger.Information("login {@Account}", new
        {
            Id = 7,
            Creds = new { Username = "yannick", Password = "hunter2", ApiKey = "ak-1" },
        });

        var evt = Assert.Single(sink.Events);
        var rendered = RenderAll(evt);
        Assert.DoesNotContain("hunter2", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ak-1", rendered, StringComparison.Ordinal);
        Assert.Contains("yannick", rendered, StringComparison.Ordinal);

        var account = Assert.IsType<StructureValue>(evt.Properties["Account"]);
        var creds = Assert.IsType<StructureValue>(
            account.Properties.Single(p => p.Name == "Creds").Value);
        Assert.Equal(
            RedactingEnricher.Placeholder,
            ((ScalarValue)creds.Properties.Single(p => p.Name == "Password").Value).Value);
    }

    // A dictionary key is a property name too - the Go and Node libraries treat
    // map keys the same way.
    [Fact]
    public void Enricher_RedactsDictionaryKeys()
    {
        var sink = new CollectingSink();
        using var logger = (Logger)Redacting(sink);

        logger.Information("headers {@Headers}", new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["X-Api-Key"] = "ak-1",
            ["authorization"] = "Bearer abc",
        });

        var rendered = RenderAll(Assert.Single(sink.Events));
        Assert.DoesNotContain("ak-1", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer abc", rendered, StringComparison.Ordinal);
        Assert.Contains("application/json", rendered, StringComparison.Ordinal);
    }

    // Sequence elements have no names, but the structures inside them do.
    [Fact]
    public void Enricher_RedactsInsideSequences()
    {
        var sink = new CollectingSink();
        using var logger = (Logger)Redacting(sink);

        logger.Information("sessions {@Sessions}", new[]
        {
            new { User = "a", SessionToken = "tok-a" },
            new { User = "b", SessionToken = "tok-b" },
        });

        var rendered = RenderAll(Assert.Single(sink.Events));
        Assert.DoesNotContain("tok-a", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-b", rendered, StringComparison.Ordinal);
        Assert.Contains("a", rendered, StringComparison.Ordinal);
    }

    // Redaction that reshapes every log line is redaction people turn off: a
    // clean value must reach the sink as the very instance it started as.
    [Fact]
    public void Enricher_LeavesCleanValuesUntouched()
    {
        var sink = new CollectingSink();
        using var logger = (Logger)Redacting(sink);

        logger.Information("ok {@Account}", new { Id = 7, Email = "a@b.c" });

        var account = Assert.IsType<StructureValue>(Assert.Single(sink.Events).Properties["Account"]);
        Assert.Equal("a@b.c", ((ScalarValue)account.Properties.Single(p => p.Name == "Email").Value).Value);
        Assert.Equal(2, account.Properties.Count);
    }

    // Correlation fields must use the cross-language snake_case names.
    [Fact]
    public void TraceCorrelation_AddsSnakeCaseFields()
    {
        using var source = new ActivitySource("test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("op")!;
        activity.SetBaggage(NetdbTelemetryDefaults.RequestIdBaggageKey, "rid-1");

        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new TraceCorrelationEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("hello");

        var evt = Assert.Single(sink.Events);
        Assert.True(evt.Properties.ContainsKey("trace_id"));
        Assert.True(evt.Properties.ContainsKey("span_id"));
        Assert.Equal("rid-1",
            ((ScalarValue)evt.Properties[NetdbTelemetryDefaults.RequestIdBaggageKey]).Value);
    }
}
