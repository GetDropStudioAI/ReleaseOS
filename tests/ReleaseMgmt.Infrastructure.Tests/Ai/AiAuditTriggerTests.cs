using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>
/// REOS-184 (D58): the database rules behind the AI audit trail, proved with direct SQL on the migrated database (no service in the way). The complete oracle is tests/reference/test_schema.py;
/// these are the rules a story acceptance leans on: the record never changes, the decision is added once, a decided or refused action never changes, nothing is deleted, the workspace boundary.
/// </summary>
public sealed class AiAuditTriggerTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Now = TriggerSuiteFixture.Now;
    private static string H(char c) => new(c, 64);

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
            INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('out','out@x.com','Outsider','Viewer');
            INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Now}');
            INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','out','Viewer','{Now}');
            INSERT INTO AiActions(Id,Feature,Provider,Model,PromptTemplateVersion,PromptHash,InputHash,Outcome,OutputText,OutputHash,RequestedByUserId,RequestedAt)
              VALUES('a1','release-notes-draft','fake','m','release-notes/1','{H('b')}','{H('d')}','Completed','Draft','{H('a')}','dev','{Now}');
            INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,Outcome,Detail,RequestedByUserId,RequestedAt,Decision)
              VALUES('r1','risk-score','fake','untracked','NoKey','No provider key is stored','dev','{Now}','NotApplicable');");
        return db;
    }

    [Fact]
    public void The_record_never_changes_only_the_decision_is_added_once()
    {
        using var db = Db();
        Blocked(db, "UPDATE AiActions SET OutputText='Better' WHERE Id='a1'", "record does not change", "output");
        Blocked(db, "UPDATE AiActions SET Model='other' WHERE Id='a1'", "record does not change", "model");
        Blocked(db, "UPDATE AiActions SET RequestedByUserId='rm' WHERE Id='a1'", "record does not change", "requester");
        Blocked(db, $"UPDATE AiActions SET InputHash='{H('f')}' WHERE Id='a1'", "record does not change", "input hash");
        Blocked(db, "UPDATE AiActions SET Decision='Edited', DecidedByUserId='rm', DecidedAt=? WHERE Id='a1'", "CHECK", "an edit without text", Now);
        Allowed(db, $"UPDATE AiActions SET Decision='Edited', FinalText='Edited', FinalHash='{H('e')}', DecidedByUserId='rm', DecidedAt='{Now}', Version=2 WHERE Id='a1'", "the human edit");
        Assert.Equal(("Draft", "Edited"), (Scalar(db, "SELECT OutputText FROM AiActions WHERE Id='a1'"), Scalar(db, "SELECT FinalText FROM AiActions WHERE Id='a1'")));   // both are kept
        Blocked(db, "UPDATE AiActions SET Decision='Accepted', FinalText=NULL, FinalHash=NULL WHERE Id='a1'", "decision on an AI action is final", "a second decision");
        Blocked(db, "UPDATE AiActions SET OutputText='x' WHERE Id='a1'", "decision on an AI action is final", "any change after the decision");
    }

    [Fact]
    public void A_refused_call_has_nothing_to_decide_and_never_changes()
    {
        using var db = Db();
        Blocked(db, "UPDATE AiActions SET Decision='Accepted', DecidedByUserId='rm', DecidedAt=? WHERE Id='r1'", "final", "deciding a refusal", Now);
        Blocked(db, "UPDATE AiActions SET Detail='x' WHERE Id='r1'", "final", "editing a refusal");
    }

    [Fact]
    public void An_action_is_never_deleted()
    {
        using var db = Db();
        Blocked(db, "DELETE FROM AiActions WHERE Id='a1'", "kept for the record", "pending");
        Blocked(db, "DELETE FROM AiActions WHERE Id='r1'", "kept for the record", "refused");
    }

    [Fact]
    public void The_workspace_boundary_holds_for_the_requester_and_the_decider()
    {
        using var db = Db();
        Blocked(db, $"INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,Outcome,RequestedByUserId,RequestedAt,Decision) VALUES('x','risk-score','fake','u','NoKey','out','{Now}','NotApplicable')", "person who asked is not a member", "requester");
        Blocked(db, "UPDATE AiActions SET WorkspaceId='w2' WHERE Id='a1'", "cannot move to another workspace", "move");
        Blocked(db, "UPDATE AiActions SET Decision='Accepted', DecidedByUserId='out', DecidedAt=? WHERE Id='a1'", "person who decided is not a member", "decider", Now);
    }

    [Fact]
    public void A_model_call_needs_an_input_hash_a_draft_needs_its_hash_and_the_decision_columns_agree()
    {
        using var db = Db();
        Blocked(db, $"INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,Outcome,OutputText,OutputHash,RequestedAt) VALUES('x','risk-score','fake','u','Completed','t','{H('a')}','{Now}')", "CHECK", "a call with no input hash");
        Blocked(db, $"INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,InputHash,Outcome,OutputText,RequestedAt) VALUES('x','risk-score','fake','u','{H('d')}','Completed','t','{Now}')", "CHECK", "output with no hash");
        Blocked(db, $"INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,InputHash,Outcome,OutputText,OutputHash,RequestedAt,Decision) VALUES('x','risk-score','fake','u','{H('d')}','Completed','t','{H('a')}','{Now}','NotApplicable')", "CHECK", "a draft marked not applicable");
        Blocked(db, $"INSERT INTO AiActions(Id,Feature,Provider,PromptTemplateVersion,Outcome,OutputText,OutputHash,RequestedAt,Decision) VALUES('x','risk-score','fake','u','Blocked','leak','{H('a')}','{Now}','NotApplicable')", "CHECK", "a refusal carrying output");
    }

    private static string? Scalar(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar() as string; }
}
