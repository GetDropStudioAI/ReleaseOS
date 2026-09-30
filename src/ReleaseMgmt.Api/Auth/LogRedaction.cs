using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-B10: a calendar feed's secret is a path segment (<c>/api/v1/ics/{token}...</c>). The <c>Microsoft.AspNetCore</c> Warning override keeps request lines out
/// of the log only while nobody raises it, and the hosting log scope puts <c>RequestPath</c> on every event of the request for any formatter that prints
/// properties. This enricher rewrites the segment in every string property (scalars, and inside sequences and structures) before a sink sees the event.
/// Not covered: text baked into a message template by string interpolation, and exception messages (nothing in this app puts the path in either).
/// </summary>
public sealed partial class IcsTokenRedactor : ILogEventEnricher
{
    public const string Mask = "(redacted)";

    [GeneratedRegex(@"(/ics/)[^/?#\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    public static string Redact(string s) => s.Contains("/ics/", StringComparison.OrdinalIgnoreCase) ? Segment().Replace(s, "$1" + Mask) : s;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? changed = null;
        foreach (var (name, value) in logEvent.Properties)
            if (Clean(value) is { } clean) (changed ??= []).Add(new LogEventProperty(name, clean));
        if (changed is not null) foreach (var p in changed) logEvent.AddOrUpdateProperty(p);
    }

    /// <summary>The value with every token masked, or null when nothing needed masking.</summary>
    private static LogEventPropertyValue? Clean(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string s } when Redact(s) is var r && r != s:
                return new ScalarValue(r);
            case SequenceValue seq:
            {
                var items = seq.Elements.Select(e => Clean(e)).ToList();
                return items.All(i => i is null) ? null : new SequenceValue(seq.Elements.Select((e, i) => items[i] ?? e));
            }
            case StructureValue st:
            {
                var props = st.Properties.Select(p => Clean(p.Value)).ToList();
                return props.All(p => p is null) ? null : new StructureValue(st.Properties.Select((p, i) => props[i] is { } c ? new LogEventProperty(p.Name, c) : p), st.TypeTag);
            }
            default:
                return null;
        }
    }
}
