using System.Globalization;
using System.Text.Json;
using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// Jira Cloud REST v3 read connector (OI-4 default): API-token auth (email + token as Basic). Two key shapes (Q-039a):
/// an issue key "PAY-123" reads <c>fields=status</c> only; a fix version "PAY/4.5.0" (project code + version name, how products map to fix versions)
/// reads that project's version list and picks the version by name. Only the status name, released flag and release date are kept (OI-12).
/// </summary>
public sealed class JiraCloudConnector(ConnectorHttp http, ConnectorCredentials credentials) : IConnector
{
    public string SourceSystem => "Jira";
    private string Auth => ConnectorHttp.Basic(credentials.Username, credentials.Secret);

    public async Task<ExternalStatus> FetchStatusAsync(string key, CancellationToken ct)
    {
        if (ExternalKeys.IsJiraVersion(key)) return await FixVersionAsync(key, ct);
        if (!ExternalKeys.IsJiraIssue(key)) throw new ConnectorException(ConnectorErrorKind.NotFound, $"'{key}' is not a Jira issue key or fix version");

        using var doc = await http.GetJsonAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status", Auth, ct);
        var name = Dig(doc.RootElement, "fields", "status", "name");
        if (name is not { ValueKind: JsonValueKind.String }) throw new ConnectorException(ConnectorErrorKind.Parse, "Jira's answer has no status name");
        var state = name.Value.GetString();
        if (!ExternalKeys.IsPlausibleState(state))
            throw new ConnectorException(ConnectorErrorKind.Parse, $"Jira's status name is blank, longer than {ExternalKeys.MaxStateLength} characters or contains control characters; it was not stored");
        return new ExternalStatus(key, state!);
    }

    private async Task<ExternalStatus> FixVersionAsync(string key, CancellationToken ct)
    {
        var slash = key.IndexOf('/');
        var project = key[..slash]; var name = key[(slash + 1)..];
        using var doc = await http.GetJsonAsync($"rest/api/3/project/{Uri.EscapeDataString(project)}/version?maxResults=50&query={Uri.EscapeDataString(name)}", Auth, ct);
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : Dig(doc.RootElement, "values") ?? default;
        if (list.ValueKind != JsonValueKind.Array) throw new ConnectorException(ConnectorErrorKind.Parse, "Jira's answer has no version list");
        foreach (var v in list.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("name", out var n) || !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
            var released = v.TryGetProperty("released", out var r) && r.ValueKind == JsonValueKind.True;
            var archived = v.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
            DateOnly? date = v.TryGetProperty("releaseDate", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd) ? rd : null;
            return new ExternalStatus(key, released ? "Released" : archived ? "Archived" : "Unreleased", ReleaseDate: date, Released: released);
        }
        throw new ConnectorException(ConnectorErrorKind.NotFound, $"Jira has no fix version {key}", 404);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var doc = await http.GetJsonAsync("rest/api/3/myself", Auth, ct);   // the answer names the account; only "it answered" is used
    }

    private static JsonElement? Dig(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out e)) return null;
        }
        return e;
    }
}
