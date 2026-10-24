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
                // RT2 (D48): the version's one-release-per-place index is a plain UNIQUE failure, which names the index and nothing else; say what it means (the service checks first, this covers a race).
                if (message.Contains("UX_Trains_VersionSlot", StringComparison.Ordinal)) message = "The version already has a release of this type (and Beta number); a version has one for each type";
                return true;
            }
            if (e.InnerException is null) break;
        }
        message = "";
        return false;
    }

    public static bool IsDbRule(DbUpdateException ex) => TryGetMessage(ex, out _);
}
