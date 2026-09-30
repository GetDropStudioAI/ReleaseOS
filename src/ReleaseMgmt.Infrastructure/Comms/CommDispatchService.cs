using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Comms;

public sealed record DispatchRequest(string? TemplateId, string? ScheduleItemId, string Channel, string? Format, string? WebhookDestinationId, string? WebhookUrl, bool IsRehearsal = false);

/// <summary>One dispatch with its exact stored body. <see cref="BodySha256"/> is the SHA-256 (hex) of the body's UTF-8 bytes, computed from what is stored.</summary>
public sealed record DispatchView(
    string Id, string TrainId, string TemplateId, string TemplateType, string Channel, string? Format, string? WebhookDestinationId, string? WebhookName,
    string Subject, string Body, string BodySha256, string DispatchedByUserId, string? DispatchedByName, DateTime DispatchedAt, bool IsRehearsal, string Outcome,
    string? FailureReason, string? ScheduleItemId, DateTime? DueAt, DateTime? SentAt, bool? Late, DateTime? AsOf, int? TrainVersion);

public sealed record DispatchLogRow(
    string Id, string TemplateId, string TemplateType, string Channel, string? WebhookName, string Subject, string DispatchedByUserId, string? DispatchedByName,
    DateTime DispatchedAt, bool IsRehearsal, string Outcome, int BodyLength);

public sealed record DispatchLogPage(IReadOnlyList<DispatchLogRow> Items, string? NextCursor, int Limit);

public sealed record CommTemplateRow(string Id, string TemplateType, string Audience, string SubjectLine, string MarkdownBody, int Version);
/// <summary>State: sent | sentLate | overdue | ready | scheduled (ready = unsent and due within 24 h).</summary>
public sealed record CommScheduleRow(string Id, string CommTemplateId, string TemplateType, string Audience, string SubjectLine, DateTime DueAt, DateTime? SentAt, string? DispatchId, string State, int Version);
/// <summary>An allowlisted destination without its URL (the URL is a secret); <see cref="Host"/> is all the drawer shows.</summary>
public sealed record CommWebhookTargetRow(string Id, string Name, string Kind, string Host);
public sealed record DispatchContext(DateTime AsOf, int TrainVersion, string TrainTitle, string TargetReleaseDate, IReadOnlyList<CommTemplateRow> Templates, IReadOnlyList<CommScheduleRow> Schedule, IReadOnlyList<CommWebhookTargetRow> WebhookTargets);

/// <summary>
/// Communication dispatch (REOS-45, PROJECT_SCOPE 5.2, Q-045a..). Every copy / mailto / webhook renders through <see cref="ICommDispatchRenderer"/> (an
/// unknown token blocks dispatch), writes ONE immutable CommDispatches row holding the exact text, and one audit row in the same transaction. A
/// dispatch that fulfils a schedule item stamps its SentAt from the TimeProvider. Webhooks go only to allowlisted (WebhookDestinations) https URLs.
/// The HTTP call happens before the row is written (the row is immutable, so its Outcome must be known); a failed delivery is still recorded
/// (Outcome 'Failed'), raises Webhook/DeliveryFailed and is returned to the caller: never swallowed.
/// </summary>
public sealed class CommDispatchService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ICommDispatchRenderer renderer, ICommWebhookSender webhooks,
    SyncAlertWriter? syncAlerts = null, IRealtimePublisher? realtime = null, ILogger<CommDispatchService>? log = null) : ServiceBase(dbf, time, realtime)
{
    public const string Copy = "Copy", Mailto = "Mailto", Webhook = "Webhook";
    public const string RichText = "RichText", Markdown = "Markdown";
    public const int MaxBodyChars = 200_000;
    public const int DefaultLimit = 50, MaxLimit = 200;
    private static readonly string[] DispatchRoles = [Roles.RTE, Roles.ReleaseManager];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // SEC-A4: "a schedule item is fulfilled once" is checked before the webhook call and the item is stamped after it, so two dispatches of one item (double
    // click, two tabs, a retry) could both pass the check and both post to the channel. Dispatches that name a schedule item are serialised per item (striped
    // locks, this singleton, one process: the app is one deployable on one SQLite file), so the second sees the item sent and is refused before sending.
    private readonly SemaphoreSlim[] _scheduleLocks = [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1))];

    // ---- dispatch ---------------------------------------------------------------------------------------------------
    /// <param name="expectedTrainVersion">The train Version the caller previewed (If-Match). A moved train is a Conflict: the caller re-previews.</param>
    public async Task<ServiceResult<DispatchView>> DispatchAsync(string trainId, DispatchRequest req, Actor actor, int? expectedTrainVersion, CancellationToken ct = default)
    {
        if (req.ScheduleItemId is null) return await DispatchCoreAsync(trainId, req, actor, expectedTrainVersion, ct);
        var gate = _scheduleLocks[(uint)StringComparer.Ordinal.GetHashCode(req.ScheduleItemId) % (uint)_scheduleLocks.Length];
        await gate.WaitAsync(ct);
        try { return await DispatchCoreAsync(trainId, req, actor, expectedTrainVersion, ct); }
        finally { gate.Release(); }
    }

    private async Task<ServiceResult<DispatchView>> DispatchCoreAsync(string trainId, DispatchRequest req, Actor actor, int? expectedTrainVersion, CancellationToken ct)
    {
        // ---- 1. read and validate, no writes yet
        ReleaseTrains train;
        CommTemplates template;
        CommSchedule? item = null;
        WebhookDestinations? dest = null;
        string? actorName;
        await using (var db = await OpenAsync(ct))
        {
            var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<DispatchView>.NotFound("train");
            train = t;
            var role = await RoleOf(db, actor.UserId);
            if (role is null || !DispatchRoles.Contains(role))
                return Fail(CommGuards.DispatchRole, "Only an RTE or Release Manager dispatches communications");
            if (VersionMismatch(expectedTrainVersion, train.Version)) return ServiceResult<DispatchView>.Conflict(new { id = train.Id, version = train.Version });
            actorName = await db.Set<Users>().Where(u => u.Id == actor.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);

            if (req.Channel is not (Copy or Mailto or Webhook)) return Fail(CommGuards.InvalidChannel, "Channel must be Copy, Mailto or Webhook");
            if (req.Channel == Copy && req.Format is not (null or RichText or Markdown)) return Fail(CommGuards.InvalidFormat, "Copy format must be RichText or Markdown");

            var templateId = req.TemplateId;
            if (req.ScheduleItemId is not null)
            {
                item = await db.Set<CommSchedule>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == req.ScheduleItemId && s.ReleaseTrainId == trainId, ct);
                if (item is null) return ServiceResult<DispatchView>.NotFound("schedule item");
                if (item.SentAt is not null) return Fail(CommGuards.ScheduleAlreadySent, "This schedule item was already sent; dispatch the template without the schedule item to send it again");
                if (templateId is not null && templateId != item.CommTemplateId) return Fail(CommGuards.TemplateMismatch, "The template is not the one this schedule item is for");
                templateId = item.CommTemplateId;
            }
            if (templateId is null) return Fail(CommGuards.TemplateRequired, "Choose a template (or a schedule item) to dispatch");
            var tpl = await db.Set<CommTemplates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == templateId && x.ReleaseTrainId == trainId, ct);
            if (tpl is null) return ServiceResult<DispatchView>.NotFound("template");
            template = tpl;

            if (req.Channel == Webhook)
            {
                var (d, failure) = await ResolveDestinationAsync(db, req, ct);
                if (failure is not null) return ServiceResult<DispatchView>.Fail(failure);
                dest = d;
            }
        }

        // ---- 2. render through the seam; an unknown token blocks everything
        var format = req.Channel == Copy ? (req.Format ?? RichText) : null;
        var bodyTarget = req.Channel switch { Copy => format == Markdown ? CommTargets.Markdown : CommTargets.Html, Mailto => CommTargets.PlainText, _ => CommTargets.JsonString };
        var subjectTarget = req.Channel == Webhook ? CommTargets.JsonString : CommTargets.PlainText;
        var body = await renderer.RenderAsync(trainId, template.Id, null, bodyTarget, ct);
        var subj = await renderer.RenderAsync(trainId, null, template.SubjectLine, subjectTarget, ct);
        var errors = body.TokenErrors.Concat(subj.TokenErrors).Distinct(StringComparer.Ordinal).ToList();
        if (errors.Count > 0)
            return ServiceResult<DispatchView>.Fail(new GuardFailure(CommGuards.TokenErrors, "The message has unknown tokens and cannot be dispatched: " + string.Join("; ", errors), errors));
        if (expectedTrainVersion is int ev && body.TrainVersion != ev) return ServiceResult<DispatchView>.Conflict(new { id = train.Id, version = body.TrainVersion });

        // ---- 3. exact bytes: what is stored is what was rendered/sent
        string subject = subj.Text, stored = body.Text;
        if (req.Channel == Webhook)
        {
            if (!TryUnescapeJson(subj.Text, out subject)) return Fail(CommGuards.RenderInvalid, "The rendered subject is not valid for a JSON webhook (a value was not escaped)");
            stored = BuildWebhookPayload(dest!.Kind, subj.Text, body.Text, trainId, template.Id, Now);
            try { using var _ = JsonDocument.Parse(stored); }
            catch (JsonException) { return Fail(CommGuards.RenderInvalid, "The rendered message is not valid for a JSON webhook (a value was not escaped)"); }
        }
        var problem = ContentProblem(stored) ?? ContentProblem(subject);
        if (problem is not null) return Fail(CommGuards.RenderInvalid, problem);
        if (string.IsNullOrWhiteSpace(stored)) return Fail(CommGuards.EmptyMessage, "The message is empty");
        if (stored.Length > MaxBodyChars) return Fail(CommGuards.MessageTooLarge, $"The message is over {MaxBodyChars:N0} characters");

        // ---- 4. deliver (webhook), then record. The row is immutable, so its Outcome has to be known before it is written.
        var outcome = "Handed";
        string? failureReason = null, host = null;
        if (dest is not null)
        {
            CommWebhookResult sent;
            try { sent = await webhooks.SendAsync(new CommWebhookTarget(dest.Id, dest.Name, dest.Url, dest.Kind), stored, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A sender that throws is a bug; the exception message could carry the URL, so only its type is kept.
                log?.LogError("Comm webhook sender threw {Type} for destination {Destination}", ex.GetType().Name, dest.Name);
                sent = new CommWebhookResult(false, HostOf(dest.Url), "the sender failed: " + ex.GetType().Name);
            }
            host = sent.Host;
            outcome = sent.Delivered ? "Delivered" : "Failed";
            failureReason = sent.Delivered ? null : sent.Reason ?? "delivery failed";
        }

        var result = await RunAsync(async db =>
        {
            var current = await db.Set<ReleaseTrains>().Where(x => x.Id == trainId).Select(x => (int?)x.Version).SingleOrDefaultAsync(ct);
            if (current is null) return ServiceResult<DispatchView>.NotFound("train");
            // Refused before anything was sent for Copy/Mailto; for a webhook that was already delivered the row must still be written (below).
            if (current != body.TrainVersion && dest is null) return ServiceResult<DispatchView>.Conflict(new { id = trainId, version = current.Value });

            var now = Now;
            var row = new CommDispatches
            {
                Id = Ids.New(), CommTemplateId = template.Id, Channel = req.Channel, WebhookDestinationId = dest?.Id, HydratedSubject = subject, HydratedBody = stored,
                DispatchedByUserId = actor.UserId, DispatchedAt = now, IsRehearsal = req.IsRehearsal, Outcome = outcome,
            };
            db.Set<CommDispatches>().Add(row);

            DateTime? sentAt = null; bool? late = null; DateTime? dueAt = null;
            if (item is not null)
            {
                dueAt = item.DueAt;
                var s = await db.Set<CommSchedule>().SingleAsync(x => x.Id == item.Id, ct);
                // A failed delivery or a rehearsal does not count as sent: the timeliness KPI (M12) must not be flattered.
                if (outcome != "Failed" && !req.IsRehearsal && s.SentAt is null)
                {
                    s.SentAt = now; s.DispatchId = row.Id; s.Version++;
                    sentAt = now; late = now > s.DueAt;
                }
            }
            Audit(db, actor, trainId, "CommDispatch", row.Id, "Dispatch", null, new
            {
                channel = req.Channel, format, outcome, templateId = template.Id, webhookDestinationId = dest?.Id, webhook = dest?.Name, host, failure = failureReason,
                rehearsal = req.IsRehearsal, scheduleItemId = item?.Id, dueAt, sentAt, late, trainVersion = body.TrainVersion, asOf = body.AsOf,
                bodySha256 = Sha256Hex(stored), bodyLength = stored.Length,
            });
            await db.SaveChangesAsync(ct);
            return ServiceResult<DispatchView>.Ok(new DispatchView(row.Id, trainId, template.Id, template.TemplateType, row.Channel, format, dest?.Id, dest?.Name, subject, stored, Sha256Hex(stored),
                actor.UserId, actorName, now, row.IsRehearsal, outcome, failureReason, item?.Id, dueAt, sentAt, late, body.AsOf, body.TrainVersion));
        }, ct);

        if (result.IsOk && outcome == "Failed" && dest is not null) await RaiseFailureAsync(dest, host ?? HostOf(dest.Url), failureReason!, trainId, ct);
        return result;
    }

    private async Task RaiseFailureAsync(WebhookDestinations dest, string host, string reason, string trainId, CancellationToken ct)
    {
        var message = $"Webhook '{dest.Name}' ({host}) failed: {reason}";   // name, host and reason only: the URL is a secret
        log?.LogError("Comm webhook delivery failed: {Message}", message);
        if (syncAlerts is null) { log?.LogError("No SyncAlertWriter is registered; the failed dispatch is visible only as an Outcome 'Failed' row"); return; }
        try { await syncAlerts.RaiseAsync("Webhook", "DeliveryFailed", dest.Id, message, trainId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dispatch row (Outcome 'Failed') is saved and visible in the log; only the alert row failed. Logged loudly, never dropped silently.
            log?.LogError(ex, "The failed dispatch to '{Destination}' was recorded but its alert could not be raised", dest.Name);
        }
    }

    private static async Task<(WebhookDestinations? Dest, GuardFailure? Failure)> ResolveDestinationAsync(ReleaseDbContext db, DispatchRequest req, CancellationToken ct)
    {
        if ((req.WebhookDestinationId is null) == (req.WebhookUrl is null))
            return (null, new GuardFailure(CommGuards.WebhookNotAllowed, "Choose one allowlisted webhook destination"));
        if (req.WebhookUrl is not null)
        {
            // https is judged first, so a plain http target says so instead of "not on the allowlist"
            if (!Uri.TryCreate(req.WebhookUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                return (null, new GuardFailure(CommGuards.WebhookNotHttps, "A webhook target must be an https URL"));
        }
        var url = req.WebhookUrl?.Trim();
        var d = req.WebhookDestinationId is not null
            ? await db.Set<WebhookDestinations>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == req.WebhookDestinationId, ct)
            : await db.Set<WebhookDestinations>().AsNoTracking().SingleOrDefaultAsync(x => x.Url == url, ct);
        if (d is null) return (null, new GuardFailure(CommGuards.WebhookNotAllowed, "That webhook target is not on the allowlist; an administrator adds destinations"));
        if (!Uri.TryCreate(d.Url, UriKind.Absolute, out var du) || du.Scheme != Uri.UriSchemeHttps)
            return (null, new GuardFailure(CommGuards.WebhookNotHttps, $"Destination '{d.Name}' is not an https URL and cannot be used"));
        if (!string.IsNullOrEmpty(du.UserInfo))
            return (null, new GuardFailure(CommGuards.WebhookNotAllowed, $"Destination '{d.Name}' carries credentials in its URL and cannot be used"));
        return (d, null);
    }

    /// <summary>Teams and Slack incoming webhooks take {"text": ...}; Generic gets the parts separately. Inputs are already escaped for a JSON string.</summary>
    internal static string BuildWebhookPayload(string kind, string subjectJson, string bodyJson, string trainId, string templateId, DateTime at) =>
        kind == "Generic"
            ? $"{{\"subject\":\"{subjectJson}\",\"text\":\"{bodyJson}\",\"trainId\":\"{trainId}\",\"templateId\":\"{templateId}\",\"sentAt\":\"{at:yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}"
            : $"{{\"text\":\"{subjectJson}\\n\\n{bodyJson}\"}}";

    private static bool TryUnescapeJson(string escaped, out string plain)
    {
        try { plain = JsonSerializer.Deserialize<string>("\"" + escaped + "\"") ?? ""; return true; }
        catch (JsonException) { plain = ""; return false; }
    }

    /// <summary>Text that cannot round-trip through SQLite TEXT and UTF-8 byte for byte is refused rather than altered.</summary>
    private static string? ContentProblem(string s)
    {
        if (s.Contains('\0')) return "The rendered message contains a NUL character and cannot be stored exactly";
        try { StrictUtf8.GetBytes(s); return null; }
        catch (EncoderFallbackException) { return "The rendered message is not valid Unicode (an unpaired surrogate) and cannot be stored exactly"; }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
    private static ServiceResult<DispatchView> Fail(string guard, string message) => ServiceResult<DispatchView>.Fail(new GuardFailure(guard, message));

    // ---- reads --------------------------------------------------------------------------------------------------------
    /// <summary>The Sent log of one train, newest first, keyset-paged on (DispatchedAt, Id). Null = no such train.</summary>
    public async Task<DispatchLogPage?> ListAsync(string trainId, int? limit, string? cursor, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return null;
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var q = from d in db.Set<CommDispatches>().AsNoTracking()
                join t in db.Set<CommTemplates>().AsNoTracking() on d.CommTemplateId equals t.Id
                where t.ReleaseTrainId == trainId
                select new { d, t };
        if (cursor is not null)
        {
            if (!TryParseCursor(cursor, out var at, out var id)) throw new ArgumentException("cursor is the nextCursor of a previous page", nameof(cursor));
            q = q.Where(x => x.d.DispatchedAt < at || (x.d.DispatchedAt == at && x.d.Id.CompareTo(id) < 0));
        }
        var rows = await q.OrderByDescending(x => x.d.DispatchedAt).ThenByDescending(x => x.d.Id).Take(take + 1)
            .Select(x => new
            {
                x.d.Id, x.d.CommTemplateId, x.t.TemplateType, x.d.Channel, x.d.WebhookDestinationId, x.d.HydratedSubject, x.d.DispatchedByUserId, x.d.DispatchedAt, x.d.IsRehearsal, x.d.Outcome,
                Length = x.d.HydratedBody.Length,
            }).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var userIds = page.Select(r => r.DispatchedByUserId).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var destIds = page.Where(r => r.WebhookDestinationId != null).Select(r => r.WebhookDestinationId!).Distinct().ToList();
        var dests = await db.Set<WebhookDestinations>().AsNoTracking().Where(w => destIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var items = page.Select(r => new DispatchLogRow(r.Id, r.CommTemplateId, r.TemplateType, r.Channel, r.WebhookDestinationId is { } w ? dests.GetValueOrDefault(w) : null, r.HydratedSubject,
            r.DispatchedByUserId, names.GetValueOrDefault(r.DispatchedByUserId), r.DispatchedAt, r.IsRehearsal, r.Outcome, r.Length)).ToList();
        var next = rows.Count > take ? MakeCursor(page[^1].DispatchedAt, page[^1].Id) : null;
        return new DispatchLogPage(items, next, take);
    }

    private static string MakeCursor(DateTime at, string id) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "|" + id;
    public static bool IsValidCursor(string cursor) => TryParseCursor(cursor, out _, out _);
    private static bool TryParseCursor(string cursor, out DateTime at, out string id)
    {
        at = default; id = "";
        var i = cursor.IndexOf('|');
        if (i <= 0 || i == cursor.Length - 1) return false;
        id = cursor[(i + 1)..];
        return DateTime.TryParseExact(cursor[..i], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    }

    /// <summary>One dispatch, exactly as stored.</summary>
    public async Task<DispatchView?> GetAsync(string dispatchId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var r = await (from d in db.Set<CommDispatches>().AsNoTracking()
                       join t in db.Set<CommTemplates>().AsNoTracking() on d.CommTemplateId equals t.Id
                       where d.Id == dispatchId
                       select new { d, t.ReleaseTrainId, t.TemplateType }).SingleOrDefaultAsync(ct);
        if (r is null) return null;
        var name = await db.Set<Users>().AsNoTracking().Where(u => u.Id == r.d.DispatchedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        var dest = r.d.WebhookDestinationId is null ? null : await db.Set<WebhookDestinations>().AsNoTracking().Where(w => w.Id == r.d.WebhookDestinationId).Select(w => w.Name).SingleOrDefaultAsync(ct);
        var item = await db.Set<CommSchedule>().AsNoTracking().SingleOrDefaultAsync(s => s.DispatchId == dispatchId, ct);
        return new DispatchView(r.d.Id, r.ReleaseTrainId, r.d.CommTemplateId, r.TemplateType, r.d.Channel, null, r.d.WebhookDestinationId, dest, r.d.HydratedSubject, r.d.HydratedBody, Sha256Hex(r.d.HydratedBody),
            r.d.DispatchedByUserId, name, r.d.DispatchedAt, r.d.IsRehearsal, r.d.Outcome, null, item?.Id, item?.DueAt, item?.SentAt, item is { SentAt: not null } ? item.SentAt > item.DueAt : null, null, null);
    }

    /// <summary>What the drawer needs in one read: this train's templates, its T-minus schedule with states, and the allowlisted destinations (no URLs). Null = no such train.</summary>
    public async Task<DispatchContext?> GetContextAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainId, ct);
        if (train is null) return null;
        var templates = await db.Set<CommTemplates>().AsNoTracking().Where(t => t.ReleaseTrainId == trainId).OrderBy(t => t.TemplateType).ThenBy(t => t.Id).ToListAsync(ct);
        var sched = await db.Set<CommSchedule>().AsNoTracking().Where(s => s.ReleaseTrainId == trainId).OrderBy(s => s.DueAt).ThenBy(s => s.Id).ToListAsync(ct);
        var byId = templates.ToDictionary(t => t.Id);
        var now = Now;
        var schedule = sched.Where(s => byId.ContainsKey(s.CommTemplateId)).Select(s =>
        {
            var t = byId[s.CommTemplateId];
            var state = s.SentAt is { } sent ? (sent > s.DueAt ? "sentLate" : "sent") : s.DueAt < now ? "overdue" : s.DueAt - now <= TimeSpan.FromHours(24) ? "ready" : "scheduled";
            return new CommScheduleRow(s.Id, s.CommTemplateId, t.TemplateType, t.Audience, t.SubjectLine, s.DueAt, s.SentAt, s.DispatchId, state, s.Version);
        }).ToList();
        var dests = await db.Set<WebhookDestinations>().AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return new DispatchContext(now, train.Version, train.Title, train.TargetReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. templates.Select(t => new CommTemplateRow(t.Id, t.TemplateType, t.Audience, t.SubjectLine, t.MarkdownBody, t.Version))], schedule,
            [.. dests.Where(d => Uri.TryCreate(d.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps).Select(d => new CommWebhookTargetRow(d.Id, d.Name, d.Kind, HostOf(d.Url)))]);
    }
}
