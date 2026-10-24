using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-139: the one message style (PROJECT_SCOPE 5.2): workspace, release key and title, UTC times labelled, a link only when there is one.</summary>
public class MessageStyleTests
{
    private static readonly MessageSubject Full = new("Payments", "RLS-284", "Card portal", "https://releaseos.example.com/trains/t1");

    [Fact]
    public void A_message_names_the_workspace_the_key_and_the_title_then_the_body_then_the_link() =>
        Assert.Equal("Payments · RLS-284 Card portal: Gate 'Code Freeze' is overdue (was due 2026-10-30 UTC). Open: https://releaseos.example.com/trains/t1",
            MessageStyle.Compose(Full, "Gate 'Code Freeze' is overdue (was due 2026-10-30 UTC)."));

    [Fact]
    public void Without_a_configured_base_there_is_no_link_and_nothing_is_invented() =>
        Assert.Equal("Payments · RLS-284 Card portal: Step R-001 has not started", MessageStyle.Compose(Full with { Link = null }, "Step R-001 has not started"));

    [Fact]
    public void A_message_that_is_not_about_a_release_names_the_workspace_alone() =>
        Assert.Equal("Beta: Jira AuthFailed: 401", MessageStyle.Compose(new("Beta", null, null, null), "Jira AuthFailed: 401"));

    [Fact]
    public void A_release_without_a_key_shows_its_title_and_one_without_a_title_its_key()
    {
        Assert.Equal("Beta · Card portal: x", MessageStyle.Compose(new("Beta", null, "Card portal", null), "x"));
        Assert.Equal("Beta · RLS-1: x", MessageStyle.Compose(new("Beta", "RLS-1", null, null), "x"));
    }

    [Fact]
    public void Times_are_utc_and_say_so()
    {
        Assert.Equal("2026-10-30 14:05 UTC", MessageStyle.Utc(new DateTime(2026, 10, 30, 14, 5, 59, DateTimeKind.Utc)));
        Assert.Equal("2026-10-30 UTC", MessageStyle.Utc(new DateOnly(2026, 10, 30)));
        Assert.Equal("2026-10-30 09:35 UTC", MessageStyle.Utc("2026-10-30T09:35:00Z"));
        Assert.Equal("2026-10-30 09:35 UTC", MessageStyle.Utc("2026-10-30T04:35:00-05:00"));
        Assert.Equal("not a time", MessageStyle.Utc("not a time"));
        Assert.Equal("", MessageStyle.Utc((string?)null));
    }
}
