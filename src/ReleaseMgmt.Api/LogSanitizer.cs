using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace ReleaseMgmt.Api;

/// <summary>
/// Security review SEC-D5 (OWASP A05 / ASVS V16 log injection): the file sink writes property values literally (<c>{Message:lj}</c>), so a CR or LF inside a
/// logged value started a new, forged log entry (a webhook channel name, logged on every failed delivery, accepts line breaks). This enricher runs on every
/// event and rewrites any property value that holds a control character
/// (C0, DEL, C1, U+2028/U+2029; tab is kept) to an escaped form (<c>\r</c>, <c>\n</c>, <c>\u001b</c>), so one event is always one line. Message templates are
/// the application's own constants and are left alone; exception stack traces keep their line breaks.
/// </summary>
public sealed class LogSanitizer : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? changed = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            var clean = Clean(value);
            if (!ReferenceEquals(clean, value)) (changed ??= []).Add(new LogEventProperty(name, clean));
        }
        if (changed is null) return;
        foreach (var p in changed) logEvent.AddOrUpdateProperty(p);
    }

    private static LogEventPropertyValue Clean(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string s }:
                return NeedsEscape(s) ? new ScalarValue(Escape(s)) : value;
            case ScalarValue { Value: null or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or DateTime or DateTimeOffset or TimeSpan or Guid }:
                return value;
            case ScalarValue sv:   // PathString, HostString, StringValues...: rendered with ToString(), so that is what must be checked
                var text = sv.Value!.ToString() ?? "";
                return NeedsEscape(text) ? new ScalarValue(Escape(text)) : value;
            case SequenceValue seq:
            {
                var items = seq.Elements.Select(Clean).ToList();
                return items.Where((e, i) => !ReferenceEquals(e, seq.Elements[i])).Any() ? new SequenceValue(items) : value;
            }
            case StructureValue st:
            {
                var props = st.Properties.Select(p => new LogEventProperty(p.Name, Clean(p.Value))).ToList();
                return props.Where((p, i) => !ReferenceEquals(p.Value, st.Properties[i].Value)).Any() ? new StructureValue(props, st.TypeTag) : value;
            }
            case DictionaryValue d:
            {
                var pairs = d.Elements.Select(kv => new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)Clean(kv.Key), Clean(kv.Value))).ToList();
                return pairs.Zip(d.Elements).Any(x => !ReferenceEquals(x.First.Key, x.Second.Key) || !ReferenceEquals(x.First.Value, x.Second.Value)) ? new DictionaryValue(pairs) : value;
            }
            default:
                return value;
        }
    }

    private static bool Bad(char c) => c != (char)9 && (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029);   // tab stays; U+2028/U+2029 are line breaks to many viewers

    public static bool NeedsEscape(string s)
    {
        foreach (var c in s) if (Bad(c)) return true;
        return false;
    }

    /// <summary>CR and LF as <c>\r</c> and <c>\n</c>, every other control character as <c>\uXXXX</c>; a backslash before them is not special (the log is read by people).</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            sb.Append(c switch { '\r' => "\\r", '\n' => "\\n", _ when Bad(c) => $"\\u{(int)c:x4}", _ => c.ToString() });
        return sb.ToString();
    }
}
