using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>A pause, resume, promote or rollback request. <see cref="Confirm"/> must be true: the person has seen the confirmation dialog (D41).</summary>
public sealed record ControlRequest(bool Confirm = false, string? Reason = null);

/// <summary>What was asked of the environment's webhook and what came of it. Status: Pending (the call is in flight), Succeeded (the receiver answered 2xx) or Failed.</summary>
public sealed record ControlActionView(string Id, string DeploymentId, string Action, string Source, string? RequestedByUserId, string? RequestedByName, string RequestedAt, string? Reason,
    string? BreachId, string Status, int? HttpStatus, string? Error, string? CompletedAt, int Version);

/// <summary>
/// Deployment control (REOS-112, D42): Pause, Resume, Promote and Rollback call the environment's outbound webhook, and automatic rollback calls it once per confirmed threshold
/// breach. ReleaseOS holds no deployment-tool credentials: the call is an HTTPS POST to a destination from the workspace's allowlist (the SSRF protections of every outbound
/// webhook apply), signed with an HMAC-SHA256 over <c>timestamp.deliveryId.body</c> (<see cref="ControlSignature"/>) with a per-destination key kept in Data Protection, 10 seconds
/// at most, one attempt. Every call is a <c>DeploymentControlActions</c> row written before the call (Pending) and completed after it, each with its own audit row; a failure is a
/// 502 to the caller, a <c>Deployment/ControlFailed</c> alert (SyncAlerts and the live push) and stays visible on the deployment: never silent. The deployment's own state is
/// not changed here: the tool reports it back through the ingest API, which is the single source of rollout state.
/// </summary>
public sealed class DeploymentControlService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config, IControlWebhookSender sender, IControlSigningSecrets secrets,
    SyncAlertWriter? alerts = null, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "DeploymentControl";
    private readonly IRealtimePublisher? _live = realtime;
    public const string Pause = "Pause", Resume = "Resume", Promote = "Promote", Rollback = "Rollback", Manual = "Manual", Automatic = "Automatic";
    public static readonly string[] Actions = [Pause, Resume, Promote, Rollback];
    private static readonly List<string> Planners = [Roles.RTE, Roles.ReleaseManager];
    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A Pending action older than this is taken to have lost its outcome (the host stopped mid-call) and is closed as Failed by the sweeper.</summary>
    public TimeSpan PendingStale => TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Control:PendingStaleSeconds", 120), 15, 3600));

    private static GuardFailure Bad(string m) => new(DeploymentGuards.ControlInvalid, m);
    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    public async Task<List<ControlActionView>> ActionsAsync(string deploymentId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<DeploymentControlActions>().AsNoTracking().Where(a => a.DeploymentId == deploymentId).OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    private static async Task<List<ControlActionView>> ViewsAsync(ReleaseDbContext db, List<DeploymentControlActions> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.RequestedByUserId).OfType<string>().Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. rows.Select(a => new ControlActionView(a.Id, a.DeploymentId, a.Action, a.Source, a.RequestedByUserId, a.RequestedByUserId is null ? null : names.GetValueOrDefault(a.RequestedByUserId),
            Iso(a.RequestedAt)!, a.Reason, a.BreachId, a.Status, a.HttpStatus, a.ErrorMessage, Iso(a.CompletedAt), a.Version))];
    }

    public async Task<RollbackStatus?> RollbackStatusAsync(string deploymentId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var dep = await db.Set<Deployments>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (dep is null) return null;
        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var hook = env.ControlWebhookDestinationId is not null;
        var signed = hook && await secrets.ExistsAsync(env.ControlWebhookDestinationId!, ct);
        string? why = !hook ? "The environment has no control webhook" : !signed ? "The control webhook has no signing key yet" : DeploymentStates.IsTerminal(dep.State) ? $"The deployment is {dep.State}" : null;
        return new(dep.AutoRollbackArmedAt is not null, Iso(dep.AutoRollbackArmedAt), hook, signed, why is null, why);
    }

    // ---- arming ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Arms or disarms automatic rollback for one deployment (RTE, Release Manager). Arming needs the environment's webhook and its signing key, and a deployment that has not finished.</summary>
    public Task<ServiceResult<RollbackStatus>> SetArmedAsync(string deploymentId, bool armed, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var dep = await db.Set<Deployments>().SingleOrDefaultAsync(d => d.Id == deploymentId, ct);
            if (dep is null) return ServiceResult<RollbackStatus>.NotFound("deployment");
            if (VersionMismatch(expectedVersion, dep.Version)) return ServiceResult<RollbackStatus>.Conflict(dep);
            if (!Planners.Contains(await RoleOf(db, actor.UserId) ?? "")) return ServiceResult<RollbackStatus>.Fail(new GuardFailure(DeploymentGuards.ControlRole, "Automatic rollback is armed by an RTE or Release Manager"));
            var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
            if ((dep.AutoRollbackArmedAt is not null) == armed) return ServiceResult<RollbackStatus>.Ok(await StatusAsync(dep, env, ct));   // nothing to change: no Version bump, no audit row
            if (armed)
            {
                if (env.ControlWebhookDestinationId is null) return ServiceResult<RollbackStatus>.Fail(new GuardFailure(DeploymentGuards.NoControlWebhook, $"The environment {env.Name} has no control webhook, so there is nothing for an automatic rollback to call"));
                if (!await secrets.ExistsAsync(env.ControlWebhookDestinationId, ct)) return ServiceResult<RollbackStatus>.Fail(new GuardFailure(DeploymentGuards.NoSigningSecret, $"The control webhook of {env.Name} has no signing key yet; create one on the environment first"));
                if (DeploymentStates.IsTerminal(dep.State)) return ServiceResult<RollbackStatus>.Fail(Bad($"The deployment is {dep.State}; there is nothing left to roll back automatically"));
            }
            var now = Now;
            dep.AutoRollbackArmedAt = armed ? now : null; dep.Version++; dep.UpdatedAt = now;
            Audit(db, actor, dep.ReleaseTrainId, Entity, dep.Id, armed ? "Arm" : "Disarm", null, new { deployment = dep.ExternalId, environment = env.Name });
            await db.SaveChangesAsync(ct);
            return ServiceResult<RollbackStatus>.Ok(await StatusAsync(dep, env, ct));
        }, ct);

    private async Task<RollbackStatus> StatusAsync(Deployments dep, Environments env, CancellationToken ct)
    {
        var hook = env.ControlWebhookDestinationId is not null;
        var signed = hook && await secrets.ExistsAsync(env.ControlWebhookDestinationId!, ct);
        string? why = !hook ? "The environment has no control webhook" : !signed ? "The control webhook has no signing key yet" : DeploymentStates.IsTerminal(dep.State) ? $"The deployment is {dep.State}" : null;
        return new(dep.AutoRollbackArmedAt is not null, Iso(dep.AutoRollbackArmedAt), hook, signed, why is null, why);
    }

    // ---- manual control -------------------------------------------------------------------------------------------------------------

    /// <summary>One prepared call: the pending row is saved, the body is built and signed, and nothing has been sent.</summary>
    private sealed record Prepared(string ActionId, string DeploymentId, string Action, string Source, string? TrainId, string ExternalId, string ServiceKey, string EnvironmentName, CommWebhookTarget? Target,
        ControlDelivery? Delivery, string? PreError);

    public async Task<ServiceResult<ControlActionView>> ControlAsync(string deploymentId, string action, ControlRequest request, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        var prepared = await RunAsync(db => PrepareManualAsync(db, deploymentId, action, request, actor, expectedVersion, ct), ct);
        if (!prepared.IsOk) return prepared.Failed<ControlActionView>();
        return await SendAndRecordAsync(prepared.Value!, actor, ct);
    }

    private async Task<ServiceResult<Prepared>> PrepareManualAsync(ReleaseDbContext db, string deploymentId, string action, ControlRequest request, Actor actor, int? expectedVersion, CancellationToken ct)
    {
        var dep = await db.Set<Deployments>().SingleOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (dep is null) return ServiceResult<Prepared>.NotFound("deployment");
        if (VersionMismatch(expectedVersion, dep.Version)) return ServiceResult<Prepared>.Conflict(dep);
        if (!Actions.Contains(action)) return ServiceResult<Prepared>.Fail(Bad($"The action is one of {string.Join(", ", Actions)}"));
        var f = new List<GuardFailure>();
        if (!Planners.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(DeploymentGuards.ControlRole, "Pause, resume, promote and roll back are for RTEs and Release Managers"));
        if (!request.Confirm) f.Add(new(DeploymentGuards.ConfirmationRequired, $"Confirm to {action.ToLowerInvariant()} this deployment: the call goes to the environment's deployment tool"));
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 }) f.Add(Bad("A reason is at most 500 characters"));
        if (action == Rollback && reason is null) f.Add(Bad("A rollback needs a reason"));
        if (f.Count > 0) return ServiceResult<Prepared>.Fail(f);

        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var illegal = await IllegalAsync(db, dep, action, ct);
        if (illegal is not null) return ServiceResult<Prepared>.Fail(new GuardFailure(DeploymentGuards.IllegalControlAction, illegal));
        var pendingSince = Now - PendingStale;
        if (await db.Set<DeploymentControlActions>().AnyAsync(a => a.DeploymentId == dep.Id && a.Status == "Pending" && a.RequestedAt > pendingSince, ct))
            return ServiceResult<Prepared>.Fail(new GuardFailure(DeploymentGuards.ControlInFlight, "Another control call for this deployment is still in flight; wait for its result"));
        if (env.ControlWebhookDestinationId is null)
            return ServiceResult<Prepared>.Fail(new GuardFailure(DeploymentGuards.NoControlWebhook, $"The environment {env.Name} has no control webhook; choose one on the environment first"));
        string? secret;
        try { secret = await secrets.GetAsync(env.ControlWebhookDestinationId, ct); }
        catch (InvalidOperationException ex) { return ServiceResult<Prepared>.Fail(new GuardFailure(DeploymentGuards.NoSigningSecret, ex.Message)); }
        if (secret is null) return ServiceResult<Prepared>.Fail(new GuardFailure(DeploymentGuards.NoSigningSecret, $"The control webhook of {env.Name} has no signing key yet; create one on the environment first"));

        return ServiceResult<Prepared>.Ok(await InsertPendingAsync(db, dep, env, action, Manual, actor, reason, null, secret, ct));
    }

    /// <summary>Null when the action fits the deployment's state, else the reason it does not.</summary>
    private async Task<string?> IllegalAsync(ReleaseDbContext db, Deployments dep, string action, CancellationToken ct)
    {
        switch (action)
        {
            case Pause when dep.State != DeploymentStates.Running: return $"Only a running deployment can be paused; this one is {dep.State}";
            case Resume when dep.State != DeploymentStates.Paused: return $"Only a paused deployment can be resumed; this one is {dep.State}";
            case Rollback when dep.State is DeploymentStates.Queued or DeploymentStates.RolledBack: return $"A {dep.State} deployment cannot be rolled back";
            case Promote:
                if (dep.Strategy != "Canary") return "Promote moves a canary to its next step; this deployment is not a canary";
                if (dep.State != DeploymentStates.Running) return $"Only a running canary can be promoted; this one is {dep.State}" + (dep.State == DeploymentStates.Paused ? " (resume it first)" : "");
                var steps = dep.CanarySteps is null ? [] : JsonSerializer.Deserialize<double[]>(dep.CanarySteps) ?? [];
                if (CanaryMath.NextTraffic(steps, dep.TrafficPercent) is null) return "The canary is already on its last step";
                var failing = await db.Set<ThresholdBreaches>().AsNoTracking().Where(b => b.DeploymentId == dep.Id && b.Series == MetricSeries.Canary && b.ResolvedAt == null && b.ConfirmedAt != null).FirstOrDefaultAsync(ct);
                if (failing is not null) return $"The canary is failing its threshold ({failing.Metric} {failing.LastValue.ToString("0.##", CultureInfo.InvariantCulture)} against {failing.ThresholdValue.ToString("0.##", CultureInfo.InvariantCulture)} since {Iso(failing.StartedAt)}); roll back, or wait for it to recover";
                break;
        }
        return null;
    }

    /// <summary>Saves the Pending row with its audit row, and builds the signed delivery. <paramref name="secret"/> null means the call cannot be signed: the row is saved anyway, to be closed as Failed.</summary>
    private async Task<Prepared> InsertPendingAsync(ReleaseDbContext db, Deployments dep, Environments env, string action, string source, Actor? actor, string? reason, ThresholdBreaches? breach, string? secret, CancellationToken ct, string? preError = null)
    {
        var now = Now;
        var service = await db.Set<CatalogServices>().AsNoTracking().Where(s => s.Id == dep.CatalogServiceId).Select(s => s.ServiceKey).SingleAsync(ct);
        var row = new DeploymentControlActions
        {
            Id = Ids.New(), DeploymentId = dep.Id, Action = action, Source = source, RequestedByUserId = actor?.UserId, RequestedAt = now, Reason = reason, BreachId = breach?.Id,
        };
        db.Set<DeploymentControlActions>().Add(row);
        Audit(db, actor, dep.ReleaseTrainId, Entity, row.Id, "Request", null, new { action, source, deployment = dep.ExternalId, service, environment = env.Name, reason, breachId = breach?.Id });
        await db.SaveChangesAsync(ct);

        CommWebhookTarget? target = null; ControlDelivery? delivery = null;
        if (preError is null && env.ControlWebhookDestinationId is { } destId && secret is not null)
        {
            var d = await db.Set<WebhookDestinations>().AsNoTracking().SingleOrDefaultAsync(w => w.Id == destId, ct);
            if (d is null) preError = "The environment's control webhook destination no longer exists";
            else
            {
                target = new CommWebhookTarget(d.Id, d.Name, d.Host, d.ProtectedUrl, d.Kind);
                var who = actor is null ? null : await db.Set<Users>().AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => new { u.Id, u.DisplayName }).SingleOrDefaultAsync(ct);
                var train = dep.ReleaseTrainId is null ? null : await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.Id == dep.ReleaseTrainId).Select(t => new { t.ReleaseKey, t.Title }).SingleOrDefaultAsync(ct);
                var ws = await db.Set<Workspaces>().AsNoTracking().Where(w => w.Id == dep.WorkspaceId).Select(w => w.Slug).SingleAsync(ct);
                var body = JsonSerializer.Serialize(new
                {
                    id = row.Id, action = action.ToLowerInvariant(), source = source.ToLowerInvariant(), requestedAt = Iso(now), reason,
                    workspace = ws,
                    deployment = new { externalId = dep.ExternalId, service, environment = env.Name, version = dep.ReleaseVersion, strategy = dep.Strategy, state = dep.State, trafficPercent = dep.TrafficPercent },
                    release = train is null ? null : new { key = train.ReleaseKey, title = train.Title },
                    requestedBy = who is null ? null : new { id = who.Id, name = who.DisplayName },
                    breach = breach is null ? null : new { id = breach.Id, metric = breach.Metric, series = breach.Series, threshold = breach.ThresholdValue, value = breach.LastValue, startedAt = Iso(breach.StartedAt), confirmedAt = Iso(breach.ConfirmedAt) },
                }, Camel);
                var ts = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
                delivery = new ControlDelivery(row.Id, ts, body, ControlSignature.Sign(secret, ts, row.Id, body));
            }
        }
        else if (preError is null) preError = env.ControlWebhookDestinationId is null ? "The environment has no control webhook" : "The control webhook has no signing key";
        return new(row.Id, dep.Id, action, source, dep.ReleaseTrainId, dep.ExternalId, service, env.Name, target, delivery, preError);
    }

    /// <summary>The call itself, outside any transaction, then the result: the row completed, the deployment's Version bumped, an audit row, and on a failure the alert.</summary>
    private async Task<ServiceResult<ControlActionView>> SendAndRecordAsync(Prepared p, Actor? actor, CancellationToken ct)
    {
        ControlSendResult result;
        if (p.PreError is not null || p.Target is null || p.Delivery is null) result = new(false, null, p.Target?.Host ?? "", p.PreError ?? "The call could not be prepared");
        else
        {
            try { result = await sender.SendAsync(p.Target, p.Delivery, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = new(false, null, p.Target.Host, "failed: " + ex.GetType().Name); }   // the sender should not throw; if it does, it is a failed call, not a lost one
        }
        var done = await RunAsync(async db =>
        {
            var row = await db.Set<DeploymentControlActions>().SingleAsync(a => a.Id == p.ActionId, ct);
            var dep = await db.Set<Deployments>().SingleAsync(d => d.Id == p.DeploymentId, ct);
            var now = Now;
            row.Status = result.Delivered ? "Succeeded" : "Failed";
            row.HttpStatus = result.HttpStatus;
            row.ErrorMessage = result.Delivered ? null : Truncate(string.IsNullOrWhiteSpace(result.Reason) ? "The call failed" : result.Reason!, 500);
            row.CompletedAt = now; row.Version++;
            dep.UpdatedAt = now; dep.Version++;
            if (result.Delivered && p.Source == Automatic) dep.AutoRollbackArmedAt = null;   // the deployment is on its way back: one automatic rollback, then disarmed
            Audit(db, actor, dep.ReleaseTrainId, Entity, row.Id, row.Status, null, new { action = p.Action, p.Source, deployment = p.ExternalId, environment = p.EnvironmentName, host = result.Host, httpStatus = result.HttpStatus, error = row.ErrorMessage });
            await db.SaveChangesAsync(ct);
            return ServiceResult<DeploymentControlActions>.Ok(row);
        }, ct);
        if (!done.IsOk) throw new InvalidOperationException($"Control call {p.ActionId} was made but its result could not be recorded: " + (done.Failures.Count > 0 ? done.Failures[0].Message : done.Missing));

        var key = $"{p.DeploymentId}:{p.Action}";
        if (alerts is not null)
        {
            if (!result.Delivered)
                await alerts.RaiseAsync("Deployment", "ControlFailed", key, $"{p.Action} of {p.ServiceKey} in {p.EnvironmentName} ({p.Source.ToLowerInvariant()}) failed: {done.Value!.ErrorMessage}", p.TrainId, ct);
            else await alerts.ResolveAsync("Deployment", "ControlFailed", key, ct);   // a later call that works clears the earlier failure
        }
        if (_live is not null)
        {
            await using var db = await OpenAsync(ct);
            var ver = await db.Set<Deployments>().Where(d => d.Id == p.DeploymentId).Select(d => d.Version).SingleAsync(ct);
            await _live.DeploymentChangedAsync(WorkspaceScope.Current ?? WorkspaceIds.Default, p.DeploymentId, ver, p.TrainId, ct);
        }
        await using var rd = await OpenAsync(ct);
        var view = (await ViewsAsync(rd, [done.Value!], ct))[0];
        return ServiceResult<ControlActionView>.Ok(view);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    // ---- automatic rollback ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A breach was just confirmed. When its deployment is armed and still running (or paused), call the environment's rollback webhook, once for this breach: the action row carries the
    /// breach's id under a unique index, so a repeat of the breach (a replayed sample, two hosts) cannot fire it again. A canary rollout reacts to its Canary series only; the baseline
    /// degrading is not the canary's doing. A call that cannot be made (no webhook, no key) is recorded as a failed action and alerted, never skipped.
    /// </summary>
    public async Task OnBreachConfirmedAsync(string breachId, CancellationToken ct = default)
    {
        var prepared = await RunAsync<Prepared?>(async db =>
        {
            var breach = await db.Set<ThresholdBreaches>().SingleOrDefaultAsync(b => b.Id == breachId, ct);
            if (breach is null || breach.ConfirmedAt is null || breach.ResolvedAt is not null) return ServiceResult<Prepared?>.Ok(null);
            var dep = await db.Set<Deployments>().SingleAsync(d => d.Id == breach.DeploymentId, ct);
            if (dep.AutoRollbackArmedAt is null || dep.State is not (DeploymentStates.Running or DeploymentStates.Paused)) return ServiceResult<Prepared?>.Ok(null);
            if (dep.Strategy == "Canary" && breach.Series != MetricSeries.Canary) return ServiceResult<Prepared?>.Ok(null);
            if (await db.Set<DeploymentControlActions>().AnyAsync(a => a.BreachId == breach.Id, ct)) return ServiceResult<Prepared?>.Ok(null);   // already fired for this breach
            var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
            string? secret = null, pre = null;
            if (env.ControlWebhookDestinationId is null) pre = $"The environment {env.Name} has no control webhook, so the automatic rollback could not be called";
            else
            {
                try { secret = await secrets.GetAsync(env.ControlWebhookDestinationId, ct); }
                catch (InvalidOperationException ex) { pre = ex.Message; }
                if (secret is null && pre is null) pre = $"The control webhook of {env.Name} has no signing key, so the automatic rollback could not be called";
            }
            var reason = $"{breach.Metric} {breach.LastValue.ToString("0.##", CultureInfo.InvariantCulture)} against {breach.ThresholdValue.ToString("0.##", CultureInfo.InvariantCulture)} for {breach.ForSeconds} s ({breach.Series} series)";
            return ServiceResult<Prepared?>.Ok(await InsertPendingAsync(db, dep, env, Rollback, Automatic, null, reason, breach, secret, ct, pre));
        }, ct);
        if (prepared.Kind == ResultKind.GuardFailed && prepared.Failures.Any(f => f.Guard == Guards.DbRule && f.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal))) return;   // lost the race: someone else fired it
        if (!prepared.IsOk) throw new InvalidOperationException("Automatic rollback could not be prepared: " + (prepared.Failures.Count > 0 ? prepared.Failures[0].Message : prepared.Missing));
        if (prepared.Value is null) return;
        await SendAndRecordAsync(prepared.Value, null, ct);
    }

    // ---- housekeeping ---------------------------------------------------------------------------------------------------------------

    /// <summary>Closes control calls that were left Pending (the host stopped between the call and its record) as Failed, with an alert: nobody knows whether the tool got it. Returns how many.</summary>
    public async Task<int> FailStalePendingAsync(CancellationToken ct = default)
    {
        var stale = new List<(string Id, string DeploymentId, string Action, string? TrainId)>();
        var r = await RunAsync(async db =>
        {
            var cutoff = Now - PendingStale;
            var rows = await db.Set<DeploymentControlActions>().Where(a => a.Status == "Pending" && a.RequestedAt < cutoff).ToListAsync(ct);
            var now = Now;
            foreach (var a in rows)
            {
                a.Status = "Failed"; a.ErrorMessage = "No result was recorded (the host stopped during the call); check the deployment tool"; a.CompletedAt = now; a.Version++;
                var dep = await db.Set<Deployments>().SingleAsync(d => d.Id == a.DeploymentId, ct);
                dep.UpdatedAt = now; dep.Version++;
                Audit(db, null, dep.ReleaseTrainId, Entity, a.Id, "Failed", null, new { action = a.Action, a.Source, error = a.ErrorMessage });
                stale.Add((a.Id, a.DeploymentId, a.Action, dep.ReleaseTrainId));
            }
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
            return ServiceResult<int>.Ok(rows.Count);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not close stale control calls: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
        if (alerts is not null)
            foreach (var s in stale)
                await alerts.RaiseAsync("Deployment", "ControlFailed", $"{s.DeploymentId}:{s.Action}", $"{s.Action} call {s.Id} never reported a result; check the deployment tool", s.TrainId, ct);
        return r.Value;
    }
}
