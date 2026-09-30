using System.Globalization;
using System.Text.Json;
using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// ServiceNow Table API read connector (OI-3 default): <c>change_request</c> (CHG...) and <c>change_task</c> (CTASK...). OAuth client-credentials
/// (token fetched once per connector instance, i.e. once per poll cycle) with Basic auth as the fallback the admin chooses by credential kind.
/// Only the number, state and planned start/end are requested (<c>sysparm_fields</c>), so no summary, description or person ever leaves ServiceNow (OI-12).
/// The token request is a POST to the OAuth endpoint; it changes nothing in the change process.
/// </summary>
public sealed class ServiceNowConnector(ConnectorHttp http, ConnectorCredentials credentials, TimeProvider time, string tokenPath = "oauth_token.do") : IConnector
{
    public string SourceSystem => "ServiceNow";
    private string? _bearer;
    private DateTime _bearerUntil;

    public async Task<ExternalStatus> FetchStatusAsync(string key, CancellationToken ct)
    {
        var task = key.StartsWith("CTASK", StringComparison.Ordinal);
        if (!task && !key.StartsWith("CHG", StringComparison.Ordinal)) throw new ConnectorException(ConnectorErrorKind.NotFound, $"'{key}' is not a ServiceNow change or change task number");
        var table = task ? "change_task" : "change_request";
        var fields = task ? "number,state" : "number,state,start_date,end_date";
        using var doc = await http.GetJsonAsync(
            $"api/now/table/{table}?sysparm_query={Uri.EscapeDataString("number=" + key)}&sysparm_fields={fields}&sysparm_limit=1&sysparm_exclude_reference_link=true",
            await AuthAsync(ct), ct);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) throw new ConnectorException(ConnectorErrorKind.Parse, "ServiceNow's answer has no result list");
        if (result.GetArrayLength() == 0) throw new ConnectorException(ConnectorErrorKind.NotFound, $"ServiceNow has no {key}", 404);
        var row = result[0];
        var raw = Str(row, "state") ?? throw new ConnectorException(ConnectorErrorKind.Parse, "ServiceNow's answer has no state");
        return new ExternalStatus(key, task ? ExternalKeys.ChangeTaskStateName(raw) : ExternalKeys.ChangeStateName(raw), Date(Str(row, "start_date")), Date(Str(row, "end_date")));
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var doc = await http.GetJsonAsync("api/now/table/change_request?sysparm_limit=1&sysparm_fields=number", await AuthAsync(ct), ct);
        if (!doc.RootElement.TryGetProperty("result", out _)) throw new ConnectorException(ConnectorErrorKind.Parse, "ServiceNow's answer has no result list");
    }

    private async Task<string> AuthAsync(CancellationToken ct)
    {
        if (credentials.Kind != ConnectorCredentials.OAuth) return ConnectorHttp.Basic(credentials.Username, credentials.Secret);
        var now = time.GetUtcNow().UtcDateTime;
        if (_bearer is not null && now < _bearerUntil) return "Bearer " + _bearer;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = credentials.Username, ["client_secret"] = credentials.Secret,
        });
        JsonDocument doc;
        try { doc = await http.SendJsonAsync(HttpMethod.Post, tokenPath, form, null, ct); }
        catch (ConnectorException ex) when (ex.HttpStatus is 400 or 401 or 403)
        {
            throw new ConnectorException(ConnectorErrorKind.Auth, $"ServiceNow refused the OAuth client (HTTP {ex.HttpStatus})", ex.HttpStatus);
        }
        using (doc)
        {
            var token = Str(doc.RootElement, "access_token") ?? throw new ConnectorException(ConnectorErrorKind.Auth, "ServiceNow's OAuth answer has no access token");
            var life = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 60;
            _bearer = token; _bearerUntil = now.AddSeconds(Math.Max(10, life - 30));
            return "Bearer " + token;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    /// <summary>ServiceNow returns UTC "yyyy-MM-dd HH:mm:ss" (empty when unset).</summary>
    private static DateTime? Date(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTime.TryParseExact(s, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
