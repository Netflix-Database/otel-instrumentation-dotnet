using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Netdb.Observability;

/// <summary>
/// Puts <c>trace_id</c>, <c>span_id</c> and <c>request.id</c> on every
/// log line, so a log can be joined back to its trace.
/// </summary>
/// <remarks>
/// The field names are the snake_case ones the Node and Go services emit, not
/// Serilog's PascalCase convention. They must match exactly or one Grafana
/// query cannot span all three languages.
/// </remarks>
public sealed class TraceCorrelationEnricher : ILogEventEnricher
{
    /// <inheritdoc/>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var activity = Activity.Current;
        if (activity is null) return;

        if (activity.TraceId != default)
        {
            logEvent.AddPropertyIfAbsent(
                new LogEventProperty("trace_id", new ScalarValue(activity.TraceId.ToString())));
        }

        if (activity.SpanId != default)
        {
            logEvent.AddPropertyIfAbsent(
                new LogEventProperty("span_id", new ScalarValue(activity.SpanId.ToString())));
        }

        var requestId = activity.GetBaggageItem(NetdbTelemetryDefaults.RequestIdBaggageKey);
        if (!string.IsNullOrEmpty(requestId))
        {
            logEvent.AddPropertyIfAbsent(
                new LogEventProperty(
                    NetdbTelemetryDefaults.RequestIdBaggageKey,
                    new ScalarValue(requestId)));
        }
    }
}
