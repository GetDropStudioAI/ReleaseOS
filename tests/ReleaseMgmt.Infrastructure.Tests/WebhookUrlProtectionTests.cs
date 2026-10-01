using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-73 / Q-053e (decided 2026-09-30): webhook URLs are stored encrypted, unique by keyed hash; existing plaintext rows are converted at start-up.</summary>
public sealed class WebhookUrlProtectionTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Token = "PLAINTEXTTOKEN7Qx", UrlA = $"https://hooks.slack.com/services/T0/B0/{Token}", UrlB = "https://example.webhook.office.com:8443/webhookb2/abc";

    // The table as db/schema.sql defined it before 2026-09-30.
    private const string LegacyDdl = """
        CREATE TABLE WebhookDestinations (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Url TEXT NOT NULL UNIQUE CHECK (Url LIKE 'https://%'),
            Kind TEXT NOT NULL CHECK (Kind IN ('Teams','Slack','Generic')),
            Version INTEGER NOT NULL DEFAULT 1
        );
        """;

    private static DataProtectionWebhookUrlVault Vault(IDataProtectionProvider dp, string secretsDir) =>
        new(dp, SyncOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:Credentials:Directory"] = secretsDir }).Build()));

    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static object? Scalar(string path, string sql)
    {
        using var c = TriggerSuiteFixture.Open(path); using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private string LegacyDatabase()
    {
        var path = fx.FreshPath();
        using var c = TriggerSuiteFixture.Open(path);
        TriggerSuiteFixture.Run(c, "PRAGMA foreign_keys=OFF; DROP TABLE WebhookDestinations;");
        TriggerSuiteFixture.Run(c, LegacyDdl);
        TriggerSuiteFixture.Run(c, "PRAGMA foreign_keys=ON;");
        TriggerSuiteFixture.Run(c, $"""
            INSERT INTO WebhookDestinations(Id,Name,Url,Kind,Version) VALUES('w1','#release-ops','{UrlA}','Slack',1),('w2','Platform','{UrlB}','Teams',3);
            INSERT INTO Teams(Id,Handle,Name,WebhookDestinationId) VALUES('tm1','platform','Platform','w2');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','s','b');
            INSERT INTO CommDispatches(Id,CommTemplateId,Channel,WebhookDestinationId,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome)
              VALUES('d1','c1','Webhook','w1','s','b','rte','2026-09-29T10:00:00Z','Delivered');
            """);
        return path;
    }

    [Fact]
    public async Task A_database_with_plaintext_addresses_is_converted_once_to_the_schema_sql_table_and_keeps_its_references()
    {
        var path = LegacyDatabase();
        var dp = new EphemeralDataProtectionProvider();
        var vault = Vault(dp, Path.Combine(fx.Dir, "s-" + Guid.NewGuid().ToString("N")[..8]));
        var teamsDdl = Scalar(path, "SELECT sql FROM sqlite_master WHERE name='Teams'");

        var r = await WebhookUrlProtectionUpgrade.RunAsync($"Data Source={path};Pooling=False", vault, Now, NullLogger.Instance);
        Assert.Equal(2, r.Protected);

        // the table is now exactly db/schema.sql's, so the schema contract holds for an upgraded database as for a new one
        var expected = SchemaSql.TablesAndIndexes().Single(s => s.StartsWith("CREATE TABLE WebhookDestinations ", StringComparison.Ordinal));
        Assert.Equal(expected.TrimEnd(';'), Scalar(path, "SELECT sql FROM sqlite_master WHERE name='WebhookDestinations'"));
        Assert.Equal(teamsDdl, Scalar(path, "SELECT sql FROM sqlite_master WHERE name='Teams'"));   // its REFERENCES clause was not rewritten
        Assert.Null(Scalar(path, "SELECT name FROM sqlite_master WHERE name LIKE 'WebhookDestinations_%'"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("w2", Scalar(path, "SELECT d.Id FROM Teams t JOIN WebhookDestinations d ON d.Id=t.WebhookDestinationId"));
        Assert.Equal("w1", Scalar(path, "SELECT d.Id FROM CommDispatches x JOIN WebhookDestinations d ON d.Id=x.WebhookDestinationId"));

        // each row: encrypted, readable back by the vault, hashed with the vault's key, host kept for display, Version bumped
        foreach (var (id, url, host, version) in new[] { ("w1", UrlA, "hooks.slack.com", 2L), ("w2", UrlB, "example.webhook.office.com:8443", 4L) })
        {
            var stored = (string)Scalar(path, $"SELECT ProtectedUrl FROM WebhookDestinations WHERE Id='{id}'")!;
            Assert.StartsWith("CfDJ8", stored);
            Assert.Equal(url, vault.Unprotect(stored));
            Assert.Equal(vault.Hmac(url), Scalar(path, $"SELECT UrlHmac FROM WebhookDestinations WHERE Id='{id}'"));
            Assert.Equal(host, Scalar(path, $"SELECT Host FROM WebhookDestinations WHERE Id='{id}'"));
            Assert.Equal(version, Scalar(path, $"SELECT Version FROM WebhookDestinations WHERE Id='{id}'"));
            Assert.Equal(1L, Scalar(path, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination' AND EntityId='{id}' AND Action='ProtectUrl' AND OccurredAt='2026-10-01T09:00:00Z'"));
        }
        Assert.Equal(0L, Scalar(path, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{Token}%' OR AfterJson LIKE '%/services/%'"));

        // the plaintext is gone from the files too (secure_delete + checkpoint), not just from the table
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            Assert.DoesNotContain(Token, Encoding.UTF8.GetString(File.ReadAllBytes(file)));

        // idempotent: a second start changes nothing
        var again = await WebhookUrlProtectionUpgrade.RunAsync($"Data Source={path};Pooling=False", vault, Now.AddHours(1), NullLogger.Instance);
        Assert.Equal((0, 0), (again.Protected, again.Rehashed));
        Assert.Equal(2L, Scalar(path, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination'"));

        // and the database now refuses a plaintext address and a second row for the same address
        using var c = TriggerSuiteFixture.Open(path);
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() => TriggerSuiteFixture.Run(c,
            $"INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('w3','x','hooks.slack.com','{UrlA}','{new string('c', 64)}','Slack')")).Message);
        var dup = vault.Protect(UrlA);
        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => TriggerSuiteFixture.Run(c,
            $"INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('w3','x','hooks.slack.com','{dup.ProtectedUrl}','{dup.UrlHmac}','Slack')")).Message);
    }

    [Fact]
    public async Task A_lost_address_key_is_recreated_and_every_readable_row_is_rehashed_the_unreadable_are_reported()
    {
        var path = fx.FreshPath();
        var dp = new EphemeralDataProtectionProvider();
        var secrets = Path.Combine(fx.Dir, "s-" + Guid.NewGuid().ToString("N")[..8]);
        var first = Vault(dp, secrets);
        var p = first.Protect(UrlA);
        using (var c = TriggerSuiteFixture.Open(path))
            TriggerSuiteFixture.Run(c, $"""
                INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('w1','#ops','hooks.slack.com','{p.ProtectedUrl}','{p.UrlHmac}','Slack');
                INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('w2','#broken','hooks.slack.com','CfDJ8notreadable','{new string('d', 64)}','Slack');
                """);

        // a start with the key present: nothing to do
        Assert.Equal(0, (await WebhookUrlProtectionUpgrade.RunAsync($"Data Source={path};Pooling=False", Vault(dp, secrets), Now, NullLogger.Instance)).Rehashed);

        File.Delete(Path.Combine(secrets, DataProtectionWebhookUrlVault.HmacKeyFile));   // restored without secrets/
        var second = Vault(dp, secrets);
        var r = await WebhookUrlProtectionUpgrade.RunAsync($"Data Source={path};Pooling=False", second, Now, NullLogger.Instance);
        Assert.Equal(1, r.Rehashed);
        Assert.Equal(["#broken"], r.Unreadable);
        Assert.NotEqual(p.UrlHmac, second.Hmac(UrlA));                 // a new key ...
        Assert.Equal(second.Hmac(UrlA), Scalar(path, "SELECT UrlHmac FROM WebhookDestinations WHERE Id='w1'"));   // ... and the row follows it, so duplicates are still caught
        Assert.Equal(2L, Scalar(path, "SELECT Version FROM WebhookDestinations WHERE Id='w1'"));
        Assert.Equal(1L, Scalar(path, "SELECT COUNT(*) FROM AuditEvents WHERE EntityId='w1' AND Action='RehashUrl'"));
    }

    [Fact]
    public void The_vault_refuses_a_key_file_it_cannot_read_rather_than_silently_replacing_it()
    {
        var secrets = Path.Combine(fx.Dir, "s-" + Guid.NewGuid().ToString("N")[..8]);
        Vault(new EphemeralDataProtectionProvider(), secrets).Protect(UrlA);   // key written under one key ring ...
        var other = Vault(new EphemeralDataProtectionProvider(), secrets);     // ... read under another
        var ex = Assert.Throws<WebhookUrlUnreadableException>(() => other.Hmac(UrlA));
        Assert.Contains("Restore keys/ and secrets/ together", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Throws<WebhookUrlUnreadableException>(() => other.Unprotect("CfDJ8notreadable"));
    }
}
