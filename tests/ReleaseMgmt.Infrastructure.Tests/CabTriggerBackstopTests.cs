using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-177 (D56, D63): the trigger backstop of the separation-of-duties rules, proved with direct SQL on the migrated database (no service in the way), and the words each rule raises are the
/// words the service returns (<see cref="CabRuleText"/>). The complete oracle (every CHECK, workspace boundary and status move) is tests/reference/test_schema.py.
/// </summary>
public sealed class CabTriggerBackstopTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Now = TriggerSuiteFixture.Now;

    private static void Allowed(SqliteConnection db, string sql, string label, params object?[] p)
    {
        try { TriggerSuiteFixture.Run(db, sql, p); }
        catch (SqliteException e) { Assert.Fail($"{label}: expected allowed, blocked: {e.Message}"); }
    }

    private static void Blocked(SqliteConnection db, string sql, string expect, string label, params object?[] p)
    {
        var e = Assert.Throws<SqliteException>(() => TriggerSuiteFixture.Run(db, sql, p));
        Assert.Equal(19, e.SqliteErrorCode);
        Assert.True(e.Message.Contains(expect), $"{label}: expected '{expect}', got: {e.Message}");
    }

    private SqliteConnection Db()
    {
        var db = fx.Fresh();
        TriggerSuiteFixture.Run(db, $@"
            INSERT INTO ChangeRequests(Id,ChangeKey,Title,ChangeType,Status,RequestedByUserId,CreatedByUserId,CreatedAt) VALUES
              ('c1','CHG-1','Normal by rte','Normal','Submitted','rte','rte','{Now}'),
              ('c2','CHG-2','Keyed in by rm for dev','Normal','Submitted','dev','rm','{Now}'),
              ('e1','CHG-3','Emergency by rte','Emergency','Submitted','rte','rte','{Now}');
            INSERT INTO CabMeetings(Id,Title,StartsAt,EndsAt,ChairUserId,CreatedByUserId,CreatedAt) VALUES('m1','CAB','2026-10-27T15:00:00Z','2026-10-27T16:00:00Z','rm','rm','{Now}');
            INSERT INTO CabAgendaItems(Id,MeetingId,ChangeRequestId,SequenceOrder) VALUES('a1','m1','c2',1);");
        return db;
    }

    private static string Decision(string id, string change, string decision, string by, bool retro = false, string? meeting = null) =>
        $"INSERT INTO CabDecisions(Id,ChangeRequestId,MeetingId,Decision,Retroactive,DecidedByUserId,DecidedAt) VALUES('{id}','{change}',{(meeting is null ? "NULL" : $"'{meeting}'")},'{decision}',{(retro ? 1 : 0)},'{by}','{Now}')";

    private static string Move(string change, string to, string decider, string lastEditor) =>
        $"UPDATE ChangeRequests SET Status='{to}', DecidedByUserId='{decider}', DecidedAt='{Now}', LastChangedByUserId='{lastEditor}', Version=Version+1 WHERE Id='{change}'";

    [Fact]
    public void Rule_1_a_requester_cannot_record_an_approval_or_a_rejection_of_their_own_change_by_direct_write()
    {
        using var db = Db();
        Blocked(db, Decision("d1", "c1", "Approved", "rte"), CabRuleText.SelfApproval, "approval");
        Blocked(db, Decision("d1", "c1", "Rejected", "rte"), CabRuleText.SelfApproval, "rejection");
        Allowed(db, Decision("d1", "c1", "Deferred", "rte"), "deferral is not a verdict");
        Allowed(db, Decision("d2", "c1", "Approved", "gov1"), "a second person");
    }

    [Fact]
    public void Rule_1_a_requester_cannot_move_their_own_change_to_Approved_or_Rejected_by_direct_write()
    {
        using var db = Db();
        Allowed(db, Decision("d1", "c1", "Approved", "gov1"), "a second person approves");
        Blocked(db, Move("c1", "Approved", "rte", "rte"), CabRuleText.SelfApproval, "the requester named as decider");
        Blocked(db, Move("c1", "Approved", "gov1", "rte"), CabRuleText.SelfApproval, "the requester as the one who made the write");
        Blocked(db, Move("c1", "Rejected", "rte", "rte"), CabRuleText.SelfApproval, "rejection");
        Allowed(db, Move("c1", "Approved", "gov1", "gov1"), "the second person's move");
        Assert.Equal("Approved", Scalar(db, "SELECT Status FROM ChangeRequests WHERE Id='c1'"));
    }

    [Fact]
    public void A_status_cannot_jump_a_legal_move_or_skip_a_decision_on_record()
    {
        using var db = Db();
        Blocked(db, Move("c1", "Approved", "gov1", "gov1"), "recorded CAB decision", "approved with no decision");
        Blocked(db, "UPDATE ChangeRequests SET Status='Closed' WHERE Id='c1'", "Illegal change request status move", "skipping to Closed");
        Blocked(db, "UPDATE ChangeRequests SET Status='Implemented' WHERE Id='c1'", "Only an Emergency change", "a Normal change implemented before approval");
        Allowed(db, "UPDATE ChangeRequests SET Status='Implemented' WHERE Id='e1'", "an Emergency change implemented before approval");
    }

    [Fact]
    public void Rule_2_the_chair_who_raised_a_change_cannot_be_its_only_approver_by_direct_write()
    {
        using var db = Db();
        Allowed(db, Decision("d1", "c2", "Approved", "rm", meeting: "m1"), "the chair's approval is recorded");
        Blocked(db, Move("c2", "Approved", "rm", "rm"), CabRuleText.ChairSoleApprover, "the chair as the only approver");
        Allowed(db, Decision("d2", "c2", "Approved", "gov1"), "a second approver is recorded");
        Allowed(db, Move("c2", "Approved", "rm", "rm"), "the chair's approval counts with a second approver");
    }

    [Fact]
    public void Rule_2_recording_without_the_meeting_does_not_dodge_it_while_the_change_is_on_the_chairs_agenda()
    {
        using var db = Db();
        Allowed(db, Decision("d1", "c2", "Approved", "rm"), "recorded with no meeting");
        Blocked(db, Move("c2", "Approved", "rm", "rm"), CabRuleText.ChairSoleApprover, "still the chair");
    }

    [Fact]
    public void Rule_3_an_Emergency_change_cannot_be_closed_without_a_review_by_someone_other_than_the_requester()
    {
        using var db = Db();
        Allowed(db, "UPDATE ChangeRequests SET Status='Implemented' WHERE Id='e1'", "implemented first");
        Blocked(db, "UPDATE ChangeRequests SET Status='Closed' WHERE Id='e1'", CabRuleText.EmergencyNeedsReview, "no review at all");
        Blocked(db, Decision("r1", "e1", "Approved", "rte", retro: true), CabRuleText.SelfApproval, "the requester reviewing themselves");
        Blocked(db, "UPDATE ChangeRequests SET Status='Closed' WHERE Id='e1'", CabRuleText.EmergencyNeedsReview, "still no review");
        Allowed(db, Decision("r2", "e1", "Approved", "gov1", retro: true), "the second person reviews retroactively");
        Allowed(db, "UPDATE ChangeRequests SET Status='Closed' WHERE Id='e1'", "closed after the review");
    }

    [Fact]
    public void Decisions_are_immutable_a_retroactive_review_is_only_for_an_implemented_Emergency_change_and_a_change_is_never_deleted()
    {
        using var db = Db();
        Blocked(db, Decision("r1", "c1", "Approved", "gov1", retro: true), "only for an Emergency change that has been implemented", "retroactive on a Normal change");
        Blocked(db, Decision("r1", "e1", "Approved", "gov1", retro: true), "only for an Emergency change that has been implemented", "retroactive before implementation");
        Allowed(db, Decision("d1", "c1", "Approved", "gov1"), "a decision");
        Blocked(db, "UPDATE CabDecisions SET Decision='Rejected' WHERE Id='d1'", "CAB decisions are immutable", "edit");
        Blocked(db, "DELETE FROM CabDecisions WHERE Id='d1'", "CAB decisions are immutable", "delete");
        Blocked(db, "DELETE FROM ChangeRequests WHERE Id='c1'", "kept for the record", "delete a change");
        Allowed(db, "UPDATE ChangeRequests SET Status='Cancelled' WHERE Id='e1'", "cancel instead");
    }

    [Fact]
    public void A_decision_is_recorded_while_the_change_waits_for_the_CAB()
    {
        using var db = Db();
        Allowed(db, Move("c1", "Cancelled", "gov1", "gov1").Replace("DecidedByUserId='gov1', DecidedAt='" + Now + "', ", ""), "cancelled");
        Blocked(db, Decision("d1", "c1", "Approved", "gov1"), "waiting for the CAB", "a decision on a cancelled change");
    }

    private static string Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar())!;
    }
}
