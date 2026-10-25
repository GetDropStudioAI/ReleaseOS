using Microsoft.Data.Sqlite;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-117 (RT2): a database rule that is a plain UNIQUE failure rather than a RAISE reads in words (CLAUDE.md rule 1: never a 500, never a bare index name).</summary>
public class DbRulesTests
{
    [Fact]
    public void The_version_slot_index_reads_as_a_sentence_when_a_race_gets_past_the_service()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "CREATE TABLE T(V TEXT, Y TEXT); CREATE UNIQUE INDEX UX_Trains_VersionSlot ON T(V, COALESCE(Y, '')); INSERT INTO T VALUES('v','t');";
            c.ExecuteNonQuery();
        }
        var ex = Assert.Throws<SqliteException>(() =>
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO T VALUES('v','t')";
            c.ExecuteNonQuery();
        });
        Assert.True(DbRules.TryGetMessage(ex, out var message));
        Assert.StartsWith("The version already has a release of this type", message);
    }

    [Fact]
    public void A_trigger_message_is_still_the_rule_text()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "CREATE TABLE T(V TEXT); CREATE TRIGGER trg BEFORE INSERT ON T BEGIN SELECT RAISE(ABORT, 'A release in a version needs a release type'); END;";
            c.ExecuteNonQuery();
        }
        var ex = Assert.Throws<SqliteException>(() =>
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT INTO T VALUES('v')";
            c.ExecuteNonQuery();
        });
        Assert.True(DbRules.TryGetMessage(ex, out var message));
        Assert.Equal("A release in a version needs a release type", message);
    }
}
