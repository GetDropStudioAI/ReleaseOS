using System.Net;
using System.Net.Http.Json;
using System.Text;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// SEC-E4 (OWASP Top 10:2025 A09, ASVS V16): security-relevant events reach the log. Before this, the Serilog override for Microsoft.AspNetCore (Warning)
/// hid the framework's "Authorization failed" lines, and nothing logged a successful sign-in or a sign-out, so a burst of refused calls or a session that
/// should not exist left no trace.
/// </summary>
public class SecurityEventLogTests
{
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static string LogText(ApiFactory f) => string.Concat(Directory.GetFiles(Path.GetDirectoryName(f.DbPath)!, "log-*.txt").Select(ReadShared));

    [Fact]
    public async Task Sign_in_access_denied_and_sign_out_are_logged_with_who_and_what()
    {
        using var f = new ApiFactory();
        var viewer = f.CreateClient();
        (await viewer.PostAsJsonAsync("/auth/dev-login", new { email = "viewer-log@x.com", name = "v", role = Roles.Viewer })).EnsureSuccessStatusCode();
        var denied = await viewer.PostAsJsonAsync("/api/v1/holidays", new { day = "2026-12-25", name = "Christmas" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        (await viewer.PostAsync("/auth/logout", null)).EnsureSuccessStatusCode();
        var uid = LifecycleContractTests.UserId(f, "viewer-log@x.com");

        string log = "";
        for (var i = 0; i < 50 && !(log.Contains("Signed out") && log.Contains("Access denied")); i++) { await Task.Delay(100); log = LogText(f); }
        Assert.Contains($"Signed in {uid} as Viewer via dev-login", log);
        Assert.Contains($"[WRN] Access denied to {uid} (Viewer) for POST /api/v1/holidays", log);
        Assert.Contains($"Signed out {uid}", log);
    }
}
