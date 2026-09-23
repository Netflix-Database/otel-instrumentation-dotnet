using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Netdb.Observability;

/// <summary>Policy for inbound baggage.</summary>
public sealed class RequestIdOptions
{
    /// <summary>
    /// Must stay false at a public edge. Baggage propagates verbatim to every
    /// downstream service, so an untrusted client could otherwise inject
    /// arbitrary keys that land on spans and in logs across the whole system.
    /// </summary>
    public bool TrustInboundBaggage { get; set; }

    /// <summary>
    /// Keys kept from inbound baggage when trusted. The request id is always
    /// replaced with ours regardless. Defaults to none.
    /// </summary>
    public IReadOnlyCollection<string> AllowedBaggageKeys { get; set; } = [];
}

/// <summary>
/// Ensures every request carries a valid request id, publishes it as baggage so
/// it reaches downstream services, and writes it onto the current Activity
///.
/// </summary>
public sealed partial class RequestIdMiddleware(RequestDelegate next, RequestIdOptions options)
{
    // An inbound id reaches log lines and span attributes, so it is restricted.
    // Without this it is a log-forging vector and an unbounded metric
    // cardinality source; a comma alone would corrupt the baggage header.
    [GeneratedRegex("^[A-Za-z0-9_.:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    internal static bool IsValidRequestId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= NetdbTelemetryDefaults.RequestIdMaxLength
        && RequestIdPattern().IsMatch(id);

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader].ToString();
        var requestId = IsValidRequestId(incoming) ? incoming : Guid.NewGuid().ToString();

        context.Request.Headers[NetdbTelemetryDefaults.RequestIdHeader] = requestId;
        context.Response.Headers[NetdbTelemetryDefaults.RequestIdHeader] = requestId;
        context.Items[NetdbTelemetryDefaults.RequestIdBaggageKey] = requestId;

        // Rebuild baggage rather than appending to whatever arrived.
        // Activity.Baggage is inherited from the propagated context, so the
        // only way to drop untrusted entries is to start from nothing.
        var activity = Activity.Current;
        if (activity is not null)
        {
            var kept = new List<KeyValuePair<string, string?>>();
            if (options.TrustInboundBaggage)
            {
                foreach (var item in activity.Baggage)
                {
                    if (item.Key == NetdbTelemetryDefaults.RequestIdBaggageKey) continue;
                    if (!options.AllowedBaggageKeys.Contains(item.Key, StringComparer.Ordinal)) continue;
                    kept.Add(item);
                }
            }

            foreach (var item in activity.Baggage.ToList())
                activity.SetBaggage(item.Key, null);

            foreach (var item in kept)
                activity.SetBaggage(item.Key, item.Value);

            activity.SetBaggage(NetdbTelemetryDefaults.RequestIdBaggageKey, requestId);
            activity.SetTag(NetdbTelemetryDefaults.RequestIdBaggageKey, requestId);
        }

        await next(context);
    }
}

/// <summary>Extension methods for wiring the middleware.</summary>
public static class RequestIdMiddlewareExtensions
{
    /// <summary>
    /// Adds request-id handling. Call this early - before authentication and
    /// before anything that logs - so every log line for the request carries
    /// the id.
    /// </summary>
    public static IApplicationBuilder UseNetdbRequestId(
        this IApplicationBuilder app,
        Action<RequestIdOptions>? configure = null)
    {
        var options = new RequestIdOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<RequestIdMiddleware>(options);
    }

    /// <summary>The request id for the current request, if the middleware ran.</summary>
    public static string? GetRequestId(this HttpContext context) =>
        context.Items.TryGetValue(NetdbTelemetryDefaults.RequestIdBaggageKey, out var v)
            ? v as string
            : null;
}
