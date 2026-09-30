using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-35: evidence attachments. Server-side SHA-256, size cap, storage outside wwwroot, lock on certify, audit, no orphans.</summary>
public class AttachmentTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static MultipartFormDataContent Form(string entityType, string entityId, byte[] bytes, string fileName = "evidence.txt", string contentType = "text/plain", string? clientHash = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(entityType), "entityType");
        form.Add(new StringContent(entityId), "entityId");
        if (clientHash is not null) form.Add(new StringContent(clientHash), "sha256");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static Task<HttpResponseMessage> Upload(HttpClient c, string entityType, string entityId, byte[] bytes, string fileName = "evidence.txt", string contentType = "text/plain", string? clientHash = null) =>
        c.PostAsync("/api/v1/attachments", Form(entityType, entityId, bytes, fileName, contentType, clientHash));

    private static byte[] Abc => Encoding.ASCII.GetBytes("abc");
    private const string AbcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // FIPS 180-2 test vector

    private static int FilesOnDisk(ApiFactory f) =>
        Directory.Exists(f.AttachmentsDir) ? Directory.EnumerateFiles(f.AttachmentsDir, "*", SearchOption.AllDirectories).Count() : 0;

    private static async Task<(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer)> Setup(long? maxBytes = null)
    {
        var f = new ApiFactory(attachmentMaxBytes: maxBytes);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
        return (f, rte, rm, gov, viewer);
    }

    /// <summary>Starts g1, completes its task and certifies it as the RTE (the DB trigger locks the gate's and task's attachments).</summary>
    private static async Task CertifyG1(HttpClient rte)
    {
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/gates/g1:start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/tasks/k1:complete", new { })).StatusCode);
        var certify = await rte.PostAsJsonAsync("/api/v1/gates/g1:certify", new { });
        Assert.True(certify.StatusCode == HttpStatusCode.OK, await certify.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_stores_the_file_outside_wwwroot_with_a_server_computed_sha256_and_audits_it()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        // The client-sent hash is a lie; the server must ignore it.
        var r = await Upload(rte, "Gate", "g1", Abc, clientHash: new string('0', 64));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.Equal(AbcSha, body.GetProperty("sha256").GetString());
        Assert.Equal(3, body.GetProperty("sizeBytes").GetInt64());
        Assert.False(body.GetProperty("isLocked").GetBoolean());
        Assert.Equal("t1", body.GetProperty("releaseTrainId").GetString());
        Assert.Equal(AbcSha, Scalar(f, "SELECT Sha256 FROM Attachments"));

        var stored = Scalar(f, "SELECT StoragePath FROM Attachments");
        Assert.False(Path.IsPathRooted(stored));
        var full = Path.Combine(f.AttachmentsDir, stored);
        Assert.True(File.Exists(full));
        Assert.Equal(AbcSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))));
        Assert.DoesNotContain(f.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().WebRootPath, Path.GetFullPath(full));   // not under wwwroot
        Assert.Equal(1, FilesOnDisk(f));

        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Attachment' AND Action='Upload' AND ReleaseTrainId='t1' AND AfterJson LIKE '%" + AbcSha + "%'"));
        Assert.Equal(UserId(f, "rte@x.com"), Scalar(f, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='Attachment' AND Action='Upload'"));
    }

    [Fact]
    public async Task Upload_works_for_a_train_a_gate_and_a_task_and_refuses_unknown_targets()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        foreach (var (t, id) in new[] { ("Train", "t1"), ("Gate", "g1"), ("Task", "k1") })
            Assert.Equal(HttpStatusCode.OK, (await Upload(rte, t, id, Abc)).StatusCode);
        Assert.Equal("3", Scalar(f, "SELECT COUNT(*) FROM Attachments WHERE ReleaseTrainId='t1'"));
        var missing = await Upload(rte, "Gate", "nope", Abc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("AttachmentInvalidEntity", (await Json(missing)).GetProperty("guard").GetString());
        var unsupported = await Upload(rte, "Blocker", "x", Abc);
        Assert.Equal("AttachmentInvalidEntity", (await Json(unsupported)).GetProperty("guard").GetString());
        Assert.Equal(3, FilesOnDisk(f));
    }

    [Fact]
    public async Task Empty_files_and_bad_multipart_are_refused_with_a_readable_message_and_leave_nothing()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var empty = await Upload(rte, "Gate", "g1", []);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("AttachmentEmpty", (await Json(empty)).GetProperty("guard").GetString());
        var json = await rte.PostAsJsonAsync("/api/v1/attachments", new { });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        // File part before the fields: refused, not guessed at.
        var wrong = new MultipartFormDataContent { { new ByteArrayContent(Abc), "file", "a.txt" }, { new StringContent("Gate"), "entityType" }, { new StringContent("g1"), "entityId" } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rte.PostAsync("/api/v1/attachments", wrong)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(0, FilesOnDisk(f));
    }

    [Fact]
    public async Task Files_over_the_limit_are_refused_413_with_a_message_and_nothing_is_left_behind()
    {
        var (f, rte, _, _, _) = await Setup(maxBytes: 1024); using var _f = f;
        var atLimit = await Upload(rte, "Gate", "g1", new byte[1024]);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        var over = await Upload(rte, "Gate", "g1", new byte[1025]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        var body = await Json(over);
        Assert.Equal("AttachmentTooLarge", body.GetProperty("guard").GetString());
        Assert.Contains("limit", body.GetProperty("message").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(1, FilesOnDisk(f));   // the accepted one only; the temp file of the refused one is gone
    }

    [Fact]
    public async Task The_default_limit_is_50_MB_exactly()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var ok = await Upload(rte, "Train", "t1", new byte[52_428_800]);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var over = await Upload(rte, "Train", "t1", new byte[52_428_801]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(1, FilesOnDisk(f));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/abs/path/report.pdf", "report.pdf")]
    [InlineData("b<c>d|e?.txt", "bcde.txt")]
    [InlineData("...", "attachment")]
    public async Task Traversal_and_odd_file_names_are_sanitised_and_never_decide_where_the_file_is_stored(string sent, string expected)
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var r = await Upload(rte, "Gate", "g1", Abc, sent);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(expected, (await Json(r)).GetProperty("fileName").GetString());
        var stored = Scalar(f, "SELECT StoragePath FROM Attachments");
        Assert.DoesNotContain("..", stored);
        var full = Path.GetFullPath(Path.Combine(f.AttachmentsDir, stored));
        Assert.StartsWith(Path.GetFullPath(f.AttachmentsDir) + Path.DirectorySeparatorChar, full);
        Assert.True(File.Exists(full));
    }

    [Fact]
    public void Sanitiser_handles_control_characters_bidi_overrides_and_length()
    {
        Assert.Equal("ab.txt", AttachmentService.SanitizeFileName("a\r\nb\u202E.txt"));
        Assert.Equal("attachment", AttachmentService.SanitizeFileName(null));
        var longName = AttachmentService.SanitizeFileName(new string('x', 500) + ".pdf");
        Assert.Equal(180, longName.Length);
        Assert.EndsWith(".pdf", longName);
        Assert.Equal("application/octet-stream", AttachmentService.SafeContentType("not a type"));
        Assert.Equal("text/plain", AttachmentService.SafeContentType("Text/Plain; charset=utf-8"));
    }

    [Fact]
    public async Task Download_serves_the_bytes_as_an_attachment_with_nosniff_and_needs_a_signed_in_user()
    {
        var (f, rte, _, _, viewer) = await Setup(); using var _f = f;
        var id = (await Json(await Upload(rte, "Gate", "g1", Abc, "../notes q.html", "text/html"))).GetProperty("id").GetString()!;

        var d = await viewer.GetAsync($"/api/v1/attachments/{id}");   // any signed-in role may read evidence
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(Abc, await d.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", d.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(AbcSha, d.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Contains("no-store", d.Headers.CacheControl!.ToString());
        Assert.Equal("attachment", d.Content.Headers.ContentDisposition!.DispositionType);
        Assert.DoesNotContain("..", d.Content.Headers.ContentDisposition!.ToString());
        Assert.DoesNotContain("/", d.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("text/html", d.Content.Headers.ContentType!.MediaType);   // safe only because of attachment + nosniff

        using var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/attachments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/attachments/nope")).StatusCode);
    }

    [Fact]
    public async Task A_row_whose_file_vanished_is_a_404_and_raises_an_alert_it_is_not_silent()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var id = (await Json(await Upload(rte, "Gate", "g1", Abc))).GetProperty("id").GetString()!;
        File.Delete(Path.Combine(f.AttachmentsDir, Scalar(f, "SELECT StoragePath FROM Attachments")));
        var d = await rte.GetAsync($"/api/v1/attachments/{id}");
        Assert.Equal(HttpStatusCode.NotFound, d.StatusCode);
        Assert.Contains("not found", (await Json(d)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Roles_viewer_cannot_upload_or_delete_everyone_can_list_and_download()
    {
        var (f, rte, rm, gov, viewer) = await Setup(); using var _f = f;
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(viewer, "Gate", "g1", Abc)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(0, FilesOnDisk(f));
        var id = (await Json(await Upload(gov, "Gate", "g1", Abc))).GetProperty("id").GetString()!;   // a Governance Officer attaches evidence

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v1/attachments/{id}")).StatusCode);
        var list = await viewer.GetAsync("/api/v1/trains/t1/attachments?entityType=Gate&entityId=g1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await Json(list)).EnumerateArray());

        // A Governance Officer may delete only what they uploaded; an RTE or Release Manager may delete anyone's.
        var rteOwn = (await Json(await Upload(rte, "Gate", "g1", Abc))).GetProperty("id").GetString()!;
        var refused = await gov.DeleteAsync($"/api/v1/attachments/{rteOwn}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("AttachmentRole", (await Json(refused)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rm.DeleteAsync($"/api/v1/attachments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.DeleteAsync($"/api/v1/attachments/{rteOwn}")).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_row_and_file_together_and_is_audited_with_the_before_image()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var id = (await Json(await Upload(rte, "Gate", "g1", Abc, "keep.txt"))).GetProperty("id").GetString()!;
        Assert.Equal(1, FilesOnDisk(f));
        Assert.Equal(HttpStatusCode.OK, (await rte.DeleteAsync($"/api/v1/attachments/{id}")).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(0, FilesOnDisk(f));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Attachment' AND EntityId='{id}' AND Action='Delete' AND BeforeJson LIKE '%keep.txt%' AND BeforeJson LIKE '%{AbcSha}%'"));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.DeleteAsync($"/api/v1/attachments/{id}")).StatusCode);
    }

    [Fact]
    public async Task Certifying_the_gate_locks_its_gate_and_task_evidence_and_locked_evidence_cannot_be_deleted_or_added()
    {
        var (f, rte, rm, _, _) = await Setup(); using var _f = f;
        var onGate = (await Json(await Upload(rte, "Gate", "g1", Abc, "gate.txt"))).GetProperty("id").GetString()!;
        var onTask = (await Json(await Upload(rte, "Task", "k1", Abc, "task.txt"))).GetProperty("id").GetString()!;
        var onTrain = (await Json(await Upload(rte, "Train", "t1", Abc, "train.txt"))).GetProperty("id").GetString()!;
        await CertifyG1(rte);

        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM Attachments WHERE IsLocked=1"));
        Assert.Equal("0", Scalar(f, $"SELECT IsLocked FROM Attachments WHERE Id='{onTrain}'"));   // train-level evidence is not gate evidence
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='StageGate' AND EntityId='g1' AND Action='EvidenceLocked'"));   // the trigger's own audit row

        foreach (var id in new[] { onGate, onTask })
        {
            var del = await rm.DeleteAsync($"/api/v1/attachments/{id}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, del.StatusCode);
            var b = await Json(del);
            Assert.Equal("AttachmentLocked", b.GetProperty("guard").GetString());
            Assert.Contains("locked", b.GetProperty("message").GetString());
        }
        var add = await Upload(rte, "Gate", "g1", Abc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
        Assert.Equal("AttachmentLocked", (await Json(add)).GetProperty("guard").GetString());
        Assert.Equal("AttachmentLocked", (await Json(await Upload(rte, "Task", "k1", Abc))).GetProperty("guard").GetString());

        Assert.Equal("3", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(3, FilesOnDisk(f));
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync($"/api/v1/attachments/{onGate}")).StatusCode);   // locked evidence is still readable
        Assert.Equal(HttpStatusCode.OK, (await rm.DeleteAsync($"/api/v1/attachments/{onTrain}")).StatusCode);

        var manifest = await Json(await rte.GetAsync("/api/v1/trains/t1/attachments/manifest"));
        Assert.Equal(2, manifest.GetArrayLength());
        Assert.All(manifest.EnumerateArray(), m => Assert.True(m.GetProperty("isLocked").GetBoolean()));
    }

    [Fact]
    public async Task The_database_trigger_is_the_backstop_locked_rows_cannot_be_updated_or_deleted_even_around_the_service()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var id = (await Json(await Upload(rte, "Gate", "g1", Abc))).GetProperty("id").GetString()!;
        await CertifyG1(rte);
        var del = Assert.Throws<SqliteException>(() => Sql(f, $"DELETE FROM Attachments WHERE Id='{id}'"));
        Assert.Contains("Attachment is locked evidence", del.Message);
        var upd = Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE Attachments SET FileName='renamed.txt' WHERE Id='{id}'"));
        Assert.Contains("Attachment is locked evidence", upd.Message);
        Assert.Equal("evidence.txt", Scalar(f, $"SELECT FileName FROM Attachments WHERE Id='{id}'"));
    }

    [Fact]
    public async Task A_trigger_abort_that_slips_past_the_service_check_is_a_DbRule_422_and_leaves_no_file()
    {
        // Race: the gate is certified between the service's check and its write. Simulated by locking the row after the read, through a trigger-visible change.
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var id = (await Json(await Upload(rte, "Gate", "g1", Abc))).GetProperty("id").GetString()!;
        // Make the service's IsLocked read see 0 but the DB row locked: an AFTER-read change is not reproducible over HTTP, so drive the delete against a row locked by a BEFORE trigger.
        Sql(f, "CREATE TRIGGER trg_test_race BEFORE DELETE ON Attachments BEGIN SELECT RAISE(ABORT, 'Attachment is locked evidence'); END;");
        var del = await rte.DeleteAsync($"/api/v1/attachments/{id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, del.StatusCode);
        var b = await Json(del);
        Assert.Equal("DbRule", b.GetProperty("guard").GetString());
        Assert.Contains("locked", b.GetProperty("message").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(1, FilesOnDisk(f));   // the file is only removed after the row is gone
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Attachment' AND Action='Delete'"));   // rolled back with the row
    }

    [Fact]
    public async Task A_failed_database_write_leaves_no_orphan_file_and_no_row()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        // Any insert into Attachments now aborts, as a constraint or trigger would.
        Sql(f, "CREATE TRIGGER trg_test_fail BEFORE INSERT ON Attachments BEGIN SELECT RAISE(ABORT, 'simulated rule'); END;");
        var r = await Upload(rte, "Gate", "g1", Abc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("DbRule", (await Json(r)).GetProperty("guard").GetString());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Attachment'"));
        Assert.Equal(0, FilesOnDisk(f));
    }

    [Fact]
    public async Task A_client_that_aborts_mid_upload_leaves_no_file_behind()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var content = new StreamContent(new ThrowingStream(new byte[300_000]));
        var bad = new MultipartFormDataContent { { new StringContent("Gate"), "entityType" }, { new StringContent("g1"), "entityId" }, { content, "file", "big.bin" } };
        await Assert.ThrowsAnyAsync<Exception>(() => rte.PostAsync("/api/v1/attachments", bad));
        await Task.Delay(300);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM Attachments"));
        Assert.Equal(0, FilesOnDisk(f));
    }

    private sealed class ThrowingStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length / 2) throw new IOException("connection reset");
            var n = Math.Min(count, Math.Min(65536, data.Length - _pos)); Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Manifest_lists_name_size_sha256_locked_uploader_and_time()
    {
        var (f, rte, _, gov, _) = await Setup(); using var _f = f;
        await Upload(rte, "Gate", "g1", Abc, "a.txt");
        await Upload(gov, "Train", "t1", Encoding.ASCII.GetBytes("hello"), "b.txt");
        var m = await Json(await rte.GetAsync("/api/v1/trains/t1/attachments/manifest"));
        Assert.Equal(2, m.GetArrayLength());
        var a = m.EnumerateArray().Single(x => x.GetProperty("fileName").GetString() == "a.txt");
        Assert.Equal(AbcSha, a.GetProperty("sha256").GetString());
        Assert.Equal(3, a.GetProperty("sizeBytes").GetInt64());
        Assert.False(a.GetProperty("isLocked").GetBoolean());
        Assert.Equal("rte", a.GetProperty("uploadedByName").GetString());
        Assert.Equal(UserId(f, "rte@x.com"), a.GetProperty("uploadedByUserId").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", a.GetProperty("uploadedAt").GetString()!);
        var b = m.EnumerateArray().Single(x => x.GetProperty("fileName").GetString() == "b.txt");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("hello"))), b.GetProperty("sha256").GetString());
    }
}
