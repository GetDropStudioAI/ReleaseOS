"""Database layer for the schema oracle (REOS-195). test_schema.py runs every case through one of two backends:

  sqlite    in-process, unchanged: the contract (default).
  postgres  opt-in: `python3 tests/reference/test_schema.py --dialect postgres [--report]`.
            Needs psycopg2 (pip install -r tests/reference/requirements-postgres.txt) and a Postgres 16 server.
            DSN: environment variable REOS_PG_DSN (default postgresql://postgres:postgres@localhost:5432/reos_oracle).

The Postgres schema is GENERATED from db/schema.sql on every run by tools/gen_pg_schema.py; nothing is frozen.
Each fresh database is a fresh Postgres schema (search_path), so one server database serves the whole run.
The case SQL is written in SQLite syntax; the Postgres connection translates it with the same translator as the DDL
(quoting, IS DISTINCT FROM, GLOB, printf ...) and maps Postgres errors to the SQLite wording the cases expect
("CHECK constraint failed", "UNIQUE constraint failed", ...), the way DbRules will for the app.
"""
import argparse, atexit, functools, os, re, sqlite3, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "..")
sys.path.insert(0, os.path.join(ROOT, "tools"))

DEFAULT_DSN = "postgresql://postgres:postgres@localhost:5432/reos_oracle"


class Blocked(Exception):
    """The database refused the statement (a rule, CHECK, UNIQUE, FOREIGN KEY or NOT NULL)."""


class DialectError(Exception):
    """The statement failed for another reason (syntax, missing object, type error): never a legitimate refusal."""


def parse_args(argv):
    ap = argparse.ArgumentParser(description="Schema oracle (db/schema.sql). Default: SQLite, unchanged.")
    ap.add_argument("--dialect", choices=["sqlite", "postgres"], default="sqlite")
    ap.add_argument("--report", action="store_true", help="per-case results grouped by trigger class; with postgres also writes the report file")
    ap.add_argument("--report-file", default=os.path.join(ROOT, "docs", "proposals", "postgres-oracle-report.md"))
    return ap.parse_args(argv)


# ---------------------------------------------------------------------------------------------------------------- SQLite
class SqliteBackend:
    name = "sqlite"
    blocked_errors = (sqlite3.IntegrityError,)
    other_errors = ()

    def connect(self, schema_path):
        db = sqlite3.connect(":memory:")
        db.execute("PRAGMA foreign_keys=ON")
        db.executescript(open(schema_path).read())
        return db


# -------------------------------------------------------------------------------------------------------------- Postgres
SQLITE_MASTER = ("(SELECT 'table' AS type, table_name::text AS name FROM information_schema.tables "
                 "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' "
                 "UNION ALL SELECT 'trigger', regexp_replace(tgname, '^t[0-9]+_', '') FROM pg_trigger "
                 "WHERE NOT tgisinternal AND tgrelid IN (SELECT oid FROM pg_class WHERE relnamespace = current_schema()::regnamespace)) sqlite_master")


class _NoRows:
    def fetchone(self): return None
    def fetchall(self): return []
    def __iter__(self): return iter(())


class PostgresBackend:
    name = "postgres"
    other_errors = ()

    def __init__(self):
        import psycopg2                                              # noqa: imported lazily so the SQLite run never needs it
        self.pg = psycopg2
        self.blocked_errors = (Blocked,)
        self.other_errors = (DialectError,)
        self.dsn = os.environ.get("REOS_PG_DSN", DEFAULT_DSN)
        self.gen = None
        self.load_errors = {}                                         # (kind, name) -> error text, from the first load
        self._schemas = []
        self._n = 0
        atexit.register(self.cleanup)

    def _generate(self):
        if self.gen is None:
            import gen_pg_schema
            self.gen = gen_pg_schema.generate()
            problems = gen_pg_schema.check(self.gen)
            if problems:
                raise RuntimeError("the Postgres schema cannot be produced: " + "; ".join(problems))
        return self.gen

    def connect(self, schema_path):
        gen = self._generate()
        conn = self.pg.connect(self.dsn)
        conn.autocommit = True
        cur = conn.cursor()
        self._n += 1
        schema = f"oracle_{os.getpid()}_{self._n}"
        self._schemas.append(schema)
        cur.execute(f'CREATE SCHEMA "{schema}"')
        first = not self.load_errors and self._n == 1
        for s in gen.statements:
            # the extension lives once in public; every other object lives in this run's schema
            cur.execute("SET search_path TO " + ("public" if s.kind == "extension" else f'"{schema}", public'))
            try:
                cur.execute(s.sql)
            except self.pg.Error as e:
                if first:
                    self.load_errors[(s.kind, s.name)] = (e.pgcode, (e.diag.message_primary or str(e)).strip())
        cur.execute(f'SET search_path TO "{schema}", public')
        return PgConnection(self, conn, gen.idents)

    def cleanup(self):
        try:
            conn = self.pg.connect(self.dsn)
            conn.autocommit = True
            cur = conn.cursor()
            for s in self._schemas:
                cur.execute(f'DROP SCHEMA IF EXISTS "{s}" CASCADE')
            conn.close()
        except Exception as e:                                        # never silent: say it, a leftover schema is harmless but visible
            print(f"warning: could not drop oracle schemas: {e}", file=sys.stderr)


class PgConnection:
    """The slice of the sqlite3 connection API the oracle uses: execute() and executescript()."""

    def __init__(self, backend, conn, idents):
        self.b, self.conn, self.idents = backend, conn, idents

    @functools.lru_cache(maxsize=None)
    def _translate(self, sql, has_params):
        import gen_pg_schema as g
        s = sql.strip()
        if re.match(r"PRAGMA\s+foreign_keys", s, re.I):
            return None
        m = re.match(r"PRAGMA\s+table_info\((\w+)\)\s*;?$", s, re.I)
        if m:
            return ("SELECT ordinal_position - 1, column_name::text FROM information_schema.columns "
                    f"WHERE table_schema = current_schema() AND table_name = '{m.group(1)}' ORDER BY ordinal_position")
        s = re.sub(r"\bsqlite_master\b", SQLITE_MASTER, s)
        s = g.translate_sql(s, self.idents, "oracle case")
        out = []
        for kind, text in g.tokenize(s):
            if kind == "punct" and text == "?":
                out.append("%s")
            elif kind == "str" and has_params:
                out.append(text.replace("%", "%%"))
            elif kind == "punct" and text == "%" and has_params:
                out.append("%%")
            else:
                out.append(text)
        return "".join(out)

    def execute(self, sql, params=()):
        pg_sql = self._translate(sql, bool(params))
        if pg_sql is None:
            return _NoRows()
        cur = self.conn.cursor()
        try:
            cur.execute(pg_sql, tuple(params) if params else None)
        except self.b.pg.Error as e:
            raise self._map(e) from None
        return cur

    def executescript(self, sql):
        self.execute(sql)

    @staticmethod
    def _map(e):
        code = getattr(e, "pgcode", None) or ""
        msg = (e.diag.message_primary or str(e)).strip()
        d = e.diag
        if code == "RE001":
            return Blocked(msg)
        if code == "23514":
            return Blocked(f"CHECK constraint failed: {d.constraint_name}")
        if code == "23505":
            return Blocked(f"UNIQUE constraint failed: {d.table_name}: {d.constraint_name}")
        if code == "23503":
            return Blocked(f"FOREIGN KEY constraint failed: {d.constraint_name}")
        if code == "23502":
            return Blocked(f"NOT NULL constraint failed: {d.table_name}.{d.column_name}")
        return DialectError(f"{code} {msg}")


def backend(name):
    if name == "sqlite":
        return SqliteBackend()
    return PostgresBackend()
