using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Netdb.Observability.Tests;

public class RequestIdMiddlewareTests
{
    private static async Task<(HttpContext Context, Activity Activity)> RunAsync(
        Action<HttpContext>? arrange = null,
        Action<RequestIdOptions>? configure = null,
        Action<Activity>? seedBaggage = null)
    {
        // The middleware mutates Activity.Current, so each case gets its own.
        using var source = new ActivitySource("test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("request")!;
        seedBaggage?.Invoke(activity);

        var context = new DefaultHttpContext();
        arrange?.Invoke(context);

        var options = new RequestIdOptions();
        configure?.Invoke(options);

        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask, options);
        await middleware.InvokeAsync(context);

        return (context, activity);
    }

    // A well-formed inbound id is reused so traces stay joined up.
    [Fact]
    public async Task ValidInboundId_IsReused()
    {
        var (context, activity) = await RunAsync(c =>
            c.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader] = "abc-123");

        Assert.Equal("abc-123", context.GetRequestId());
        Assert.Equal("abc-123", context.Response.Headers[NetdbTelemetryDefaults.RequestIdHeader]);
        Assert.Equal("abc-123", activity.GetBaggageItem(NetdbTelemetryDefaults.RequestIdBaggageKey));
    }

    // The id reaches log lines and span attributes, so hostile values are replaced.
    [Theory]
    [InlineData("id level=error msg=fake")]   // space-separated log forging
    [InlineData("a,b=c")]                     // comma would split baggage
    [InlineData("id;prop=1")]                 // baggage metadata delimiter
    [InlineData("")]
    [InlineData("   ")]
    public async Task HostileInboundId_IsReplaced(string hostile)
    {
        var (context, _) = await RunAsync(c =>
            c.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader] = hostile);

        var id = context.GetRequestId()!;
        Assert.NotEqual(hostile, id);
        Assert.True(RequestIdMiddleware.IsValidRequestId(id));
    }

    [Fact]
    public async Task OverlongInboundId_IsReplaced()
    {
        var hostile = new string('x', NetdbTelemetryDefaults.RequestIdMaxLength + 1);
        var (context, _) = await RunAsync(c =>
            c.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader] = hostile);

        Assert.NotEqual(hostile, context.GetRequestId());
    }

    [Fact]
    public async Task MissingInboundId_IsGenerated()
    {
        var (context, _) = await RunAsync();

        Assert.True(RequestIdMiddleware.IsValidRequestId(context.GetRequestId()));
    }

    // Untrusted baggage must not reach downstream services.
    [Fact]
    public async Task UntrustedBaggage_IsDropped()
    {
        var (_, activity) = await RunAsync(
            seedBaggage: a =>
            {
                a.SetBaggage("evil", "1");
                a.SetBaggage("tenant", "acme");
            });

        Assert.Null(activity.GetBaggageItem("evil"));
        Assert.Null(activity.GetBaggageItem("tenant"));
        Assert.NotNull(activity.GetBaggageItem(NetdbTelemetryDefaults.RequestIdBaggageKey));
    }

    // Even a trusted caller only gets allow-listed keys through.
    [Fact]
    public async Task TrustedBaggage_KeepsOnlyAllowListed()
    {
        var (_, activity) = await RunAsync(
            configure: o =>
            {
                o.TrustInboundBaggage = true;
                o.AllowedBaggageKeys = ["tenant"];
            },
            seedBaggage: a =>
            {
                a.SetBaggage("evil", "1");
                a.SetBaggage("tenant", "acme");
            });

        Assert.Equal("acme", activity.GetBaggageItem("tenant"));
        Assert.Null(activity.GetBaggageItem("evil"));
    }

    // A compromised internal service must not be able to relabel traces.
    [Fact]
    public async Task InboundBaggage_CannotSpoofRequestId()
    {
        var (_, activity) = await RunAsync(
            arrange: c => c.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader] = "real-id",
            configure: o =>
            {
                o.TrustInboundBaggage = true;
                o.AllowedBaggageKeys = [NetdbTelemetryDefaults.RequestIdBaggageKey];
            },
            seedBaggage: a => a.SetBaggage(NetdbTelemetryDefaults.RequestIdBaggageKey, "spoofed"));

        Assert.Equal("real-id", activity.GetBaggageItem(NetdbTelemetryDefaults.RequestIdBaggageKey));
    }

    // The id must land on the span so traces are findable by it.
    [Fact]
    public async Task RequestId_IsSetAsSpanTag()
    {
        var (context, activity) = await RunAsync();

        var tag = activity.GetTagItem(NetdbTelemetryDefaults.RequestIdBaggageKey);
        Assert.Equal(context.GetRequestId(), tag);
    }
}
