using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>The product owner's security decisions of 2026-09-30 (REOS-72 to REOS-77), at the level of the running app.</summary>
public class SecurityDecisionsTests
{
    private static WebApplicationFactory<Program> With(ApiFactory root, params (string Key, string? Value)[] settings) =>
        root.WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); });

    private static string StartFailure(WebApplicationFactory<Program> f) => Assert.ThrowsAny<Exception>(() => f.CreateClient()).ToString();

    // ---- REOS-72: export retention ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REOS72_an_export_whose_file_was_deleted_after_the_retention_answers_410_with_a_readable_reason_and_stays_listed()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var uid = UserId(f, "rte@x.com");
        var sha = new string('a', 64);
        Sql(f, $$"""
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO ExportJobs(Id,Kind,ReleaseTrainId,Parameters,FileName,Sha256,StoragePath,RequestedByUserId,CreatedAt,CompletedAt,Status,Version)
              VALUES('0199a000-0000-7000-8000-0000000000e1','ReleaseReportPdf','t1','{"format":"pdf","contentType":"application/pdf","sizeBytes":10,"expiredAt":"2027-10-02T00:00:00Z"}','release-report.pdf','{{sha}}',NULL,'{{uid}}',
                     '2026-10-01T00:00:00Z','2026-10-01T00:00:05Z','Done',4);
            """);
        var res = await rte.GetAsync("/api/v1/export-jobs/0199a000-0000-7000-8000-0000000000e1/file");
        Assert.Equal(HttpStatusCode.Gone, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ExportExpired", body.GetProperty("guard").GetString());
        Assert.Contains("365 days", body.GetProperty("message").GetString());
        Assert.Contains("generate the export again", body.GetProperty("message").GetString());
        var job = JsonDocument.Parse(await rte.GetStringAsync("/api/v1/export-jobs/0199a000-0000-7000-8000-0000000000e1")).RootElement;
        Assert.Equal("Done", job.GetProperty("status").GetString());           // the record stays
        Assert.Equal("2027-10-02T00:00:00Z", job.GetProperty("expiredAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rte.DeleteAsync("/api/v1/export-jobs/0199a000-0000-7000-8000-0000000000e1")).StatusCode);   // and still cannot be deleted by hand
    }

    [Fact]
    public void REOS72_a_bad_retention_value_stops_the_start_naming_the_key()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Exports:RetentionDays", "0"));
        Assert.Contains("Exports:RetentionDays", StartFailure(f));
    }

    // ---- REOS-74: the key ring at rest -------------------------------------------------------------------------------------------------------

    private static (string Path, string Password) TestCertificate(string dir)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ReleaseMgmt key ring test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var path = Path.Combine(dir, "keyring.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        return (path, password);
    }

    private static string[] KeyFiles(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "key-*.xml") : [];

    [Fact]
    public void REOS74_outside_Development_an_unprotected_key_ring_refuses_to_start_naming_the_keys()
    {
        using var root = new ApiFactory("Production");
        using var f = With(root, (KeyRingEncryption.AllowUnprotectedKey, "false"));
        if (OperatingSystem.IsWindows())
        {
            f.CreateClient();   // Windows has DPAPI: the ring is protected with the service account's user scope, so the app starts
            f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("x");
            var keys = KeyFiles(Path.Combine(Path.GetDirectoryName(root.DbPath)!, "keys"));
            Assert.NotEmpty(keys);
            Assert.All(keys, k => Assert.Contains("encryptedSecret", File.ReadAllText(k)));
            return;
        }
        var why = StartFailure(f);
        Assert.Contains(KeyRingEncryption.PathKey, why);
        Assert.Contains(KeyRingEncryption.ThumbprintKey, why);
        Assert.Contains(KeyRingEncryption.AllowUnprotectedKey, why);
    }

    [Fact]
    public void REOS74_with_a_certificate_new_keys_are_written_encrypted_and_keys_written_before_stay_readable()
    {
        var dir = Directory.CreateTempSubdirectory("reos-keyring-").FullName;
        try
        {
            var keys = Path.Combine(dir, "keys");
            var (pfx, password) = TestCertificate(dir);

            // 1. a pilot that ran with plain keys protects a value
            string payload;
            using (var plainRoot = new ApiFactory("Production"))
            using (var plain = With(plainRoot, ("DataProtection:KeysDirectory", keys)))
            {
                plain.CreateClient();
                payload = plain.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("still readable");
            }
            var before = KeyFiles(keys);
            Assert.Single(before);
            Assert.DoesNotContain("encryptedSecret", File.ReadAllText(before[0]));

            // 2. the certificate is configured: the old key still reads, and a new key is encrypted with the certificate
            using (var certRoot = new ApiFactory("Production"))
            using (var protectedApp = With(certRoot, ("DataProtection:KeysDirectory", keys), (KeyRingEncryption.AllowUnprotectedKey, "false"),
                       (KeyRingEncryption.PathKey, pfx), (KeyRingEncryption.PasswordKey, password)))
            {
                protectedApp.CreateClient();
                Assert.Equal("still readable", protectedApp.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Unprotect(payload));
                protectedApp.Services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            }
            var added = KeyFiles(keys).Except(before).ToArray();
            var xml = File.ReadAllText(Assert.Single(added));
            Assert.Contains("encryptedSecret", xml);
            Assert.Contains("EncryptedXml", xml);                          // the certificate decryptor ...
            Assert.DoesNotContain("<masterKey", xml);                     // ... and no key material in clear

            // 3. a later start with the same certificate reads the new key
            using var againRoot = new ApiFactory("Production");
            using var again = With(againRoot, ("DataProtection:KeysDirectory", keys), (KeyRingEncryption.PathKey, pfx), (KeyRingEncryption.PasswordKey, password));
            again.CreateClient();
            Assert.All(again.Services.GetRequiredService<IKeyManager>().GetAllKeys(), k => Assert.NotNull(k.Descriptor));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void REOS74_Development_is_unchanged_plain_keys_and_no_certificate_needed()
    {
        using var f = new ApiFactory();
        f.CreateClient();
        f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("x");
        var keys = KeyFiles(Path.Combine(Path.GetDirectoryName(f.DbPath)!, "keys"));
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.DoesNotContain("encryptedSecret", File.ReadAllText(k)));
    }

    [Fact]
    public void REOS74_the_opt_out_is_logged_at_Warning_on_every_start()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows always has DPAPI, so the opt-out is never needed there
        using var f = new ApiFactory("Production");   // ApiFactory opts out for every Production test
        f.CreateClient();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(f.DbPath)!;
        string Log() => string.Concat(Directory.GetFiles(dir, "log-*.txt").Select(p =>
        {
            using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Log().Contains(KeyRingEncryption.AllowUnprotectedKey) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        Assert.Contains($"[WRN] The Data Protection key ring is stored unencrypted ({KeyRingEncryption.AllowUnprotectedKey}=true)", Log());
    }

    [Fact]
    public void REOS74_a_certificate_password_in_an_appsettings_file_and_unusable_settings_are_refused_naming_the_key()
    {
        var dir = Directory.CreateTempSubdirectory("reos-keyring-").FullName;
        try
        {
            var (pfx, password) = TestCertificate(dir);
            var json = Path.Combine(dir, "appsettings.Production.json");
            File.WriteAllText(json, JsonSerializer.Serialize(new { DataProtection = new { Certificate = new { Path = pfx, Password = password } } }));
            static string Refusal(IConfiguration c) =>
                Assert.Throws<InvalidOperationException>(() => KeyRingEncryption.Configure(new ServiceCollection().AddDataProtection(), c, new Env("Production"))).Message;

            var fromFile = Refusal(new ConfigurationBuilder().AddJsonFile(json).Build());
            Assert.Contains(KeyRingEncryption.PasswordKey, fromFile);
            Assert.Contains("appsettings.Production.json", fromFile);

            Assert.Contains(KeyRingEncryption.PasswordKey, Refusal(Mem((KeyRingEncryption.PathKey, pfx), (KeyRingEncryption.PasswordKey, "wrong"))));
            Assert.Contains(KeyRingEncryption.PathKey, Refusal(Mem((KeyRingEncryption.PathKey, Path.Combine(dir, "missing.pfx")))));
            Assert.Contains(KeyRingEncryption.ThumbprintKey, Refusal(Mem((KeyRingEncryption.ThumbprintKey, new string('A', 40)))));
            Assert.Contains("not both", Refusal(Mem((KeyRingEncryption.PathKey, pfx), (KeyRingEncryption.ThumbprintKey, new string('A', 40)))));

            // the same password from the environment (an in-memory source here) is accepted
            var ok = KeyRingEncryption.Configure(new ServiceCollection().AddDataProtection(), Mem((KeyRingEncryption.PathKey, pfx), (KeyRingEncryption.PasswordKey, password)), new Env("Production"));
            Assert.StartsWith("certificate CN=ReleaseMgmt key ring test", ok.Protector);
            Assert.Null(ok.Warning);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static IConfiguration Mem(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ReleaseMgmt.Api";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ---- REOS-75: connector hosts ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REOS75_outside_Development_the_app_defaults_to_Atlassian_and_ServiceNow_and_refuses_another_host_naming_the_key()
    {
        using var prod = new ApiFactory("Production");
        prod.CreateClient();
        Assert.Equal(SyncOptions.DefaultAllowedHosts, prod.Services.GetRequiredService<SyncOptions>().AllowedHosts);
        var r = await prod.Services.GetRequiredService<ConnectorService>().SaveSettingsAsync("Jira", "https://jira.example.com", null, new Actor("someone"), null);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Contains("Sync:AllowedHosts", r.Failures[0].Message);

        using var dev = new ApiFactory();
        dev.CreateClient();
        Assert.Empty(dev.Services.GetRequiredService<SyncOptions>().AllowedHosts);   // Development: any host, so the fakes work
    }

    [Fact]
    public async Task REOS75_saving_a_connector_on_a_host_off_the_list_is_a_readable_422_over_http()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Sync:AllowedHosts", "*.atlassian.net"));
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        var res = await c.PutAsJsonAsync("/api/v1/connectors/Jira", new { baseUrl = "https://jira.example.com" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var msg = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString();
        Assert.Contains("jira.example.com", msg);
        Assert.Contains("Sync:AllowedHosts", msg);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/v1/connectors/Jira", new { baseUrl = "https://acme.atlassian.net" })).StatusCode);
    }

    // ---- REOS-77: operator NAT64 prefixes ------------------------------------------------------------------------------------------------------

    private static OutboundAddressPolicy Policy(params (string Key, string? Value)[] values) => OutboundAddressPolicy.From(Mem(values));

    [Theory]
    [InlineData("2600:1f18:64::a00:5", true)]       // 10.0.0.5 behind the operator's prefix
    [InlineData("2600:1f18:64::7f00:1", true)]      // 127.0.0.1
    [InlineData("2600:1f18:64::a9fe:a9fe", true)]   // 169.254.169.254, cloud metadata
    [InlineData("2600:1f18:64::c0a8:101", true)]    // 192.168.1.1
    [InlineData("2600:1f18:64::6812:1", false)]     // 104.18.0.1, a public address: still reachable through the translator
    [InlineData("2600:1f18:65::a00:5", false)]      // outside the prefix: an ordinary global IPv6 address
    [InlineData("fd00:64::6812:1", false)]          // a ULA prefix is judged by its IPv4 too (not blocked as ULA)
    [InlineData("fd00:64::a00:5", true)]
    public void REOS77_an_address_inside_a_configured_prefix_is_judged_by_its_embedded_IPv4(string address, bool blocked)
    {
        var p = Policy(("Sync:Nat64Prefixes", "2600:1f18:64::/96, fd00:64::/96"));
        Assert.Equal(blocked, p.IsBlocked(IPAddress.Parse(address)));
        // the well-known prefix keeps its rule with or without configuration
        Assert.True(p.IsBlocked(IPAddress.Parse("64:ff9b::a00:5")));
        Assert.False(p.IsBlocked(IPAddress.Parse("64:ff9b::6812:1")));
    }

    [Fact]
    public void REOS77_without_configuration_a_network_specific_prefix_is_invisible_which_is_why_the_key_exists()
    {
        Assert.False(OutboundAddressPolicy.Default.IsBlocked(IPAddress.Parse("2600:1f18:64::a00:5")));
        Assert.True(Policy(("Sync:Nat64Prefixes:0", "2600:1f18:64::/96")).IsBlocked(IPAddress.Parse("2600:1f18:64::a00:5")));   // the array form works too
    }

    [Theory]
    [InlineData("2600:1f18:64::/64")]          // only /96 is supported
    [InlineData("2600:1f18:64::")]             // no length
    [InlineData("2600:1f18:64::1/96")]         // host bits set
    [InlineData("2600:1f18:64:0:ff00::/96")]   // RFC 6052: bits 64-71 must be zero
    [InlineData("10.0.0.0/96")]                // IPv4
    [InlineData("nat64")]
    public void REOS77_a_bad_prefix_is_refused_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Policy(("Sync:Nat64Prefixes", value)));
        Assert.Contains("Sync:Nat64Prefixes", ex.Message);
        Assert.Contains(value, ex.Message);
    }

    [Fact]
    public void REOS77_a_bad_prefix_stops_the_app_from_starting()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Sync:Nat64Prefixes", "2600:1f18:64::/64"));
        Assert.Contains("Sync:Nat64Prefixes", StartFailure(f));
    }

    [Fact]
    public async Task REOS77_the_app_applies_the_prefix_to_webhook_and_connector_addresses_at_save_time()
    {
        using var root = new ApiFactory();
        using var f = With(root, ("Sync:Nat64Prefixes", "2600:1f18:64::/96"));
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();

        var hook = await c.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", new { name = "#ops", url = "https://[2600:1f18:64::a00:5]/hook" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hook.StatusCode);
        Assert.Equal(SyncGuards.WebhookPrivateTarget, JsonDocument.Parse(await hook.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", new { name = "#public", url = "https://[2600:1f18:64::6812:1]/hook" })).StatusCode);

        var conn = await c.PutAsJsonAsync("/api/v1/connectors/Jira", new { baseUrl = "https://[2600:1f18:64::a00:5]" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conn.StatusCode);
        Assert.Contains("private", JsonDocument.Parse(await conn.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString());
        Assert.Same(f.Services.GetRequiredService<OutboundAddressPolicy>(), f.Services.GetRequiredService<OutboundAddressPolicy>());
        Assert.Single(f.Services.GetRequiredService<OutboundAddressPolicy>().Nat64Prefixes);
    }
}
