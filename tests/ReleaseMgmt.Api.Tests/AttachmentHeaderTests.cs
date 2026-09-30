using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-53 attachment-serving review. Evidence files are user-supplied bytes served from the app's own origin, so every download must be inert:
/// a download (not inline), no sniffing, a content type that is a plain media type or octet-stream, a CSP that would run nothing even if a browser rendered it,
/// no caching, and a file name that cannot break out of the header. (The existing AttachmentTests cover size, hash, lock and orphan rules.)
/// </summary>
public class AttachmentHeaderTests
{
    private static async Task<string> Upload(HttpClient c, byte[] bytes, string fileName, string? contentType)
    {
        var form = new MultipartFormDataContent { { new StringContent("Gate"), "entityType" }, { new StringContent("g1"), "entityId" } };
        var file = new ByteArrayContent(bytes);
        if (contentType is not null) file.Headers.TryAddWithoutValidation("Content-Type", contentType);
        try { form.Add(file, "file", fileName); }
        catch (ArgumentException)   // HttpClient refuses to quote some names; send them RFC 5987-encoded (filename*), which the server also reads
        {
            file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileNameStar = fileName };
            form.Add(file);
        }
        var res = await c.PostAsync("/api/v1/attachments", form);
        Assert.True(res.StatusCode == HttpStatusCode.OK || res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Theory]
    [InlineData("text/html", "text/html")]                              // active types are kept as sent, and made inert by the headers below
    [InlineData("image/svg+xml", "image/svg+xml")]
    [InlineData("application/pdf", "application/pdf")]
    [InlineData("text/html; charset=utf-7", "text/html")]               // parameters are dropped
    [InlineData("TEXT/PLAIN", "text/plain")]
    [InlineData("not a media type", "application/octet-stream")]
    [InlineData("text/html\u0001", "application/octet-stream")]
    [InlineData("*/*", "application/octet-stream")]
    [InlineData("multipart/form-data; boundary=x", "multipart/form-data")]
    [InlineData(null, "application/octet-stream")]
    public async Task A_download_is_an_inert_attachment_whatever_content_type_was_uploaded(string? sent, string served)
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var id = await Upload(rte, Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "evidence.bin", sent);

        using var viewer = await As(f, Roles.Viewer, "v@x.com");
        var d = await viewer.GetAsync($"/api/v1/attachments/{id}");
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(served, d.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", d.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", d.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("default-src 'none'; sandbox", d.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", d.Headers.CacheControl!.ToString());
        Assert.True(d.Headers.CacheControl!.Private);
        Assert.False(d.Headers.Contains("Accept-Ranges"));   // range processing is off: the hash covers the whole file
        Assert.Equal("DENY", d.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("<script>alert(1)</script>", await d.Content.ReadAsStringAsync());   // the bytes are untouched; only the framing is safe
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\win.ini", "win.ini")]
    [InlineData("a\"b.txt", "ab.txt")]
    [InlineData("x;y=z.txt", null)]
    [InlineData("<img src=x onerror=1>.html", null)]
    [InlineData("gnp‮cod.exe", null)]   // right-to-left override would make "exe" look like an image
    [InlineData("....", "attachment")]
    public async Task A_hostile_file_name_cannot_break_out_of_the_Content_Disposition_header(string uploaded, string? expected)
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var id = await Upload(rte, [1, 2, 3], uploaded, "application/octet-stream");

        var d = await rte.GetAsync($"/api/v1/attachments/{id}");
        var raw = string.Join("|", d.Content.Headers.GetValues("Content-Disposition"));
        Assert.StartsWith("attachment", raw);
        Assert.DoesNotContain('\r', raw);
        Assert.DoesNotContain('\n', raw);
        Assert.DoesNotContain("/", raw.Replace("filename*=UTF-8''", ""));
        Assert.DoesNotContain("..", raw);
        Assert.DoesNotContain('‮', raw);
        var name = d.Content.Headers.ContentDisposition!.FileNameStar ?? d.Content.Headers.ContentDisposition.FileName!.Trim('"');
        Assert.DoesNotContain("<", name);
        if (expected is not null) Assert.Equal(expected, name);
        Assert.Single(raw.Split('|'));   // exactly one Content-Disposition header
    }

    [Fact]
    public async Task Download_needs_a_signed_in_user_and_an_unknown_id_is_a_plain_404()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var id = await Upload(rte, [1], "a.txt", "text/plain");
        using var anon = f.CreateClient();
        var res = await anon.GetAsync($"/api/v1/attachments/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Content.Headers.Contains("Content-Disposition"));
        var missing = await rte.GetAsync("/api/v1/attachments/..%2f..%2fappsettings.json");
        Assert.True(missing.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"got {(int)missing.StatusCode}");
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/attachments/x-1")).StatusCode);
    }

    [Fact]
    public async Task Stored_files_are_not_reachable_as_static_files()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var id = await Upload(rte, Encoding.ASCII.GetBytes("static-probe"), "probe.txt", "text/plain");
        using var anon = f.CreateClient();
        foreach (var path in new[] { $"/attachments/{id}", $"/{id}", $"/{id[^2..]}/{id}", "/data/attachments", "/appsettings.json", "/releasemgmt.db" })
        {
            var res = await anon.GetAsync(path);
            Assert.DoesNotContain("static-probe", await res.Content.ReadAsStringAsync());
            Assert.NotEqual("application/json", res.Content.Headers.ContentType?.MediaType);   // not appsettings.json
        }
        Assert.DoesNotContain("wwwroot", f.AttachmentsDir);   // storage is outside the web root
    }
}
