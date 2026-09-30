using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using NBomber.CSharp;
using NBomber.Contracts;

namespace ReleaseMgmt.LoadTests;

public sealed class VUser(int index, string email, string role, string userId, HttpClient http, int seed)
{
    public int Index { get; } = index;
    public string Email { get; } = email;
    public string Role { get; } = role;
    public string UserId { get; } = userId;
    public HttpClient Http { get; } = http;
    public Random Rnd { get; } = new(seed);
    public bool CanPlan => Role is "RTE" or "ReleaseManager";      // owns/starts steps, sees the audit log
    public bool CanAudit => Role != "Viewer";
    public List<string> Tasks { get; } = [];                        // tasks only this user toggles (no accidental 409s)
    public Dictionary<string, (int Version, bool Done)> Hot { get; } = [];   // this user's last knowledge of each contended task
}

/// <summary>
/// One iteration = one user action, picked by weight from a realistic mix. Every HTTP call is a measured NBomber step and is also recorded individually
/// (exact percentiles). 409 on a hot task is an expected outcome; every other non-2xx is an error.
/// </summary>
public sealed class Workload
{
    private readonly SeedResult _seed;
    private readonly Recorder _rec;
    private readonly Dictionary<string, List<string>> _trainGates = [];
    private readonly ConcurrentQueue<(string RunId, string StepId)> _stepPool;
    private readonly (string Name, int Weight)[] _mix =
    [
        ("stream", 14), ("openTrain", 22), ("taskToggle", 12), ("hotTask", 3), ("liveRead", 8), ("liveStep", 6), ("audit", 8), ("analytics", 8), ("calendar", 5), ("myWork", 8), ("inbox", 6),
    ];
    private readonly int _totalWeight;
    private readonly List<string> _userIds;
    public int StepPairsDone;
    public int PoolExhausted;

    public Workload(SeedResult seed, Recorder rec, IReadOnlyList<VUser> users)
    {
        _seed = seed; _rec = rec; _userIds = [.. users.Select(x => x.UserId)];
        foreach (var (g, t) in seed.GateTrain) { if (!_trainGates.TryGetValue(t, out var l)) _trainGates[t] = l = []; l.Add(g); }
        _stepPool = new(seed.WriteSteps);
        _totalWeight = _mix.Sum(m => m.Weight);
        for (var i = 0; i < seed.WriteTasks.Count; i++) users[i % users.Count].Tasks.Add(seed.WriteTasks[i]);
    }

    public async Task IterationAsync(IScenarioContext ctx, VUser u)
    {
        var pick = u.Rnd.Next(_totalWeight); var action = _mix[0].Name;
        foreach (var (n, w) in _mix) { if (pick < w) { action = n; break; } pick -= w; }
        // Warm-up must not burn the one-shot step pool or take the contended-task path.
        var warm = ctx.ScenarioInfo.ScenarioOperation != ScenarioOperation.Bombing;
        if ((action == "liveStep" && (!u.CanPlan || warm)) ) action = "liveRead";
        if (action == "audit" && !u.CanAudit) action = "analytics";
        switch (action)
        {
            case "stream": await Stream(ctx, u); break;
            case "openTrain": await OpenTrain(ctx, u); break;
            case "taskToggle": await TaskToggle(ctx, u); break;
            case "hotTask": await HotTask(ctx, u); break;
            case "liveRead": await LiveRead(ctx, u); break;
            case "liveStep": await LiveStep(ctx, u); break;
            case "audit": await Audit(ctx, u); break;
            case "analytics": await Analytics(ctx, u); break;
            case "calendar": await Calendar(ctx, u); break;
            case "myWork": await MyWork(ctx, u); break;
            default: await Inbox(ctx, u); break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ actions

    private async Task Stream(IScenarioContext ctx, VUser u)
    {
        await Get(ctx, u, "Stream: GET /trains", "/api/v1/trains");
        await Get(ctx, u, "Stream: GET /freeze-windows/ahead", "/api/v1/freeze-windows/ahead");
    }

    private async Task OpenTrain(IScenarioContext ctx, VUser u)
    {
        var t = Pick(u, _seed.StreamTrains);
        await Get(ctx, u, "Train: GET /trains/{id}", $"/api/v1/trains/{t}");
        await Get(ctx, u, "Train: GET /trains/{id}/readiness", $"/api/v1/trains/{t}/readiness");
        await Get(ctx, u, "Train: GET /trains/{id}/products", $"/api/v1/trains/{t}/products");
        await Get(ctx, u, "Train: GET /trains/{id}/window", $"/api/v1/trains/{t}/window");
        if (_trainGates.TryGetValue(t, out var gates)) await Get(ctx, u, "Gate: GET /gates/{id}", $"/api/v1/gates/{Pick(u, gates)}");
    }

    /// <summary>Open the gate (read the task Version), tick the task, untick it: each write carries the Version just read, as the UI does.</summary>
    private async Task TaskToggle(IScenarioContext ctx, VUser u)
    {
        if (u.Tasks.Count == 0) { await Stream(ctx, u); return; }
        var task = Pick(u, u.Tasks);
        var gate = _seed.TaskGate[task];
        var g = await Get(ctx, u, "Gate: GET /gates/{id}", $"/api/v1/gates/{gate}");
        var version = 1;
        if (g.Json is { } j && j.TryGetProperty("tasks", out var ts))
            foreach (var t in ts.EnumerateArray()) if (t.GetProperty("id").GetString() == task) { version = t.GetProperty("version").GetInt32(); break; }
        var c = await Send(ctx, u, "Task: POST /tasks/{id}:complete", HttpMethod.Post, $"/api/v1/tasks/{task}:complete", null, version, write: true);
        if (c.Status != 200 || c.Json is not { } cj) return;
        await Send(ctx, u, "Task: POST /tasks/{id}:reopen", HttpMethod.Post, $"/api/v1/tasks/{task}:reopen", null, cj.GetProperty("version").GetInt32(), write: true);
    }

    /// <summary>Everyone fights over five tasks with what they last saw; a stale Version is a 409 (expected) that carries the current row.</summary>
    private async Task HotTask(IScenarioContext ctx, VUser u)
    {
        var task = Pick(u, _seed.HotTasks);
        if (!u.Hot.TryGetValue(task, out var known))
        {
            var g = await Get(ctx, u, "Gate: GET /gates/{id}", $"/api/v1/gates/{_seed.TaskGate[task]}");
            known = (1, false);
            if (g.Json is { } j && j.TryGetProperty("tasks", out var ts))
                foreach (var t in ts.EnumerateArray()) if (t.GetProperty("id").GetString() == task) { known = (t.GetProperty("version").GetInt32(), t.GetProperty("done").GetBoolean()); break; }
        }
        var verb = known.Done ? "reopen" : "complete";
        var r = await Send(ctx, u, $"HotTask: POST /tasks/{{id}}:{verb} (contended)", HttpMethod.Post, $"/api/v1/tasks/{task}:{verb}", null, known.Version, write: true, conflictOk: true);
        if (r.Json is { } body)
        {
            if (r.Status == 200) u.Hot[task] = (body.GetProperty("version").GetInt32(), body.GetProperty("isCompleted").GetBoolean());
            else if (r.Status == 409 && body.TryGetProperty("current", out var cur)) u.Hot[task] = (cur.GetProperty("version").GetInt32(), cur.GetProperty("isCompleted").GetBoolean());
        }
    }

    private async Task LiveRead(IScenarioContext ctx, VUser u)
    {
        var (train, run) = Pick(u, _seed.ReadRuns);
        await Get(ctx, u, "Live run: GET /runs/{id}", $"/api/v1/runs/{run}");
        await Get(ctx, u, "Live run: GET /runs/{id}/forecast", $"/api/v1/runs/{run}/forecast");
        await Get(ctx, u, "Live run: GET /trains/{id}/steps", $"/api/v1/trains/{train}/steps");
    }

    private async Task LiveStep(IScenarioContext ctx, VUser u)
    {
        if (!_stepPool.TryDequeue(out var s)) { Interlocked.Increment(ref PoolExhausted); await LiveRead(ctx, u); return; }
        var start = await Send(ctx, u, "Live step: POST /runs/{id}/steps/{stepId}:start", HttpMethod.Post, $"/api/v1/runs/{s.RunId}/steps/{s.StepId}:start", new { }, 1, write: true);
        if (start.Status != 200 || start.Json is not { } sj) return;
        var done = await Send(ctx, u, "Live step: POST /runs/{id}/steps/{stepId}:done", HttpMethod.Post, $"/api/v1/runs/{s.RunId}/steps/{s.StepId}:done", new { }, sj.GetProperty("version").GetInt32(), write: true);
        if (done.Status == 200) Interlocked.Increment(ref StepPairsDone);
    }

    private async Task Audit(IScenarioContext ctx, VUser u)
    {
        switch (u.Rnd.Next(9))
        {
            case 0 or 1 or 2:   // the viewer opens on the newest page, then pages back with the keyset cursor
                var page = await Get(ctx, u, "Audit: newest page (limit 100)", "/api/v1/audit?limit=100");
                for (var n = 0; n < 2 && page.Json is { } pj && pj.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String; n++)
                    page = await Get(ctx, u, "Audit: next page (cursor)", $"/api/v1/audit?limit=100&cursor={c.GetString()}");
                break;
            case 3: await Get(ctx, u, "Audit: filter train", $"/api/v1/audit?train={Pick(u, _seed.StreamTrains)}&limit=100"); break;
            case 4: await Get(ctx, u, "Audit: filter entity type", "/api/v1/audit?entity=ChecklistTask&limit=100"); break;
            case 5: await Get(ctx, u, "Audit: filter actor", $"/api/v1/audit?actor={Pick(u, _userIds)}&limit=100"); break;
            case 6: await Get(ctx, u, "Audit: filter action (contains)", "/api/v1/audit?action=Certif&limit=100"); break;
            case 7:
                var to = DateTime.UtcNow.AddDays(-u.Rnd.Next(1, 100)); var from = to.AddDays(-7);
                await Get(ctx, u, "Audit: filter date range (7 days)", $"/api/v1/audit?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&limit=100"); break;
            default:
                await Get(ctx, u, "Audit: entity types (filter line)", "/api/v1/audit/entity-types");
                if (_seed.EntityIds.Length > 0) await Get(ctx, u, "Audit: filter entity id", $"/api/v1/audit?entity=ChecklistTask&entityId={Pick(u, _seed.EntityIds)}&limit=100");
                break;
        }
    }

    private async Task Analytics(IScenarioContext ctx, VUser u)
    {
        var m = 1 + u.Rnd.Next(15);
        await Get(ctx, u, $"Analytics: M{m:00}", $"/api/v1/analytics/M{m}");
    }

    private async Task Calendar(IScenarioContext ctx, VUser u)
    {
        var d = DateTime.UtcNow.AddMonths(u.Rnd.Next(-1, 2)); var first = new DateTime(d.Year, d.Month, 1);
        await Get(ctx, u, "Calendar: month range", $"/api/v1/calendar?from={first:yyyy-MM-dd}&to={first.AddMonths(1).AddDays(-1):yyyy-MM-dd}");
    }

    private async Task MyWork(IScenarioContext ctx, VUser u)
    {
        await Get(ctx, u, "My work: GET /me/work", "/api/v1/me/work");
        await Get(ctx, u, "Inbox: GET /me/notifications/count", "/api/v1/me/notifications/count");
    }

    private async Task Inbox(IScenarioContext ctx, VUser u)
    {
        await Get(ctx, u, "Inbox: GET /me/notifications", "/api/v1/me/notifications?limit=50");
        if (u.Rnd.Next(3) == 0) await Get(ctx, u, "Inbox: GET /me/notifications (unread)", "/api/v1/me/notifications?unread=true&limit=50");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ plumbing

    private static T Pick<T>(VUser u, IReadOnlyList<T> list) => list[u.Rnd.Next(list.Count)];

    private Task<Result> Get(IScenarioContext ctx, VUser u, string name, string url) => Send(ctx, u, name, HttpMethod.Get, url, null, null, write: false);

    public readonly record struct Result(int Status, JsonElement? Json);

    private async Task<Result> Send(IScenarioContext ctx, VUser u, string name, HttpMethod method, string url, object? body, int? ifMatch, bool write, bool conflictOk = false)
    {
        Result result = default;
        await Step.Run(name, ctx, async () =>
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body);
            else if (method != HttpMethod.Get) req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            if (ifMatch is int v) req.Headers.TryAddWithoutValidation("If-Match", v.ToString());
            var t0 = Stopwatch.GetTimestamp();
            int status = 0; string text = ""; string? failure = null;
            try
            {
                using var resp = await u.Http.SendAsync(req);
                text = await resp.Content.ReadAsStringAsync();   // the whole response counts: the user waits for the body
                status = (int)resp.StatusCode;
            }
            catch (Exception ex) { failure = $"{ex.GetType().Name}: {ex.Message}"; }
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            if (text.Contains("database is locked", StringComparison.OrdinalIgnoreCase) || text.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase) || text.Contains("SQLite Error 5", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _rec.BusyHits);
            var outcome = failure is null && status is >= 200 and < 300 ? Outcome.Ok : failure is null && status == 409 && conflictOk ? Outcome.ExpectedConflict : Outcome.Error;
            var detail = outcome == Outcome.Error ? (failure ?? $"{status} {(text.Length > 300 ? text[..300] : text)}") : null;
            JsonElement? json = null;
            if (status is 200 or 409 && text.Length > 0 && text[0] is '{' or '[') { try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { } }
            if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.Bombing) _rec.Add(new Recorder.Sample(name, write, ms, status, outcome, detail));
            result = new Result(status, json);
            return outcome == Outcome.Error ? Response.Fail<object>(statusCode: status.ToString(), message: detail) : Response.Ok<object>(statusCode: status.ToString(), sizeBytes: text.Length);
        });
        return result;
    }
}
