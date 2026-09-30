using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-39/40/41 pure rules: error classification, timing, external keys and the 5.3.7 mismatch rules.</summary>
public class SyncRulesTests
{
    // ---- classification -------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(200, null)] [InlineData(204, null)]
    [InlineData(401, ConnectorErrorKind.Auth)] [InlineData(403, ConnectorErrorKind.Auth)]
    [InlineData(404, ConnectorErrorKind.NotFound)] [InlineData(410, ConnectorErrorKind.NotFound)]
    [InlineData(408, ConnectorErrorKind.Timeout)] [InlineData(504, ConnectorErrorKind.Timeout)]
    [InlineData(429, ConnectorErrorKind.RateLimited)]
    [InlineData(500, ConnectorErrorKind.Server)] [InlineData(502, ConnectorErrorKind.Server)] [InlineData(503, ConnectorErrorKind.Server)]
    [InlineData(302, ConnectorErrorKind.Server)] [InlineData(400, ConnectorErrorKind.Server)]
    public void Http_status_is_classified(int status, ConnectorErrorKind? expected) => Assert.Equal(expected, ConnectorErrorClassifier.FromHttpStatus(status));

    [Theory]
    [InlineData(ConnectorErrorKind.Auth, "AuthFailed", true, "AuthFailed")]
    [InlineData(ConnectorErrorKind.NotFound, "NotFound", false, "NotFound")]
    [InlineData(ConnectorErrorKind.Timeout, "Unreachable", true, null)]
    [InlineData(ConnectorErrorKind.Network, "Unreachable", true, null)]
    [InlineData(ConnectorErrorKind.Server, "Unreachable", true, null)]
    [InlineData(ConnectorErrorKind.RateLimited, "RateLimited", true, null)]
    [InlineData(ConnectorErrorKind.Parse, "ParseError", false, null)]
    public void Each_class_maps_to_an_allowed_alert_kind_scope_and_link_state(ConnectorErrorKind k, string alertKind, bool wide, string? linkState)
    {
        Assert.Equal(alertKind, ConnectorErrorClassifier.AlertKind(k));
        Assert.Equal(wide, ConnectorErrorClassifier.IsConnectorWide(k));
        Assert.Equal(linkState, ConnectorErrorClassifier.LinkState(k));
        // the schema's SyncAlerts.Kind CHECK list
        Assert.Contains(alertKind, new[] { "AuthFailed", "Unreachable", "NotFound", "Mismatch", "RateLimited", "ParseError", "Stalled" });
    }

    [Fact]
    public void Credentials_never_print_their_secret()
    {
        var c = new ConnectorCredentials("ApiToken", "rae@example.com", "s3cr3t-token");
        Assert.DoesNotContain("s3cr3t-token", c.ToString());
        Assert.DoesNotContain("rae@example.com", c.ToString());
    }

    // ---- timing ---------------------------------------------------------------------------------------------------------------------
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(5), Fast = TimeSpan.FromMinutes(1);
    private static readonly DateTime T0 = new(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Poll_interval_is_five_minutes_and_one_while_a_window_is_open()
    {
        Assert.Equal(Slow, SyncIntervals.PollInterval(false, Slow, Fast));
        Assert.Equal(Fast, SyncIntervals.PollInterval(true, Slow, Fast));
    }

    [Fact]
    public void Stall_threshold_is_three_intervals_and_switches_to_fast_once_the_window_has_been_open_a_full_slow_interval()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SyncIntervals.StallThreshold(T0, null, Slow, Fast));
        Assert.Equal(TimeSpan.FromMinutes(15), SyncIntervals.StallThreshold(T0, T0.AddMinutes(-2), Slow, Fast));   // window just opened: the last cycle may be 5 min old
        Assert.Equal(TimeSpan.FromMinutes(3), SyncIntervals.StallThreshold(T0, T0.AddMinutes(-5), Slow, Fast));
    }

    [Fact]
    public void A_link_is_stale_after_three_intervals_without_a_sync_and_a_never_synced_link_is_not()
    {
        var th = TimeSpan.FromMinutes(15);
        Assert.False(SyncIntervals.IsStale(T0.AddMinutes(-14), T0, th));
        Assert.True(SyncIntervals.IsStale(T0.AddMinutes(-15), T0, th));
        Assert.False(SyncIntervals.IsStale(null, T0, th));
    }

    [Fact]
    public void Stalled_uses_the_later_of_the_last_cycle_and_the_watchdog_start()
    {
        var th = TimeSpan.FromMinutes(15);
        Assert.True(SyncIntervals.IsStalled(T0.AddMinutes(-15), T0.AddMinutes(-60), T0, th));
        Assert.False(SyncIntervals.IsStalled(T0.AddMinutes(-14), T0.AddMinutes(-60), T0, th));
        Assert.False(SyncIntervals.IsStalled(T0.AddMinutes(-600), T0.AddMinutes(-2), T0, th));   // just restarted: not a stall
        Assert.True(SyncIntervals.IsStalled(null, T0.AddMinutes(-15), T0, th));
    }

    [Fact]
    public void Backoff_doubles_honours_retry_after_and_never_exceeds_the_cap()
    {
        var b = TimeSpan.FromSeconds(30); var cap = TimeSpan.FromMinutes(5);
        Assert.Equal(TimeSpan.FromSeconds(30), SyncIntervals.Backoff(1, b, null, cap));
        Assert.Equal(TimeSpan.FromSeconds(60), SyncIntervals.Backoff(2, b, null, cap));
        Assert.Equal(TimeSpan.FromSeconds(120), SyncIntervals.Backoff(3, b, null, cap));
        Assert.Equal(cap, SyncIntervals.Backoff(9, b, null, cap));
        Assert.Equal(TimeSpan.FromSeconds(90), SyncIntervals.Backoff(1, b, TimeSpan.FromSeconds(90), cap));
        Assert.Equal(cap, SyncIntervals.Backoff(1, b, TimeSpan.FromHours(2), cap));
        Assert.Equal(TimeSpan.FromSeconds(30), SyncIntervals.Backoff(0, b, null, cap));
    }

    // ---- keys -----------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Jira", "PAY-123", true)] [InlineData("Jira", "PAY/4.5.0", true)] [InlineData("Jira", "PAY/Card Portal 2.1", true)]
    [InlineData("Jira", "pay-123", false)] [InlineData("Jira", "PAY-", false)] [InlineData("Jira", "PAY/../x", false)] [InlineData("Jira", "PAY/a?b=c", false)]
    [InlineData("Jira", "http://evil/x", false)] [InlineData("Jira", "", false)]
    [InlineData("ServiceNow", "CHG0030001", true)] [InlineData("ServiceNow", "CTASK0010001", true)]
    [InlineData("ServiceNow", "chg0030001", false)] [InlineData("ServiceNow", "CHG1", false)] [InlineData("ServiceNow", "CHG0030001^ORactive=true", false)]
    [InlineData("Other", "CHG0030001", false)]
    public void External_key_shapes_are_narrow(string source, string key, bool valid) => Assert.Equal(valid, ExternalKeys.Validate(source, key) is null);

    [Fact]
    public void Keys_are_normalised_without_touching_a_fix_version_name()
    {
        Assert.Equal("CHG0030001", ExternalKeys.Normalize("ServiceNow", " chg0030001 "));
        Assert.Equal("PAY-12", ExternalKeys.Normalize("Jira", "pay-12"));
        Assert.Equal("PAY/4.5.0-rc1", ExternalKeys.Normalize("Jira", "pay/4.5.0-rc1"));
    }

    [Theory]
    [InlineData("-1", "Implement")] [InlineData("3", "Closed")] [InlineData("-2", "Scheduled")] [InlineData("Implement", "Implement")]
    public void Change_state_codes_have_names(string raw, string name) => Assert.Equal(name, ExternalKeys.ChangeStateName(raw));

    // ---- 5.3.7 mismatch rules ---------------------------------------------------------------------------------------------------------
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Target = new(2026, 10, 30);
    private static readonly DateTime WS = new(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc), WE = WS.AddHours(4);

    private static MismatchInput Chg(string train, string state, DateTime? ended = null, DateTime? ps = null, DateTime? pe = null, DateTime? now = null) =>
        new("ServiceNow", "CHG0030001", new ExternalStatus("CHG0030001", state, ps, pe), train, Target, ended, WS, WE, now ?? Now);
    private static MismatchInput Fix(string train, string state, bool released, DateOnly? date = null, DateTime? ended = null) =>
        new("Jira", "PAY/4.5.0", new ExternalStatus("PAY/4.5.0", state, ReleaseDate: date, Released: released), train, Target, ended, WS, WE, Now);

    [Fact]
    public void Rule1_train_executing_but_change_not_in_implement()
    {
        Assert.Equal([MismatchRules.ChgNotImplementing], MismatchRules.Evaluate(Chg("Executing", "Scheduled")).Select(f => f.Rule));
        Assert.Empty(MismatchRules.Evaluate(Chg("Executing", "Implement")));
        Assert.Empty(MismatchRules.Evaluate(Chg("Gated", "Scheduled")));   // only while Executing
    }

    [Fact]
    public void Rule2_train_complete_but_change_not_closed_after_24h()
    {
        Assert.Empty(MismatchRules.Evaluate(Chg("Complete", "Review", ended: Now.AddHours(-23))));
        Assert.Equal([MismatchRules.ChgNotClosed], MismatchRules.Evaluate(Chg("Complete", "Review", ended: Now.AddHours(-24))).Select(f => f.Rule));
        Assert.Empty(MismatchRules.Evaluate(Chg("Complete", "Closed", ended: Now.AddDays(-3))));
    }

    [Fact]
    public void Rule3_train_complete_but_fix_version_not_released_after_24h()
    {
        Assert.Empty(MismatchRules.Evaluate(Fix("Complete", "Unreleased", false, ended: Now.AddHours(-2))));
        Assert.Equal([MismatchRules.FixVersionNotReleased], MismatchRules.Evaluate(Fix("Complete", "Unreleased", false, ended: Now.AddHours(-25))).Select(f => f.Rule));
        Assert.Empty(MismatchRules.Evaluate(Fix("Complete", "Released", true, ended: Now.AddHours(-25))));
    }

    [Fact]
    public void Rule4_change_planned_window_differs_from_the_deployment_window()
    {
        Assert.Empty(MismatchRules.Evaluate(Chg("Planning", "Scheduled", ps: WS, pe: WE)));
        var f = Assert.Single(MismatchRules.Evaluate(Chg("Planning", "Scheduled", ps: WS.AddHours(1), pe: WE)));
        Assert.Equal(MismatchRules.ChgWindowDiffers, f.Rule);
        Assert.Contains("2026-10-30T03:00Z", f.Message);
        Assert.Empty(MismatchRules.Evaluate(Chg("Planning", "Scheduled")));                                         // no planned dates: no data, no mismatch
        Assert.Empty(MismatchRules.Evaluate(Chg("Planning", "Scheduled", ps: WS.AddMinutes(4), pe: WE), TimeSpan.FromMinutes(5)));   // within tolerance
    }

    [Fact]
    public void Rule5_fix_version_release_date_differs_from_the_train_target()
    {
        Assert.Empty(MismatchRules.Evaluate(Fix("Planning", "Unreleased", false, Target)));
        Assert.Equal([MismatchRules.FixVersionDateDiffers], MismatchRules.Evaluate(Fix("Planning", "Unreleased", false, Target.AddDays(2))).Select(f => f.Rule));
        Assert.Empty(MismatchRules.Evaluate(Fix("Planning", "Unreleased", false)));
    }

    [Fact]
    public void Findings_and_expected_status_hold_keys_states_and_dates_only()
    {
        var f = MismatchRules.Evaluate(Chg("Executing", "Scheduled")).Single();
        Assert.Equal("CHG0030001 is Scheduled; the train is Executing, so the change should be in Implement", f.Message);
        Assert.Equal("Implement", MismatchRules.ExpectedStatus("ServiceNow", "CHG0030001", "Executing"));
        Assert.Equal("Closed", MismatchRules.ExpectedStatus("ServiceNow", "CHG0030001", "Complete"));
        Assert.Equal("Released", MismatchRules.ExpectedStatus("Jira", "PAY/4.5.0", "Complete"));
        Assert.Null(MismatchRules.ExpectedStatus("Jira", "PAY-12", "Complete"));
    }
}
