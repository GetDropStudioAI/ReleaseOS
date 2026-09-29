using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Trigger RAISE(ABORT) and constraint failures surface as SqliteException (code 19). Callers map them to 422 DbRule.</summary>
public static class DbRules
{
    public static bool TryGetMessage(Exception ex, out string message)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is SqliteException { SqliteErrorCode: 19 } s)
            {
                // "SQLite Error 19: 'Gate lockout: …'." -> the rule text
                var m = s.Message;
                var a = m.IndexOf('\'');
                var b = m.LastIndexOf('\'');
                message = a >= 0 && b > a ? m[(a + 1)..b] : m;
                return true;
            }
            if (e.InnerException is null) break;
        }
        message = "";
        return false;
    }

    public static bool IsDbRule(DbUpdateException ex) => TryGetMessage(ex, out _);
}
