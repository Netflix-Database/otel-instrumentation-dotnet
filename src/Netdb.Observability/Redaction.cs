using Serilog.Core;
using Serilog.Events;

namespace Netdb.Observability;

/// <summary>
/// Censors secret-bearing properties before they reach any sink.
/// </summary>
/// <remarks>
/// This is an enricher rather than per-sink formatting, so the console and the
/// OTLP sink both see redacted values. Redacting in only one place is how
/// secrets end up in exactly the backend you forgot about.
/// </remarks>
public sealed class RedactingEnricher : ILogEventEnricher
{
    /// <summary>
    /// The needles a property name is matched against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shared redaction contract.</b> Netdb.Observability,
    /// otel-instrumentation-go and otel-instrumentation-next implement the same
    /// four rules. A secret that leaks in one language must leak in all three,
    /// or the contract has drifted and the weakest service decides what ends up
    /// in the log backend:
    /// </para>
    /// <list type="number">
    /// <item>A name is normalised before matching - lowercased, with
    /// <c>-</c>, <c>_</c>, <c>.</c> and space removed. <c>api_key</c>,
    /// <c>apiKey</c>, <c>X-API-KEY</c> and <c>Api.Key</c> all reduce to
    /// <c>apikey</c>. HTTP header names are hyphenated and headers are the most
    /// common accidental leak, so matching only the underscore spelling would
    /// miss them.</item>
    /// <item>The normalised name is substring-matched against this list. The
    /// usual leak is a property that gained a secret months after the logging
    /// call was written, so <c>sessionToken</c> and <c>db_credential</c> match
    /// too.</item>
    /// <item>A match replaces the entire value with <see cref="Placeholder"/>,
    /// whatever the value's type - the contents of a matching key are never
    /// inspected.</item>
    /// <item>Matching applies at every depth up to <see cref="MaxDepth"/>, not
    /// only to top-level properties. Logging <c>{@User}</c> whose nested
    /// property is named <c>password</c> censors that property rather than the
    /// whole object.</item>
    /// </list>
    /// </remarks>
    public static readonly string[] SecretNeedles =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "authorization",
        "cookie",
        "credential",
    ];

    /// <summary>The value substituted for a redacted property.</summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// How far into a destructured value the walk descends. A log line nested
    /// deeper than this is unreadable anyway, and the cap is what makes a
    /// self-referencing value terminate.
    /// </summary>
    public const int MaxDepth = 8;

    private static readonly ScalarValue RedactedValue = new(Placeholder);

    /// <summary>Normalises a property name for matching.</summary>
    internal static string Normalise(string name)
    {
        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (c is '-' or '_' or '.' or ' ') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..length]);
    }

    /// <summary>True when a property with this name must be censored.</summary>
    /// <param name="name">The property name, in any spelling.</param>
    public static bool ShouldRedact(string name)
    {
        var normalised = Normalise(name);
        foreach (var needle in SecretNeedles)
        {
            if (normalised.Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // Collected first: AddOrUpdateProperty cannot run while Properties is
        // being enumerated.
        List<LogEventProperty>? replacements = null;
        foreach (var property in logEvent.Properties)
        {
            var replacement = ShouldRedact(property.Key)
                ? RedactedValue
                : Redact(property.Value, 0);

            if (ReferenceEquals(replacement, property.Value)) continue;
            (replacements ??= []).Add(new LogEventProperty(property.Key, replacement));
        }

        if (replacements is null) return;

        foreach (var replacement in replacements)
        {
            logEvent.AddOrUpdateProperty(replacement);
        }
    }

    /// <summary>
    /// Censors secret-named properties nested inside a destructured value.
    /// </summary>
    /// <remarks>
    /// Returns the value it was given when there is nothing to censor, so a
    /// clean log line keeps its exact structure and the sinks see the same
    /// instances they would have without the enricher. Only a value that
    /// actually contains a secret is rebuilt.
    /// </remarks>
    internal static LogEventPropertyValue Redact(LogEventPropertyValue value, int depth)
    {
        if (depth >= MaxDepth) return value;

        switch (value)
        {
            case StructureValue structure:
            {
                List<LogEventProperty>? rewritten = null;
                var properties = structure.Properties;
                for (var i = 0; i < properties.Count; i++)
                {
                    var property = properties[i];
                    var replacement = ShouldRedact(property.Name)
                        ? RedactedValue
                        : Redact(property.Value, depth + 1);

                    if (ReferenceEquals(replacement, property.Value)) continue;
                    rewritten ??= [.. properties];
                    rewritten[i] = new LogEventProperty(property.Name, replacement);
                }
                return rewritten is null ? structure : new StructureValue(rewritten, structure.TypeTag);
            }

            case DictionaryValue dictionary:
            {
                Dictionary<ScalarValue, LogEventPropertyValue>? rewritten = null;
                foreach (var (key, element) in dictionary.Elements)
                {
                    // A dictionary key is the property name here, exactly as a
                    // map key is in the Go and Node implementations.
                    var name = key.Value as string ?? key.Value?.ToString() ?? string.Empty;
                    var replacement = ShouldRedact(name)
                        ? RedactedValue
                        : Redact(element, depth + 1);

                    if (ReferenceEquals(replacement, element)) continue;
                    rewritten ??= new Dictionary<ScalarValue, LogEventPropertyValue>(dictionary.Elements);
                    rewritten[key] = replacement;
                }
                return rewritten is null ? dictionary : new DictionaryValue(rewritten);
            }

            case SequenceValue sequence:
            {
                // Elements carry no names of their own, but may contain
                // structures that do.
                List<LogEventPropertyValue>? rewritten = null;
                var elements = sequence.Elements;
                for (var i = 0; i < elements.Count; i++)
                {
                    var replacement = Redact(elements[i], depth + 1);
                    if (ReferenceEquals(replacement, elements[i])) continue;
                    rewritten ??= [.. elements];
                    rewritten[i] = replacement;
                }
                return rewritten is null ? sequence : new SequenceValue(rewritten);
            }

            default:
                // A ScalarValue has no names inside it to match against.
                return value;
        }
    }
}
