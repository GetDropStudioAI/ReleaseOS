"""Runner for `test_schema.py --dialect postgres` and `--report` (REOS-195).

test_schema.py is a flat script. This runner executes it ONE TOP-LEVEL STATEMENT AT A TIME so that a case (or its setup)
that fails on Postgres is recorded and the run goes on; the SQLite default path never uses this file.
Exit code: 0 when the run completed (failing cases are the porting backlog, not an error), 2 when the run could not be
done at all (no psycopg2, no server, the Postgres schema cannot be produced).
"""
import ast, collections, os, sys, traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools"))

CONSTRAINT_WORDS = ("CHECK", "UNIQUE", "FOREIGN KEY", "NOT NULL", "PRIMARY KEY")


def run(script, opts):
    src = open(script, encoding="utf-8").read()
    tree = ast.parse(src)
    ns = {"__name__": "__oracle__", "__file__": script}
    sys.argv = [script] + (["--dialect", opts.dialect])        # the module re-parses argv
    uncaught = []
    try:
        for node in tree.body:
            if isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) and getattr(node.value.func, "attr", "") == "exit":
                break                                           # the script's closing sys.exit(...)
            if (isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) and getattr(node.value.func, "id", "") == "print"
                    and node.value.args and isinstance(node.value.args[0], ast.Constant) and isinstance(node.value.args[0].value, str)):
                ns["SECTION"] = node.value.args[0].value.strip()
            try:
                exec(compile(ast.Module([node], []), script, "exec"), ns)
            except Exception as e:                              # a statement that fails on this dialect: record it, carry on
                if isinstance(e, ModuleNotFoundError) or "connection" in type(e).__name__.lower() or type(e).__name__ == "OperationalError":
                    raise
                uncaught.append((node.lineno, ns.get("SECTION", ""), f"{type(e).__name__}: {e}"))
                print(f"  ERROR line {node.lineno}: {type(e).__name__}: {e}")
    except ModuleNotFoundError as e:
        print(f"cannot run the {opts.dialect} oracle: {e}. Install tests/reference/requirements-postgres.txt", file=sys.stderr)
        return 2
    except Exception as e:
        print(f"cannot run the {opts.dialect} oracle: {type(e).__name__}: {e}", file=sys.stderr)
        traceback.print_exc()
        return 2
    results = ns.get("RESULTS", [])
    passed = sum(1 for r in results if r[2])
    failed = len(results) - passed
    print(f"\n{passed} passed, {failed} failed" + (f", {len(uncaught)} statements raised outside a case" if uncaught else ""))
    backend = ns["DIALECT"]
    if opts.report:
        if opts.dialect == "postgres":
            text = build_report(opts.dialect, results, uncaught, backend)
            open(opts.report_file, "w", encoding="utf-8").write(text)
            print(f"report written to {os.path.relpath(opts.report_file)}")
        print_summary(opts.dialect, results, backend)
        return 0
    return 1 if (failed or uncaught) else 0


# ------------------------------------------------------------------------------------------------------------ grouping
def trigger_index():
    """message -> set of trigger classes, from the inventory's heuristic (tools/schema_inventory.py)."""
    import schema_inventory as inv
    _, _, triggers, _ = inv.build()
    idx = collections.defaultdict(list)
    for t in triggers:
        for m in t["messages"]:
            idx[m].append(t)
    return idx


def case_class(r, idx):
    section, label, ok, msg, expect, should_pass = r
    if should_pass is None:
        return "assertion (rows written by cascades, logs, counts)"
    if should_pass:
        return "allowed (a legal statement must go through)"
    if expect is None:
        return "blocked, message not pinned"
    if any(w in expect for w in CONSTRAINT_WORDS) and not any(expect in m for m in idx):
        return "constraint (CHECK / UNIQUE / FOREIGN KEY / NOT NULL)"
    classes = collections.Counter(t["class"] for m, ts in idx.items() if expect in m for t in ts)
    if not classes:
        return "constraint (CHECK / UNIQUE / FOREIGN KEY / NOT NULL)" if any(w in expect for w in CONSTRAINT_WORDS) else "blocked, message not in any trigger"
    top = classes.most_common()
    return top[0][0] if len(top) == 1 else top[0][0] + " (message shared with other classes)"


def grouped(results):
    idx = trigger_index()
    g = collections.OrderedDict()
    for n, r in enumerate(results, 1):
        g.setdefault(case_class(r, idx), []).append((n, r))
    return g


def print_summary(dialect, results, backend):
    print(f"\nBy class ({dialect}):")
    for cls, rows in sorted(grouped(results).items()):
        ok = sum(1 for _, r in rows if r[2])
        print(f"  {cls:<70} {ok:>4}/{len(rows):<4} passed")
    errs = getattr(backend, "load_errors", {})
    if errs:
        print(f"\n{len(errs)} generated statements failed to load on {dialect} (see the report)")


def md_escape(s, n=170):
    s = " ".join(str(s).split()).replace("|", "\\|")
    return s if len(s) <= n else s[: n - 1] + "…"


def build_report(dialect, results, uncaught, backend):
    import gen_pg_schema
    gen = backend.gen
    g = grouped(results)
    passed = sum(1 for r in results if r[2])
    failed = len(results) - passed
    out = []
    w = out.append
    w("# Postgres oracle report (REOS-195)")
    w("")
    w("Generated by `python3 tests/reference/test_schema.py --dialect postgres --report`. **Do not edit by hand.** "
      "The Postgres schema is generated from `db/schema.sql` at run time by `tools/gen_pg_schema.py` (a first-pass translator, "
      "plan: `docs/proposals/postgres-port-plan.md` sections 2 and 3). The report is the porting backlog: SQLite is the specification, "
      "every case runs unchanged except for the mechanical SQL translation in `tests/reference/dialect.py`.")
    w("")
    w(f"**Result: {passed} of {len(results)} cases pass on Postgres, {failed} fail.** SQLite runs the same cases at 100%.")
    w("")
    w("| Fact | Value |")
    w("|---|---|")
    w(f"| Server | {backend_version(backend)} |")
    w(f"| Source schema | {gen.source_counts['tables']} tables, {gen.source_counts['indexes']} indexes, {gen.source_counts['triggers']} triggers |")
    w(f"| Generated | {gen.counts['tables']} tables, {gen.counts['foreign keys']} foreign keys, {gen.counts['indexes']} indexes, {gen.counts['triggers']} triggers (one PL/pgSQL function each) |")
    w(f"| Generated statements that failed to load | {len(backend.load_errors)} |")
    w(f"| `TODO(pg)` markers (constructs the generator cannot translate yet) | {len(gen.todos.items)} |")
    w(f"| Statements that raised outside a case | {len(uncaught)} |")
    w("")
    w("## What a green run does and does not prove")
    w("")
    w("- It proves that, for the 539 cases, the generated Postgres schema accepts and refuses the same statements with the same message, and that the cascades write the same rows. "
      "The cases are SQLite's specification (plan 3.3, layer 1).")
    w("- It does **not** prove the same-event firing order for all 117 trigger pairs (plan 3.3 layer 2), the order manifest (layer 3), concurrency (4.8), "
      "or TRUNCATE. The cases pin the order only where one statement breaks two rules. "
      "Check this when reading the number: reversing the generator's trigger numbering (so the oldest rule fires first) fails 7 cases; ignoring every `WHEN` fails 206.")
    w("- The translator is first pass. Constructs it does not know are listed in section 3 and fail visibly instead of being skipped.")
    w("")
    w("## 1. Results by trigger class")
    w("")
    w("Class comes from the case's expected message looked up in the trigger inventory (`tools/schema_inventory.py` classes); "
      "cases that pin no trigger message are grouped by kind. Heuristic, like the inventory's own classes.")
    w("")
    w("| Class | Cases | Pass | Fail |")
    w("|---|---:|---:|---:|")
    for cls, rows in sorted(g.items()):
        ok = sum(1 for _, r in rows if r[2])
        w(f"| {cls} | {len(rows)} | {ok} | {len(rows) - ok} |")
    w("")
    w("## 2. Generated statements that failed to load")
    w("")
    if backend.load_errors:
        w("Each is one object the generator produced and Postgres rejected. Cases that depend on it fail until it loads.")
        w("")
        w("| Kind | Object | SQLSTATE | Error |")
        w("|---|---|---|---|")
        for (kind, name), (code, msg) in sorted(backend.load_errors.items()):
            w(f"| {kind} | `{name}` | {code} | {md_escape(msg)} |")
    else:
        w("None: every generated statement loaded.")
    w("")
    w("## 3. Constructs the generator cannot translate yet (`TODO(pg)`)")
    w("")
    if gen.todos.items:
        w("| Where | Construct | Detail |")
        w("|---|---|---|")
        for where, construct, detail in gen.todos.items:
            w(f"| {md_escape(where)} | {md_escape(construct)} | {md_escape(detail)} |")
    else:
        w("None.")
    w("")
    w("## 4. Failing cases (the porting backlog)")
    w("")
    w("Case numbers are the order in `test_schema.py`. The error text is what Postgres reported (`blocked:` messages are mapped to the SQLite wording the case expects).")
    for cls, rows in sorted(g.items()):
        bad = [(n, r) for n, r in rows if not r[2]]
        if not bad:
            continue
        w("")
        w(f"### {cls} ({len(bad)} failing of {len(rows)})")
        w("")
        w("| # | Section | Case | Expected | Postgres said |")
        w("|---:|---|---|---|---|")
        for n, (section, label, ok, msg, expect, should_pass) in bad:
            exp = "allowed" if should_pass else (f"blocked, `{expect}`" if expect else ("blocked" if should_pass is False else "assertion true"))
            w(f"| {n} | {md_escape(section, 40)} | {md_escape(label, 90)} | {md_escape(exp, 50)} | {md_escape(msg)} |")
    if uncaught:
        w("")
        w("## 5. Statements that raised outside a case")
        w("")
        w("A top-level statement of the script (setup, a query feeding an assertion) raised on Postgres. Cases after it may fail as a result.")
        w("")
        w("| Line | Section | Error |")
        w("|---:|---|---|")
        for line, section, err in uncaught:
            w(f"| {line} | {md_escape(section, 40)} | {md_escape(err)} |")
    w("")
    return "\n".join(out) + "\n"


def backend_version(backend):
    try:
        conn = backend.pg.connect(backend.dsn)
        cur = conn.cursor()
        cur.execute("SHOW server_version")
        v = cur.fetchone()[0]
        conn.close()
        return f"PostgreSQL {v}"
    except Exception:
        return "PostgreSQL (version unknown)"
