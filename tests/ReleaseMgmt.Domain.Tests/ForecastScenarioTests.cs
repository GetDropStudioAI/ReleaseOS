using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>The pure forecast engine against the handoff scenario tests/reference/fixtures/r26-24-scenario.json (M3's acceptance scenario).</summary>
public class ForecastScenarioTests
{
    private static string Fixtures([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "reference", "fixtures"));
    private static DateTime U(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static (JsonElement Scenario, List<ForecastStep> Steps) Load(DateTime clockNow)
    {
        var scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "r26-24-scenario.json"))).RootElement;
        var rows = File.ReadAllLines(Path.Combine(Fixtures(), "r26-24-runbook.csv")).Skip(1).Select(l => l.Split(',')).ToList();
        var steps = new List<ForecastStep>();
        foreach (var c in rows)
        {
            string actualStart = c[9], actualEnd = c[10];
            var status = actualEnd != "" ? "Done" : actualStart != "" ? "Running" : "Scheduled";
            steps.Add(new ForecastStep(c[1], c[3], U(c[4]), int.Parse(c[5]), c[8] == "" ? [] : c[8].Split(';'), status,
                actualStart == "" ? null : U(actualStart), actualEnd == "" ? null : U(actualEnd)));
        }
        return (scenario, steps);
    }

    [Fact]
    public void The_handoff_scenario_yields_exactly_its_expected_block()
    {
        var (scenario, steps) = Load(default);
        var now = U(scenario.GetProperty("clockNow").GetString()!);
        var windowEnd = U(scenario.GetProperty("setup").GetProperty("deploymentWindow").GetProperty("endsAt").GetString()!);
        var expected = scenario.GetProperty("expected");

        var r = RunForecasting.Compute(now, windowEnd, steps);

        Assert.Equal(U(expected.GetProperty("forecastFinish").GetString()!), r.ForecastFinish);
        Assert.Equal(U(expected.GetProperty("plannedFinish").GetString()!), r.PlannedFinish);
        Assert.Equal(expected.GetProperty("rollbackPlannedMin").GetInt32(), r.RollbackPlannedMin);
        Assert.Equal(U(expected.GetProperty("rollbackDeadline").GetString()!), r.RollbackDeadline);
        Assert.Equal(expected.GetProperty("crossesDeadlineByMin").GetInt32(), r.CrossesDeadlineByMin);
        Assert.Equal(expected.GetProperty("alertRaised").GetBoolean(), r.AlertRaised);
        Assert.Equal(expected.GetProperty("windowClosesInSec").GetInt32(), r.WindowClosesInSec);          // 8,566 s
        Assert.Empty(r.BlockedByFailed);

        var exp = expected.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(exp.Count, r.Steps.Count);                                                             // R-001..R-012; the rollback steps are not part of the forecast
        for (var i = 0; i < exp.Count; i++)
        {
            var e = exp[i]; var a = r.Steps[i];
            Assert.Equal(e.GetProperty("step").GetString(), a.Step);
            Assert.Equal(e.GetProperty("state").GetString(), a.State);
            Assert.Equal(U(e.GetProperty("start").GetString()!), a.Start);
            Assert.Equal(U(e.GetProperty("end").GetString()!), a.End);
            Assert.Equal(e.GetProperty("endVarianceMin").GetInt32(), a.EndVarianceMin);
        }
        Assert.Equal([1, 3, 5, 16, 19], r.Steps.Take(5).Select(s => s.EndVarianceMin!.Value).ToArray());   // the +1, +3, +5, +16, +19 of the acceptance text
    }

    [Fact]
    public void A_running_step_that_is_overdue_ends_now_and_pushes_its_successors()
    {
        var (scenario, steps) = Load(default);
        var now = U("2026-10-30T07:50:00Z");                                                                // R-006 (20 min from 07:24) should have ended at 07:44
        var r = RunForecasting.Compute(now, U("2026-10-30T10:00:00Z"), steps);
        Assert.Equal(now, r.Steps.Single(s => s.Step == "R-006").End);
        Assert.Equal(now, r.Steps.Single(s => s.Step == "R-007").Start);                                   // a successor cannot start before the running step ends
        Assert.Equal(U("2026-10-30T09:35:00Z"), r.ForecastFinish);                                          // the scenario finish (09:29) plus the 6 minutes R-006 has overrun
    }

    [Fact]
    public void A_failed_step_blocks_everything_after_it_and_the_finish_is_unknown()
    {
        var (scenario, steps) = Load(default);
        var i = steps.FindIndex(s => s.Code == "R-006");
        steps[i] = steps[i] with { Status = "Failed", ActualEnd = null };
        var r = RunForecasting.Compute(U("2026-10-30T07:37:14Z"), U("2026-10-30T10:00:00Z"), steps);
        Assert.Equal(["R-006"], r.BlockedByFailed);
        Assert.Null(r.ForecastFinish);
        Assert.False(r.AlertRaised);
        Assert.Null(r.Steps.Single(s => s.Step == "R-007").End);
        Assert.NotNull(r.Steps.Single(s => s.Step == "R-005").End);                                         // what already happened is still reported
    }

    [Fact]
    public void A_skipped_step_is_transparent_and_a_scheduled_step_never_starts_in_the_past()
    {
        var (scenario, steps) = Load(default);
        var i = steps.FindIndex(s => s.Code == "R-006");
        steps[i] = steps[i] with { Status = "Skipped", ActualStart = null };
        var now = U("2026-10-30T07:37:14Z");
        var r = RunForecasting.Compute(now, U("2026-10-30T10:00:00Z"), steps);
        Assert.Equal(U("2026-10-30T07:24:00Z"), r.Steps.Single(s => s.Step == "R-006").End);               // ends when R-005 ends
        Assert.Equal(now, r.Steps.Single(s => s.Step == "R-007").Start);                                    // planned 07:25 is in the past: it can start no earlier than now
    }

    [Fact]
    public void Without_a_window_there_is_no_deadline_and_no_alert()
    {
        var (scenario, steps) = Load(default);
        var r = RunForecasting.Compute(U("2026-10-30T07:37:14Z"), null, steps);
        Assert.Null(r.RollbackDeadline); Assert.Null(r.WindowClosesInSec); Assert.False(r.AlertRaised);
    }

    [Fact]
    public void A_run_that_is_on_time_does_not_alert()
    {
        var steps = new List<ForecastStep>
        {
            new("R-001", "Deploy", U("2026-10-30T06:00:00Z"), 30, [], "Done", U("2026-10-30T06:00:00Z"), U("2026-10-30T06:30:00Z")),
            new("R-002", "Deploy", U("2026-10-30T06:30:00Z"), 30, ["R-001"], "Scheduled", null, null),
            new("R-901", "Rollback", U("2026-10-30T07:00:00Z"), 30, [], "Scheduled", null, null),
        };
        var r = RunForecasting.Compute(U("2026-10-30T06:31:00Z"), U("2026-10-30T09:00:00Z"), steps);
        Assert.Equal(U("2026-10-30T07:01:00Z"), r.ForecastFinish);                                          // R-002 starts now (06:31), not at its planned 06:30
        Assert.Equal(U("2026-10-30T08:30:00Z"), r.RollbackDeadline);
        Assert.False(r.AlertRaised);
        Assert.Null(r.CrossesDeadlineByMin);
    }
}
