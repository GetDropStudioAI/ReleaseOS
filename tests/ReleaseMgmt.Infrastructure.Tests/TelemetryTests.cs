using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Infrastructure.Tests.OpsKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-111: metric samples, thresholds with a duration, breach detection, canary analysis, SLO and error budget, retention, and workspace scope.</summary>
public sealed class TelemetryTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private OpsKit Kit() => new OpsKit(fx).Seed();

    [Fact]
    public async Task Samples_are_stored_once_and_a_replay_changes_nothing_and_writes_nothing()
    {
        var k = Kit();
        var batch = Run("Canary", T0, 3, er: 0.1, lat: 120, av: 99.99);
        var r = await k.Telemetry.IngestAsync("ci-1", batch, Key);
        Assert.True(r.IsOk, string.Join(";", r.Failures.Select(f => f.Message)));
        Assert.Equal((3, 0, 0), (r.Value!.Accepted, r.Value.Duplicates, r.Value.Late));
        Assert.Equal(3, k.Count("SELECT count(*) FROM MetricSamples"));
        var version = k.Count("SELECT Version FROM Deployments WHERE Id='d1'");
        Assert.Equal(1, k.Audits("MetricSample", "Append"));

        var again = await k.Telemetry.IngestAsync("ci-1", batch, Key);
        Assert.True(again.IsOk);
        Assert.Equal((0, 3), (again.Value!.Accepted, again.Value.Duplicates));
        Assert.Equal(3, k.Count("SELECT count(*) FROM MetricSamples"));
        Assert.Equal(version, k.Count("SELECT Version FROM Deployments WHERE Id='d1'"));
        Assert.Equal(1, k.Audits("MetricSample", "Append"));   // nothing written for a replay

        // one call, one audit row and one Version bump, however many samples it carries
        var more = await k.Telemetry.IngestAsync("ci-1", Run("Stable", T0, 5, er: 0.05), Key);
        Assert.Equal(5, more.Value!.Accepted);
        Assert.Equal(2, k.Audits("MetricSample", "Append"));
        Assert.Equal(version + 1, k.Count("SELECT Version FROM Deployments WHERE Id='d1'"));
    }

    [Fact]
    public async Task A_breach_is_watching_until_it_has_lasted_its_duration_and_is_confirmed_by_the_sample_that_completes_it()
    {
        var k = Kit();
        // error rate above 0.5 % for 120 s; a sample every 30 s: breach starts at T0, lasts 120 s at the fifth sample
        for (var i = 0; i < 4; i++)
        {
            var r = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(30 * i), er: 0.9)], Key);
            Assert.Equal(0, r.Value!.BreachesConfirmed);   // 0, 30, 60, 90 s: not yet 120 s
        }
        var watching = Assert.Single(await k.Telemetry.BreachesAsync(k.Text("SELECT Id FROM Deployments"), true));
        Assert.Equal(("Watching", "ErrorRate", "Canary"), (watching.Status, watching.Metric, watching.Series));
        Assert.Null(watching.ConfirmedAt);

        var fifth = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(120), er: 1.4)], Key);
        Assert.Equal(1, fifth.Value!.BreachesConfirmed);   // detected by the one sample that completes the duration: within one sample interval
        var breached = Assert.Single(await k.Telemetry.BreachesAsync("d1"));
        Assert.Equal("Breached", breached.Status);
        Assert.Equal(("2026-10-20T14:02:00Z", 5, 1.4, 0.5), (breached.ConfirmedAt, breached.SampleCount, breached.PeakValue, breached.ThresholdValue));
        Assert.Equal("2026-10-20T14:00:00Z", breached.StartedAt);

        // a further sample beyond the threshold does not confirm it again
        Assert.Equal(0, (await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(150), er: 0.7)], Key)).Value!.BreachesConfirmed);
        // the first sample back inside resolves it
        var back = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(180), er: 0.1)], Key);
        Assert.Equal(1, back.Value!.BreachesResolved);
        Assert.Equal("Resolved", Assert.Single(await k.Telemetry.BreachesAsync("d1")).Status);
        Assert.Equal((1, 1, 1), (k.Audits("ThresholdBreach", "Open"), k.Audits("ThresholdBreach", "Confirm"), k.Audits("ThresholdBreach", "Resolve")));
    }

    [Fact]
    public async Task A_spike_shorter_than_the_duration_is_never_confirmed()
    {
        var k = Kit();
        await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0, er: 0.9), S("Canary", T0.AddSeconds(30), er: 0.9), S("Canary", T0.AddSeconds(60), er: 0.1)], Key);
        var b = Assert.Single(await k.Telemetry.BreachesAsync("d1"));
        Assert.Equal(("Resolved", null), (b.Status, b.ConfirmedAt));
        Assert.Equal(0, k.Audits("ThresholdBreach", "Confirm"));
    }

    [Fact]
    public async Task Latency_and_availability_have_their_own_thresholds_and_a_zero_duration_confirms_at_once()
    {
        var k = Kit();
        k.Sql("UPDATE Environments SET ThresholdForSeconds=0 WHERE Id='e1'");
        var r = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0, lat: 410, av: 99.1)], Key);
        Assert.Equal((2, 2), (r.Value!.BreachesOpened, r.Value.BreachesConfirmed));
        var metrics = (await k.Telemetry.BreachesAsync("d1")).Select(b => (b.Metric, b.Status)).Order().ToList();
        Assert.Equal([("Availability", "Breached"), ("LatencyP95", "Breached")], metrics);
        // exactly at a limit is inside it
        k.Sql("UPDATE ThresholdBreaches SET ResolvedAt=StartedAt");
        Assert.Equal(0, (await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(30), er: 0.5, lat: 300, av: 99.5)], Key)).Value!.BreachesOpened);
    }

    [Fact]
    public async Task A_late_sample_is_kept_but_does_not_move_the_breach_state()
    {
        var k = Kit();
        await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(60), er: 0.1)], Key);
        var r = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(30), er: 5.0)], Key);   // older than the newest canary sample
        Assert.Equal((1, 1, 0), (r.Value!.Accepted, r.Value.Late, r.Value.BreachesOpened));
        Assert.Equal(2, k.Count("SELECT count(*) FROM MetricSamples"));
        Assert.Empty(await k.Telemetry.BreachesAsync("d1"));
    }

    [Fact]
    public async Task Invalid_samples_are_refused_and_nothing_is_stored()
    {
        var k = Kit();
        async Task<string[]> Refused(params IngestSample[] s) { var r = await k.Telemetry.IngestAsync("ci-1", s, Key); Assert.Equal(ResultKind.GuardFailed, r.Kind); return GuardsOf(r); }
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Beta", T0, er: 1)));
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0)));   // no metric
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0, er: 120)));
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0, lat: -1)));
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0, av: double.NaN)));
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0.AddHours(1), er: 1)));           // the future
        Assert.Equal([DeploymentGuards.MetricInvalid], await Refused(S("Canary", T0.AddDays(-31), er: 1)));          // older than the retention
        Assert.Equal([DeploymentGuards.MetricInvalid], (GuardsOf(await k.Telemetry.IngestAsync("ci-1", [], Key))));
        Assert.Equal([DeploymentGuards.MetricInvalid], (GuardsOf(await k.Telemetry.IngestAsync("ci-1", Run("Canary", T0, 201, er: 0.1), Key))));   // more than a call may carry
        Assert.Equal(ResultKind.NotFound, (await k.Telemetry.IngestAsync("nope", [S("Canary", T0, er: 1)], Key)).Kind);
        Assert.Equal(0, k.Count("SELECT count(*) FROM MetricSamples"));
        Assert.Equal(0, k.Audits("MetricSample", "Append"));
    }

    [Fact]
    public async Task A_key_of_another_workspace_finds_nothing_and_writes_nothing()
    {
        var k = Kit();
        k.Sql($"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Iso(T0)}')");
        var r = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0, er: 5)], new IngestPrincipal("ik9", "EU CI", "w2"));
        Assert.Equal(ResultKind.NotFound, r.Kind);
        Assert.Equal(0, k.Count("SELECT count(*) FROM MetricSamples"));
        using (WorkspaceScope.Enter("w2"))
        {
            Assert.Null(await k.Telemetry.MetricsAsync("d1"));
            Assert.Null(await k.Telemetry.CanaryAsync("d1"));
            Assert.Null(await k.Telemetry.SloAsync("e1"));
            Assert.Empty(await k.Telemetry.BreachesAsync("d1"));
        }
        await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0, er: 5)], Key);
        using (WorkspaceScope.Enter("w2")) Assert.Null(await k.Telemetry.MetricsAsync("d1"));   // and nothing of the default workspace's is returned once there is some
    }

    [Fact]
    public async Task The_metrics_read_returns_series_threshold_lines_and_the_canary_start_for_the_chart()
    {
        var k = Kit();
        await k.Telemetry.IngestAsync("ci-1", [.. Run("Canary", T0, 6, er: 0.2, lat: 150, av: 99.95), .. Run("Stable", T0, 6, er: 0.1, lat: 120, av: 99.99)], Key);
        var m = (await k.Telemetry.MetricsAsync("d1"))!;
        Assert.Equal(("Canary", "Running", "2026-10-20T14:00:00Z"), (m.Strategy, m.State, m.CanaryStartedAt));
        Assert.Equal(new ThresholdLines(0.5, 300, 99.5, 120), m.Thresholds);
        Assert.Equal(new SloTargets(99.9, 250, 0.4), m.Slo);
        Assert.Equal(["Canary", "Stable"], m.Series.Select(s => s.Series));
        Assert.All(m.Series, s => Assert.Equal(6, s.Points.Count));
        Assert.Equal(0, m.BucketSeconds);
        Assert.Equal("2026-10-20T14:02:30Z", m.LastSampleAt);
        // one series only
        Assert.Equal(["Stable"], (await k.Telemetry.MetricsAsync("d1", new("Stable")))!.Series.Select(s => s.Series));
        // many points are bucketed, and the bucket keeps the worst value so a spike does not disappear
        k.Time.Advance(TimeSpan.FromHours(1));   // samples are refused when they are ahead of the server's clock
        var bulk = await k.Telemetry.IngestAsync("ci-1", [.. Run("Canary", T0.AddMinutes(10), 100, er: 0.1), S("Canary", T0.AddMinutes(10).AddSeconds(3030), er: 0.9)], Key);
        Assert.True(bulk.IsOk, string.Join(";", bulk.Failures.Select(f => f.Message)));
        var thin = (await k.Telemetry.MetricsAsync("d1", new("Canary", MaxPoints: 10)))!;
        var points = thin.Series.Single().Points;
        Assert.True(thin.BucketSeconds > 0 && points.Count <= 12, $"{points.Count} points, bucket {thin.BucketSeconds}");
        Assert.Contains(points, p => p.ErrorRatePct == 0.9);
    }

    [Fact]
    public async Task Canary_analysis_names_the_step_the_window_the_next_promotion_and_the_verdict()
    {
        var k = Kit();
        var c = (await k.Telemetry.CanaryAsync("d1"))!;
        Assert.Equal((1, 4, 5.0, 25.0), (c.CurrentStep, c.StepCount, c.TrafficPercent, c.NextTrafficPercent));
        Assert.Equal("Unknown", c.Result);   // no canary samples yet
        Assert.Equal((600, false, "2026-10-20T14:10:00Z"), (c.ObservationWindowSeconds, c.WindowComplete, c.NextPromotionAt));
        k.Time.Advance(TimeSpan.FromSeconds(630));
        await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(600), er: 0.2, lat: 140, av: 99.99)], Key);
        c = (await k.Telemetry.CanaryAsync("d1"))!;
        Assert.Equal(("Pass", true, true), (c.Result, c.WindowComplete, c.ReadyToPromote));
        Assert.Equal(630, c.ObservedSeconds);

        // the next step starts a new window
        await k.Deployments.UpsertAsync(new("ci-1", TrafficPercent: 25), Key);
        c = (await k.Telemetry.CanaryAsync("d1"))!;
        Assert.Equal((2, 25.0, 50.0, "2026-10-20T14:20:30Z", false), (c.CurrentStep, c.TrafficPercent, c.NextTrafficPercent, c.NextPromotionAt, c.WindowComplete));

        // a confirmed breach on the canary series fails it; one beyond a threshold but short of its duration is at risk
        await k.Telemetry.IngestAsync("ci-1", Run("Canary", T0.AddSeconds(660), 1, er: 2.0), Key);
        c = (await k.Telemetry.CanaryAsync("d1"))!;
        Assert.Equal(("Pass", true, false), (c.Result, c.AtRisk, c.ReadyToPromote));
        await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddSeconds(780), er: 2.1)], Key);
        c = (await k.Telemetry.CanaryAsync("d1"))!;
        Assert.Equal(("Fail", false), (c.Result, c.ReadyToPromote));
        Assert.Equal("ErrorRate", c.Checks.Single(x => x.Passed == false).Metric);
    }

    [Fact]
    public async Task The_error_budget_is_what_the_objective_allows_to_fail_and_how_much_of_it_is_left()
    {
        var k = Kit();
        // weighted by requests: 9,000 requests at 100 % and 1,000 at 99 %: 99.9 % measured against a 99.9 % objective
        await k.Telemetry.IngestAsync("ci-1", [S("Stable", T0, av: 100, req: 9000), S("Stable", T0.AddSeconds(30), av: 99, req: 1000)], Key);
        var slo = (await k.Telemetry.SloAsync("e1", 30))!;
        Assert.Equal((99.9, 99.9, true), (slo.Availability.Objective, Math.Round(slo.Availability.Measured!.Value, 6), slo.Availability.Meets));
        Assert.Equal(2, slo.Samples);
        Assert.NotNull(slo.ErrorBudget);
        Assert.Equal(0.1, slo.ErrorBudget!.BudgetPct, 6);
        Assert.Equal(0.1, slo.ErrorBudget.ConsumedPct, 6);
        Assert.Equal(0.0, slo.ErrorBudget.RemainingPct, 4);

        var budget = SloMath.Budget(99.9, 99.95);
        Assert.Equal((true, 50.0, false), (budget.Meets, Math.Round(budget.RemainingPct, 6), budget.Exhausted));
        var overspent = SloMath.Budget(99.9, 99.5);
        Assert.Equal((false, true), (overspent.Meets, overspent.Exhausted));
        Assert.True(overspent.RemainingPct < 0);
        Assert.Equal(100, SloMath.Budget(100, 100).RemainingPct);
        Assert.True(SloMath.Budget(100, 99.99).Exhausted);
    }

    [Fact]
    public async Task Samples_past_retention_are_summarised_into_rollups_and_dropped_and_still_count_toward_the_slo()
    {
        var k = Kit();
        var old = T0.AddDays(-40);
        k.Sql($@"INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,LatencyP95Ms,AvailabilityPct,RequestCount,WorkspaceId) VALUES
            ('d1','Canary','{Iso(old)}','{Iso(old)}',0.2,100,99.0,1000,'default'),('d1','Canary','{Iso(old.AddSeconds(30))}','{Iso(old)}',0.6,300,99.0,1000,'default'),('d1','Stable','{Iso(old)}','{Iso(old)}',0.1,90,100,500,'default')");
        await k.Telemetry.IngestAsync("ci-1", [S("Stable", T0, av: 100, req: 2000)], Key);
        var r = await k.Telemetry.RunRetentionAsync();
        Assert.True(r.IsOk);
        Assert.Equal((3, 2), (r.Value!.SamplesDropped, r.Value.BucketsWritten));
        Assert.Equal(1, k.Count("SELECT count(*) FROM MetricSamples"));   // today's stays
        var canary = k.Query("SELECT Samples, ErrorRateAvgPct, ErrorRateMaxPct, LatencyP95MaxMs, AvailabilityMinPct, RequestCount FROM MetricRollups WHERE Series='Canary'").Single();
        Assert.Equal((2L, 0.4, 0.6, 300.0, 99.0, 2000L), (Convert.ToInt64(canary[0]), Convert.ToDouble(canary[1]), Convert.ToDouble(canary[2]), Convert.ToDouble(canary[3]), Convert.ToDouble(canary[4]), Convert.ToInt64(canary[5])));
        Assert.Equal(1, k.Audits("MetricSample", "Summarise"));
        // idempotent: nothing left to do, nothing written
        Assert.Equal(0, (await k.Telemetry.RunRetentionAsync()).Value!.SamplesDropped);
        Assert.Equal(1, k.Audits("MetricSample", "Summarise"));
        // the old hours still count over a long window
        var slo = (await k.Telemetry.SloAsync("e1", 60))!;
        Assert.Equal((1, 3), (slo.Samples, slo.RolledUpSamples));
        Assert.True(slo.Availability.Measured < 100);
        // and the chart over the old range gets the rollups
        var m = (await k.Telemetry.MetricsAsync("d1", new(From: old.AddHours(-1), To: old.AddHours(2))))!;
        Assert.Equal(2, m.Rollups.Count);
    }

    [Fact]
    public async Task The_retention_period_is_a_setting()
    {
        var k = new OpsKit(fx, new Dictionary<string, string?> { ["Telemetry:RetentionDays"] = "3" }).Seed();
        Assert.Equal(3, k.Telemetry.RetentionDays);
        var r = await k.Telemetry.IngestAsync("ci-1", [S("Canary", T0.AddDays(-4), er: 1)], Key);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);   // already past the 3 days it would be kept
    }

    [Fact]
    public async Task A_finished_deployment_closes_its_breaches_and_is_no_longer_armed()
    {
        var k = Kit();
        await k.Telemetry.IngestAsync("ci-1", [.. Run("Canary", T0, 5, er: 0.9)], Key);
        k.Sql($"UPDATE Deployments SET AutoRollbackArmedAt='{Iso(T0)}' WHERE Id='d1'");
        Assert.Equal("Breached", Assert.Single(await k.Telemetry.BreachesAsync("d1")).Status);
        var done = await k.Deployments.UpsertAsync(new("ci-1", State: "Failed"), Key);
        Assert.True(done.IsOk);
        Assert.Equal("Resolved", Assert.Single(await k.Telemetry.BreachesAsync("d1")).Status);
        Assert.Equal(0, k.Count("SELECT count(*) FROM Deployments WHERE AutoRollbackArmedAt IS NOT NULL"));
    }

    [Fact]
    public void The_pure_rules_agree_with_the_thresholds_as_stated()
    {
        Assert.True(ThresholdRules.IsBeyond(MetricNames.ErrorRate, 0.51, 0.5));
        Assert.False(ThresholdRules.IsBeyond(MetricNames.ErrorRate, 0.5, 0.5));
        Assert.True(ThresholdRules.IsBeyond(MetricNames.Availability, 99.4, 99.5));
        Assert.False(ThresholdRules.IsBeyond(MetricNames.Availability, 99.5, 99.5));
        Assert.Equal(99.0, ThresholdRules.Worse(MetricNames.Availability, 99.0, 99.9));
        Assert.Equal(900.0, ThresholdRules.Worse(MetricNames.LatencyP95, 900, 120));
        double[] steps = [5, 25, 50, 100];
        Assert.Equal((0, 1, 2, 4), (CanaryMath.CurrentStep(steps, 0), CanaryMath.CurrentStep(steps, 5), CanaryMath.CurrentStep(steps, 30), CanaryMath.CurrentStep(steps, 100)));
        Assert.Equal(25.0, CanaryMath.NextTraffic(steps, 5));
        Assert.Null(CanaryMath.NextTraffic(steps, 100));
    }
}
