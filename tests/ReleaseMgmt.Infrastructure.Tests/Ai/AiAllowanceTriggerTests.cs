using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>
/// REOS-185 (D57, D58): the database rules behind the AI allowance, proved with direct SQL on the migrated database (no service in the way). The complete oracle is tests/reference/test_schema.py; these are the
/// rules the acceptance leans on: a reservation cannot take a month over its allowance, chat is capped first, usage never falls, the ledger is never rewritten or deleted, the workspace boundary.
/// </summary>
public sealed class AiAllowanceTriggerTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
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
            INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('out','out@x.com','Outsider','Viewer');
            INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Now}');
            INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','out','Viewer','{Now}');
            INSERT INTO AiAllowancePeriods(Id,WorkspaceId,PeriodStart,PeriodEnd,IncludedMicroUsd,SoftCapPercent,ChatSharePercent,OpenedAt) VALUES('p1','default','2026-10-01','2026-11-01',20000,80,50,'{Now}');
            INSERT INTO AiAllowancePeriods(Id,WorkspaceId,PeriodStart,PeriodEnd,IncludedMicroUsd,SoftCapPercent,ChatSharePercent,OpenedAt) VALUES('p2','w2','2026-10-01','2026-11-01',20000,80,50,'{Now}');
            INSERT INTO AiUsageLedger(Id,WorkspaceId,PeriodId,Feature,Class,Model,RequestedByUserId,State,ReservedMicroUsd,ReservedAt) VALUES('l1','default','p1','risk-score','Standard','m','dev','Reserved',1902,'{Now}');");
        return db;
    }

    [Fact]
    public void A_reservation_cannot_take_a_month_over_its_allowance_and_chat_is_capped_first()
    {
        using var db = Db();
        Blocked(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=20001 WHERE Id='p1'", "allowance for this period is used up", "over the allowance");
        Allowed(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=20000 WHERE Id='p1'", "exactly the allowance");
        Allowed(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=0, UsedMicroUsd=21000 WHERE Id='p1'", "a settlement above its estimate is recorded, not refused");
        Blocked(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=1 WHERE Id='p1'", "allowance for this period is used up", "any reservation once over");
        Blocked(db, "UPDATE AiAllowancePeriods SET ChatUsedMicroUsd=1, UsedMicroUsd=0 WHERE Id='p1'", "never go down", "usage turned back down");
        Allowed(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=10000, ChatReservedMicroUsd=10000 WHERE Id='p2'", "chat inside its 50% share");
        Blocked(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=10001, ChatReservedMicroUsd=10001 WHERE Id='p2'", "Chat is capped first", "chat over its share");
        Allowed(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=19000, ChatReservedMicroUsd=0 WHERE Id='p2'", "a draft is not held to chat's share");
        Blocked(db, "UPDATE AiAllowancePeriods SET ReservedMicroUsd=19001, ChatReservedMicroUsd=1 WHERE Id='p2'", "Chat is capped first", "chat past the 80% soft cap");
    }

    [Fact]
    public void The_allowance_of_a_month_is_fixed_and_periods_are_never_deleted_or_moved()
    {
        using var db = Db();
        Blocked(db, "UPDATE AiAllowancePeriods SET IncludedMicroUsd=1 WHERE Id='p1'", "fixed when the period opens", "included");
        Blocked(db, "UPDATE AiAllowancePeriods SET SoftCapPercent=100 WHERE Id='p1'", "fixed when the period opens", "soft cap");
        Blocked(db, "UPDATE AiAllowancePeriods SET WorkspaceId='w2' WHERE Id='p1'", "cannot move to another workspace", "moved");
        Blocked(db, "DELETE FROM AiAllowancePeriods WHERE Id='p1'", "kept for the record", "deleted");
        Blocked(db, $"INSERT INTO AiAllowancePeriods(Id,WorkspaceId,PeriodStart,PeriodEnd,IncludedMicroUsd,SoftCapPercent,ChatSharePercent,OpenedAt) VALUES('p9','default','2026-10-01','2026-11-01',1,80,50,'{Now}')", "UNIQUE", "second period for the month");
        Allowed(db, "UPDATE AiAllowancePeriods SET PackMicroUsd=5000 WHERE Id='p1'", "a credit pack adds to a month");
        Blocked(db, "UPDATE AiAllowancePeriods SET PackMicroUsd=4000 WHERE Id='p1'", "never go down", "a pack taken away");
    }

    [Fact]
    public void The_ledger_is_settled_once_never_rewritten_never_deleted_and_stays_in_its_workspace()
    {
        using var db = Db();
        TriggerSuiteFixture.Run(db, $@"INSERT INTO AiActions(Id,Feature,Provider,Model,PromptTemplateVersion,InputHash,Outcome,OutputText,OutputHash,RequestedByUserId,RequestedAt)
            VALUES('a1','risk-score','fake','m','untracked','{new string('d', 64)}','Completed','x','{new string('a', 64)}','dev','{Now}')");
        Blocked(db, "UPDATE AiUsageLedger SET Feature='chat' WHERE Id='l1'", "does not change once written", "relabelled");
        Blocked(db, "UPDATE AiUsageLedger SET ReservedMicroUsd=1 WHERE Id='l1'", "does not change once written", "amount rewritten");
        Blocked(db, "UPDATE AiUsageLedger SET WorkspaceId='w2' WHERE Id='l1'", "cannot move to another workspace", "moved");
        Blocked(db, "UPDATE AiUsageLedger SET State='Settled', CostMicroUsd=5, SettledAt=? WHERE Id='l1'", "CHECK", "settled with no audit-trail record", Now);
        Allowed(db, "UPDATE AiUsageLedger SET State='Settled', CostMicroUsd=2000, InputTokens=100, OutputTokens=180, AiActionId='a1', SettledAt=?, Version=2 WHERE Id='l1'", "the call settles", Now);
        Blocked(db, "UPDATE AiUsageLedger SET CostMicroUsd=1 WHERE Id='l1'", "settled AI usage entry is final", "a settled cost edited");
        Blocked(db, "UPDATE AiUsageLedger SET State='Released' WHERE Id='l1'", "settled AI usage entry is final", "a settled entry released");
        Blocked(db, "DELETE FROM AiUsageLedger WHERE Id='l1'", "kept for the record", "deleted");
        Blocked(db, $"INSERT INTO AiUsageLedger(Id,WorkspaceId,PeriodId,Feature,Class,State,ReservedMicroUsd,ReservedAt) VALUES('l9','default','p2','risk-score','Standard','Reserved',1,'{Now}')", "belongs to another workspace", "another workspace's period");
        Blocked(db, $"INSERT INTO AiUsageLedger(Id,WorkspaceId,PeriodId,Feature,Class,RequestedByUserId,State,ReservedMicroUsd,ReservedAt) VALUES('l9','default','p1','risk-score','Standard','out','Reserved',1,'{Now}')", "person who asked is not a member", "a requester from another workspace");
    }

    [Fact]
    public void A_credit_pack_is_never_edited_or_deleted_and_one_billing_purchase_is_one_grant()
    {
        using var db = Db();
        Allowed(db, $"INSERT INTO AiCreditPacks(Id,WorkspaceId,PeriodId,MicroUsd,Source,Reference,GrantedAt) VALUES('k1','default','p1',50000,'Billing','inv-1','{Now}')", "a billed pack");
        Blocked(db, $"INSERT INTO AiCreditPacks(Id,WorkspaceId,PeriodId,MicroUsd,Source,Reference,GrantedAt) VALUES('k2','default','p1',50000,'Billing','inv-1','{Now}')", "UNIQUE", "the same purchase twice");
        Blocked(db, "UPDATE AiCreditPacks SET MicroUsd=1 WHERE Id='k1'", "never changes once granted", "edited");
        Blocked(db, "DELETE FROM AiCreditPacks WHERE Id='k1'", "kept for the record", "deleted");
        Blocked(db, $"INSERT INTO AiCreditPacks(Id,WorkspaceId,PeriodId,MicroUsd,Source,GrantedAt) VALUES('k3','default','p2',1,'Manual','{Now}')", "belongs to another workspace", "another workspace's period");
        Blocked(db, $"INSERT INTO AiCreditPacks(Id,WorkspaceId,PeriodId,MicroUsd,Source,GrantedByUserId,GrantedAt) VALUES('k3','default','p1',1,'Manual','out','{Now}')", "not a member of this workspace", "a granter from another workspace");
    }
}
