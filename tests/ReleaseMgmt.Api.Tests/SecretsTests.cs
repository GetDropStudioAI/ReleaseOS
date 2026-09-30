using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-53 secrets review. Plants recognisable secrets (connector credentials, a webhook address whose path is the token, an ICS feed token), drives the
/// features that touch them, then looks for them everywhere they must not be: every GET response, every SQLite table and file, audit rows, alerts, logs,
/// committed configuration. The one deliberate at-rest exception (the webhook address, WebhookDestinations.Url) is pinned so it cannot spread.
/// </summary>
public class SecretsTests
{
    private const string User = "SENTINEL-USER-f00d", Secret = "SENTINEL-SECRET-c0ffee", Hook = "WEBHOOKSENTINEL9zQ";
    private const string HookUrl = $"https://hooks.slack.com/services/T0AAAAAA/B0BBBBBB/{Hook}";

    private sealed class Fake : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            // a hostile upstream that echoes credentials back in its error body
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent($"{{\"error\":\"bad credentials {User}:{Secret}\",\"authorization\":\"{request.Headers.Authorization}\"}}", Encoding.UTF8, "application/json") });
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool Has(byte[] bytes, string needle) => Encoding.UTF8.GetString(bytes).Contains(needle, StringComparison.Ordinal);

    /// <summary>Every text value in every table, as "table.column: value".</summary>
    private static List<(string Where, string Value)> DumpDatabase(string dbPath)
    {
        var rows = new List<(string, string)>();
        using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\"";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i)) rows.Add(($"{t}.{r.GetName(i)}", r.GetValue(i) is byte[] b ? Encoding.UTF8.GetString(b) : Convert.ToString(r.GetValue(i))!));
        }
        return rows;
    }

    [Fact]
    public async Task Planted_secrets_are_in_no_response_no_table_no_audit_row_no_alert_and_no_log()
    {
        using var f = new ApiFactory();
        var fake = new Fake();
        using var host = f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sync:StartDelaySeconds", "3600");
            b.UseSetting("Sync:WatchdogSeconds", "3600");
            b.ConfigureTestServices(s => s.AddHttpClient(ConnectorHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake));
        });
        async Task<HttpClient> Login(string role, string email)
        {
            var c = host.CreateClient();
            (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
            return c;
        }
        var rm = await Login(Roles.ReleaseManager, "rm@x.com");
        var gov = await Login(Roles.GovernanceOfficer, "gov@x.com");
        SeedTrain(f, UserId(f, "rm@x.com"), UserId(f, "gov@x.com"));

        var responses = new StringBuilder();
        async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null)
        {
            var res = await c.SendAsync(new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) });
            responses.AppendLine(url).AppendLine(await res.Content.ReadAsStringAsync());
            return res;
        }

        // connector credentials, then a test and a sync that fail against an upstream that echoes them
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Put, "/api/v1/connectors/ServiceNow", new { baseUrl = "https://acme.service-now.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Put, "/api/v1/connectors/ServiceNow/credentials", new { kind = "Basic", username = User, secret = Secret })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" })).StatusCode);
        await Send(rm, HttpMethod.Post, "/api/v1/connectors/ServiceNow:test");
        await Send(rm, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");
        Assert.True(fake.Count > 0, "the fake upstream was never called, so the failure paths were not exercised");

        // a webhook address whose path is its token
        var added = await Send(rm, HttpMethod.Post, "/api/v1/sync/webhook-allowlist", new { name = "#release-ops", url = HookUrl });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var hookId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await Send(rm, HttpMethod.Get, "/api/v1/sync/webhook-allowlist");

        // an ICS feed token: shown once, then only its hash exists
        var made = await Send(rm, HttpMethod.Post, "/api/v1/me/ics-tokens", new { scope = "all" });
        var tokenJson = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement;
        var icsToken = tokenJson.GetProperty("token").GetString()!;
        Assert.True(icsToken.Length >= 40);
        using (var anon = host.CreateClient()) Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/v1/ics/{icsToken}/all.ics")).StatusCode);
        var mintBody = await made.Content.ReadAsStringAsync();

        // now read everything a signed-in Release Manager (and a Governance Officer) can read
        var block = responses.Length;
        var endpoints = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true && e.RoutePattern.RawText!.StartsWith("/api/v1/") && !e.RoutePattern.RawText.Contains("/ics/"))
            .Select(e => e.RoutePattern.RawText!).Distinct().ToList();
        Assert.True(endpoints.Count > 60);
        var crawled = 0;
        foreach (var raw in endpoints)
        {
            var url = Regex.Replace(raw, @"\{\*?(\w+)(:[^}]*)?\}", m => m.Groups[1].Value switch { "grid" => "gates", "metric" => "m1", "source" => "ServiceNow", "clientId" => "tab-12345678", "day" => "2026-01-01", _ => "t1" });
            if (url == "/api/v1/attachments") url += "?trainId=t1";
            foreach (var who in new[] { rm, gov })
            {
                var res = await Send(who, HttpMethod.Get, url);
                Assert.True((int)res.StatusCode < 500, $"{url} -> {(int)res.StatusCode}");
                crawled++;
            }
        }
        Assert.True(crawled > 120);
        // and the ones that need a train or job id
        foreach (var url in new[] { "/api/v1/audit?limit=500", "/api/v1/audit.csv", "/api/v1/exports/audit.csv", "/api/v1/exports/connectors.csv", "/api/v1/sync/alerts?state=all", "/api/v1/trains/t1", "/api/v1/trains/t1/links", "/api/v1/me/notifications" })
            await Send(gov, HttpMethod.Get, url);
        await Send(rm, HttpMethod.Delete, $"/api/v1/sync/webhook-allowlist/{hookId}");   // and the removal answer

        var all = responses.ToString().Replace(mintBody, "");   // the ICS token is the one secret a response may carry: in the answer that created it, once
        foreach (var s in new[] { User, Secret, Hook, "hooks.slack.com/services", icsToken, Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}")) })
            Assert.False(all.Contains(s, StringComparison.Ordinal), $"a response carries '{s[..Math.Min(20, s.Length)]}...'");
        Assert.Contains(icsToken, mintBody);

        // ---- SQLite: the tables ------------------------------------------------------------------------------------------------------------
        var rows = DumpDatabase(f.DbPath);
        Assert.True(rows.Count > 100);
        foreach (var s in new[] { User, Secret, icsToken, Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}")) })
            Assert.Empty(rows.Where(r => r.Value.Contains(s, StringComparison.Ordinal)).Select(r => r.Where));
        // the webhook token: nowhere but the allowlist row itself (documented exception, Q-053e), and gone from the row once the destination is removed
        Assert.Empty(rows.Where(r => r.Value.Contains(Hook, StringComparison.Ordinal)).Select(r => r.Where));   // it was removed above
        var audits = rows.Where(r => r.Where.StartsWith("AuditEvents.")).ToList();
        Assert.Contains(audits, r => r.Value.Contains("ConnectorCredentials", StringComparison.Ordinal));      // the credential change *was* audited ...
        Assert.DoesNotContain(audits, r => r.Value.Contains("Secret", StringComparison.OrdinalIgnoreCase) && r.Value.Contains(":", StringComparison.Ordinal) && r.Value.Contains("SENTINEL", StringComparison.Ordinal));   // ... without its values
        // ICS: only a hash is stored (64 hex chars), never the token
        Assert.Matches("^[0-9a-fA-F]{64}$", Scalar(f, "SELECT TokenSha256 FROM IcsTokens LIMIT 1"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM IcsTokens"));   // and it is only a hash of a 256-bit random value, so the stored value is useless as a URL

        // ---- SQLite: the files (WAL included), the credential file, the key ring ---------------------------------------------------------------
        var dir = Path.GetDirectoryName(f.DbPath)!;
        foreach (var file in Directory.GetFiles(dir, "app.db*"))
        {
            var bytes = ReadShared(file);
            foreach (var s in new[] { User, Secret, icsToken }) Assert.False(Has(bytes, s), $"{Path.GetFileName(file)} holds a secret");
        }
        var cred = Path.Combine(dir, "secrets", "ServiceNow.cred");
        Assert.True(File.Exists(cred));
        Assert.False(Has(ReadShared(cred), Secret) || Has(ReadShared(cred), User));   // protected, not merely encoded

        // ---- backups are copies of the database: they inherit its cleanliness, but check one exists and is clean ------------------------------
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "bk"), "*", SearchOption.AllDirectories))
            foreach (var s in new[] { User, Secret, icsToken }) Assert.False(Has(ReadShared(file), s), $"{file} holds a secret");

        // ---- logs -----------------------------------------------------------------------------------------------------------------------------
        var logs = Directory.GetFiles(dir, "log-*.txt");
        var logText = string.Concat(logs.Select(l => Encoding.UTF8.GetString(ReadShared(l))));
        Assert.True(logs.Length > 0 && logText.Length > 0, "no log was written, so the log check proves nothing");
        foreach (var s in new[] { User, Secret, Hook, icsToken, Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}")) })
            Assert.DoesNotContain(s, logText);
    }

    [Fact]
    public async Task A_webhook_address_is_kept_only_in_its_allowlist_row()
    {
        using var f = new ApiFactory();
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var res = await rm.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", new { name = "#ops", url = HookUrl });
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Hook, body);
        Assert.DoesNotContain(Hook, await (await rm.GetAsync("/api/v1/sync/webhook-allowlist")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(Hook, await (await rm.GetAsync("/api/v1/audit?limit=500")).Content.ReadAsStringAsync());
        var where = DumpDatabase(f.DbPath).Where(r => r.Value.Contains(Hook, StringComparison.Ordinal)).Select(r => r.Where).Distinct().ToList();
        Assert.Equal(["WebhookDestinations.Url"], where);   // Q-053e: at rest in SQLite (and so in backups) by design; must not appear anywhere else
    }

    [Fact]
    public async Task Committed_configuration_holds_no_secret_values()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ReleaseMgmt.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root!, "src"), "appsettings*.json", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        Assert.NotEmpty(files);
        var bad = new List<string>();
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            void Walk(JsonElement e, string path)
            {
                if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value, path + ":" + p.Name);
                else if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } v
                         && Regex.IsMatch(path, "secret|password|token|apikey|api-key|credential|connectionstring|privatekey", RegexOptions.IgnoreCase)
                         && !Regex.IsMatch(path, "Directory|Path|Url$", RegexOptions.IgnoreCase)) bad.Add($"{Path.GetFileName(file)} {path}");
            }
            Walk(doc.RootElement, "");
        }
        Assert.True(bad.Count == 0, "Secret-looking configuration values are committed:\n  " + string.Join("\n  ", bad));

        // the OIDC client secret is read from configuration/environment only, and no endpoint reports it
        using var f = new ApiFactory();
        var cfg = await f.CreateClient().GetStringAsync("/auth/config");
        Assert.DoesNotContain("ecret", cfg);
        Assert.DoesNotContain("ecret", await f.CreateClient().GetStringAsync("/healthz"));
    }

    [Fact]
    public async Task An_unexpected_error_does_not_leak_details()
    {
        using var f = new ApiFactory();
        // a server-side failure: the users list against a database that no longer has the table
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        Sql(f, "PRAGMA foreign_keys=OFF; ALTER TABLE Holidays RENAME TO Holidays_gone;");
        var res = await rm.GetAsync("/api/v1/holidays");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.DoesNotContain("Holidays", body);       // no table name
        Assert.DoesNotContain("SqliteException", body);
        Assert.DoesNotContain("   at ", body);        // no stack trace
        Assert.DoesNotContain(f.DbPath, body);
    }
}
