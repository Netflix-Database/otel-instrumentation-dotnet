using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Netdb.Observability.Tests;

public class HealthResponseTests
{
    private static HealthReport Report(HealthStatus overall, params (string Name, HealthStatus Status, string? Error)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Name,
            e => new HealthReportEntry(
                e.Status,
                description: e.Error,
                duration: TimeSpan.FromMilliseconds(12),
                exception: null,
                data: null),
            StringComparer.Ordinal);

        return new HealthReport(dict, overall, TimeSpan.FromMilliseconds(20));
    }

    private static async Task<(int Status, JsonElement Body, string? CacheControl)> WriteAsync(HealthReport report)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        // MapHealthChecks sets the code from ResultStatusCodes; the writer only
        // produces the body, so the test sets it the way the framework would.
        context.Response.StatusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        await NetdbHealthResponse.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, doc.RootElement.Clone(), context.Response.Headers.CacheControl);
    }

    // Liveness runs no checks, so the body carries no "checks" key at all -
    // byte-identical to what the Go and Node liveness handlers return.
    [Fact]
    public async Task NoEntries_OmitsChecksObject()
    {
        var (status, body, cacheControl) = await WriteAsync(Report(HealthStatus.Healthy));

        Assert.Equal(200, status);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("checks", out _));
        Assert.Equal("no-store", cacheControl);
    }

    [Fact]
    public async Task AllHealthy_ReportsOk()
    {
        var (status, body, _) = await WriteAsync(Report(HealthStatus.Healthy,
            ("db", HealthStatus.Healthy, null),
            ("redis", HealthStatus.Healthy, null)));

        Assert.Equal(200, status);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var checks = body.GetProperty("checks");
        Assert.Equal("ok", checks.GetProperty("db").GetProperty("status").GetString());
        Assert.Equal(12, checks.GetProperty("db").GetProperty("duration_ms").GetInt64());
        // A passing check carries no error key.
        Assert.False(checks.GetProperty("db").TryGetProperty("error", out _));
    }

    // A critical failure is Unhealthy, which the framework maps to 503 so load
    // balancers hold traffic back.
    [Fact]
    public async Task CriticalFailure_IsErrorAnd503()
    {
        var (status, body, _) = await WriteAsync(Report(HealthStatus.Unhealthy,
            ("db", HealthStatus.Unhealthy, "conn refused")));

        Assert.Equal(503, status);
        Assert.Equal("error", body.GetProperty("status").GetString());
        var db = body.GetProperty("checks").GetProperty("db");
        Assert.Equal("error", db.GetProperty("status").GetString());
        Assert.Equal("conn refused", db.GetProperty("error").GetString());
    }

    // A non-critical failure is Degraded: reported, but still serving.
    [Fact]
    public async Task NonCriticalFailure_IsDegradedAnd200()
    {
        var (status, body, _) = await WriteAsync(Report(HealthStatus.Degraded,
            ("search", HealthStatus.Degraded, "down")));

        Assert.Equal(200, status);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("error", body.GetProperty("checks").GetProperty("search").GetProperty("status").GetString());
    }
}

public class HealthRegistrationTests
{
    private static HealthCheckRegistration Register(
        string name, Func<CancellationToken, Task> probe, bool critical = true, TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddDependency(name, probe, critical, timeout);

        var options = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();

        return Assert.Single(options.Value.Registrations);
    }

    // Only tagged checks run on /readyz, so the tag has to be applied for the
    // probe to see the dependency at all.
    [Fact]
    public void Dependency_IsTaggedReady()
    {
        var registration = Register("db", _ => Task.CompletedTask);

        Assert.Contains(HealthEndpoints.ReadyTag, registration.Tags);
    }

    [Fact]
    public void Critical_MapsToUnhealthy()
    {
        Assert.Equal(HealthStatus.Unhealthy, Register("db", _ => Task.CompletedTask).FailureStatus);
    }

    [Fact]
    public void NonCritical_MapsToDegraded()
    {
        Assert.Equal(
            HealthStatus.Degraded,
            Register("search", _ => Task.CompletedTask, critical: false).FailureStatus);
    }

    // A hung dependency must not hold the probe open indefinitely.
    [Fact]
    public void Timeout_DefaultsAndIsOverridable()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), Register("db", _ => Task.CompletedTask).Timeout);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            Register("db", _ => Task.CompletedTask, timeout: TimeSpan.FromMilliseconds(250)).Timeout);
    }

    // The probe's exception message is surfaced, but never the exception -
    // connection failures routinely carry the connection string.
    [Fact]
    public async Task ProbeFailure_SurfacesMessageWithoutException()
    {
        var registration = Register("db", _ => throw new InvalidOperationException("conn refused"));
        var check = registration.Factory(new ServiceCollection().BuildServiceProvider());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("conn refused", result.Description);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task ProbeSuccess_IsHealthy()
    {
        var registration = Register("db", _ => Task.CompletedTask);
        var check = registration.Factory(new ServiceCollection().BuildServiceProvider());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ProbeCancellation_ReportsTimeout()
    {
        var registration = Register("db", ct => Task.FromCanceled(new CancellationToken(canceled: true)));
        var check = registration.Factory(new ServiceCollection().BuildServiceProvider());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("timed out", result.Description);
    }
}
