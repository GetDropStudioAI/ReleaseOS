#!/usr/bin/env python3
"""Inventory of db/schema.sql for the Postgres port plan (REOS-194, D59).

Parses db/schema.sql (text only; no database is opened for the parse) and prints, as Markdown:
  1. the table list (name, column count, CHECK count, FK count, triggers on it)
  2. the index list (name, table, unique, partial predicate)
  3. every trigger: fire order within its (table, timing, event), name, table, timing, event, class, rule text, notes
  4. same-event groups that hold more than one trigger, in SQLite fire order (newest-first = reverse file order)

Run:   python3 tools/schema_inventory.py                 # Markdown to stdout
       python3 tools/schema_inventory.py --check         # asserts 72 / 152 / 41 and prints the totals only
       python3 tools/schema_inventory.py --json out.json # machine-readable, for the dual-dialect oracle
       python3 tools/schema_inventory.py --embed docs/proposals/postgres-port-plan.md        # rewrite the block between the inventory markers
       python3 tools/schema_inventory.py --check-embed docs/proposals/postgres-port-plan.md  # exit 1 if that block is stale

The classification is a HEURISTIC over the trigger text (see classify()), with an explicit override table for the
few triggers where text alone misleads. Every override is listed in OVERRIDES with its reason. A reviewer should read
the "class" column as a first pass, not as proof. The counts (72 tables, 152 triggers, 41 indexes) are asserted.
"""
import argparse, collections, json, os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
SCHEMA = os.path.join(ROOT, "db", "schema.sql")
EXPECT = {"tables": 72, "triggers": 152, "indexes": 41}

# Classes (CLAUDE.md rule 1 and the REOS-194 brief):
C_CHECK = "pure-check"          # CHECK-like validation: row-local, cross-row/tenant consistency, immutability, role/SoD lookups
C_STATUS = "status-machine"     # guards a status/state/decision transition or reads another row's status
C_CASCADE = "cascade-writes"    # AFTER trigger that writes rows (audit rows, decertify, evidence lock, baseline, PIR, key assign)
C_TIME = "time-dependent"       # compares against a clock ('now' fallback or the service clock column)
C_SQLITE = "sqlite-specific"    # relies on a SQLite function/semantic with no direct Postgres twin (GLOB, randomblob, json_* ...)

# name -> (class, reason). Only where the text heuristic is wrong or ambiguous.
OVERRIDES = {
    "trg_Step_FreezeLockout": (C_SQLITE, "uses GLOB for ProductPattern; it is also a status guard (Status -> Running); GLOB is case-sensitive, Postgres LIKE/SIMILAR TO are not the same"),
    "trg_Run_LiveRequiresExecuting": (C_STATUS, "INSERT guard that reads the train's CurrentStatus"),
    "trg_Condition_RequiresConditionalGo": (C_STATUS, "INSERT guard that reads the decision's Decision value"),
    "trg_Import_NoCommitWithErrors": (C_STATUS, "guards ImportJobs.Status -> Committed"),
    "trg_ApprovalDecision_ApprovalOpen": (C_STATUS, "reads Approvals.Status: a decision cannot change once the approval is closed"),
    "trg_ApprovalDecision_Eligible": (C_CHECK, "role / separation-of-duties lookup on who may decide (fires on Decision change but checks the person, not a status path)"),
    "trg_ApprovalDecision_Separation": (C_CHECK, "role / separation-of-duties lookup on who may decide"),
    "trg_Template_ApproverNotAuthor_Insert": (C_CHECK, "role / separation-of-duties lookup: approver is not the template author"),
    "trg_Template_ApproverNotAuthor_Update": (C_CHECK, "role / separation-of-duties lookup: approver is not the template author"),
    "trg_Train_AssignKey": (C_CASCADE, "AFTER INSERT writes Workspaces.NextKeyNumber and the train's own ReleaseKey; no audit row"),
}

STATUS_COLS = ("Status", "CurrentStatus", "State", "Decision", "IsCompleted")


def strip_comments(sql):
    return re.sub(r"--[^\n]*", "", sql)


def split_statements(text):
    """Split into CREATE TABLE / CREATE INDEX / CREATE TRIGGER chunks, keeping source order and the comment block above each trigger."""
    out = []
    pos = 0
    pat = re.compile(r"(?m)^(CREATE TABLE|CREATE UNIQUE INDEX|CREATE INDEX|CREATE TRIGGER)\b")
    starts = [m.start() for m in pat.finditer(text)]
    for i, s in enumerate(starts):
        e = starts[i + 1] if i + 1 < len(starts) else len(text)
        out.append(text[s:e])
    return out


def parse_tables(chunks):
    tables = []
    for c in chunks:
        m = re.match(r"CREATE TABLE (\w+)\s*\(", c)
        if not m:
            continue
        body = strip_comments(c)
        # the table body ends at the first line that is exactly ");" at depth 0
        depth, end = 0, None
        for i, ch in enumerate(body):
            if ch == "(":
                depth += 1
            elif ch == ")":
                depth -= 1
                if depth == 0:
                    end = i
                    break
        inner = body[body.index("(") + 1:end]
        # split top-level commas
        parts, d, cur = [], 0, ""
        for ch in inner:
            if ch == "(":
                d += 1
            elif ch == ")":
                d -= 1
            if ch == "," and d == 0:
                parts.append(cur.strip()); cur = ""
            else:
                cur += ch
        if cur.strip():
            parts.append(cur.strip())
        cols = [p for p in parts if not re.match(r"(?i)(PRIMARY KEY|UNIQUE|FOREIGN KEY|CHECK|CONSTRAINT)\b", p)]
        tables.append({
            "name": m.group(1),
            "columns": len(cols),
            "checks": len(re.findall(r"(?i)\bCHECK\s*\(", inner)),
            "fks": len(re.findall(r"(?i)\bREFERENCES\b", inner)),
            "nocase": len(re.findall(r"COLLATE NOCASE", inner)),
            "autoinc": "AUTOINCREMENT" in inner,
        })
    return tables


def parse_indexes(chunks):
    out = []
    for c in chunks:
        m = re.match(r"CREATE (UNIQUE )?INDEX (\w+)\s+ON\s+(\w+)\s*\((.*?)\)(\s+WHERE\s+([^;]*))?;", strip_comments(c).strip(), re.S)
        if m:
            out.append({"name": m.group(2), "table": m.group(3), "unique": bool(m.group(1)), "columns": " ".join(m.group(4).split()),
                        "where": " ".join(m.group(6).split()) if m.group(6) else None})
    return out


def parse_triggers(chunks):
    trig = []
    hdr = re.compile(r"CREATE TRIGGER (\w+)\s+(BEFORE|AFTER|INSTEAD OF)\s+(INSERT|DELETE|UPDATE(?: OF [\w, ]+)?)\s+ON\s+(\w+)", re.S)
    for order, c in enumerate(x for x in chunks if x.startswith("CREATE TRIGGER")):
        h = hdr.match(c)
        if not h:
            raise SystemExit("unparsed trigger header: " + c[:80])
        body = strip_comments(c)
        # a trailing comment block belonging to the next statement is dropped by the next chunk start; the END; terminates this one
        body = body[: body.rindex("END;") + 4] if "END;" in body else body
        ev = h.group(3)
        upd_cols = [x.strip() for x in ev[len("UPDATE OF "):].split(",")] if ev.startswith("UPDATE OF") else None
        when = None
        wm = re.search(r"\bWHEN\b(.*?)\bBEGIN\b", body, re.S)
        if wm:
            when = " ".join(wm.group(1).split())
        inner = body.split("BEGIN", 1)[1].rsplit("END;", 1)[0] if "BEGIN" in body else ""
        msgs = [m.replace("''", "'") for m in re.findall(r"RAISE\(ABORT,\s*'((?:[^']|'')*)'", body)]
        writes = re.findall(r"(?m)^\s*(INSERT INTO|UPDATE|DELETE FROM)\s+(\w+)", inner)
        trig.append({
            "file_order": order + 1,
            "name": h.group(1),
            "timing": h.group(2),
            "event": ev.split(" OF")[0],
            "update_of": upd_cols,
            "table": h.group(4),
            "when": when,
            "messages": msgs,
            "writes": sorted({w[1] for w in writes}),
            "uses_now": "'now'" in body,
            "uses": sorted({f for f in ("GLOB", "randomblob", "json_object", "json_group_array", "strftime", "LIKE", "COLLATE") if f in body}),
            "text": body,
        })
    return trig


def classify(t):
    n = t["name"]
    if n in OVERRIDES:
        return OVERRIDES[n]
    body = t["text"]
    if t["timing"] == "AFTER" and t["writes"]:
        what = "writes " + ", ".join(t["writes"])
        if "AuditEvents" in t["writes"] and len(t["writes"]) == 1:
            what += " (audit row only)"
        return C_CASCADE, what
    if t["uses_now"]:
        return C_TIME, "compares to the service clock column, falling back to SQLite 'now'"
    if "GLOB" in body or "randomblob" in body:
        return C_SQLITE, "uses GLOB/randomblob"
    cols = t["update_of"] or []
    status_text = re.search(r"\b(NEW|OLD)\.(Status|CurrentStatus|State|Decision)\b|\.(Status|CurrentStatus|State)\s*(=|<>|NOT IN|IN)\b", body)
    if any(c in cols for c in STATUS_COLS) and not n.startswith("trg_Ws_") and "NoUpdate" not in n or (status_text and not n.startswith("trg_Ws_") and "NoUpdate" not in n and "NoDelete" not in n):
        return C_STATUS, "guards a status/decision transition or reads another row's status"
    return C_CHECK, subclass(t)


def subclass(t):
    n = t["name"]
    if n.startswith("trg_Ws_"):
        return "tenancy: parent and child must share a workspace/train"
    if "NoUpdate" in n or "NoDelete" in n or "Immutable" in n or "Locked" in n or "Final" in n:
        return "immutability / append-only"
    if "Role" in n or "Separation" in n or "NotAuthor" in n or "Eligible" in n:
        return "role / separation-of-duties lookup"
    return "row or cross-row validation"


def build():
    text = open(SCHEMA, encoding="utf-8").read()
    chunks = split_statements(text)
    tables, indexes, triggers = parse_tables(chunks), parse_indexes(chunks), parse_triggers(chunks)
    for t in triggers:
        t["class"], t["note"] = classify(t)
    # fire order: SQLite fires same (table, timing, event) triggers newest-first = reverse file order.
    # An UPDATE OF col trigger belongs to the group of each statement that sets col, so the true fire list depends on the SET list; we
    # report the group per (table, timing, event) and keep update_of so the oracle can compute the exact list for a given SET.
    groups = collections.defaultdict(list)
    for t in triggers:
        groups[(t["table"], t["timing"], t["event"])].append(t)
    for g in groups.values():
        for rank, t in enumerate(sorted(g, key=lambda x: -x["file_order"]), 1):
            t["sqlite_fire_rank"] = rank
            t["group_size"] = len(g)
    return tables, indexes, triggers, groups


def pg_order(group):
    """Postgres fires same-event triggers in name order (byte order, verified on PG 16: 'trg_B_Upper' fires before 'trg_a_lower')."""
    return sorted(group, key=lambda t: t["name"].encode())


def sqlite_order(group):
    return sorted(group, key=lambda t: -t["file_order"])


def divergence(groups):
    """Per multi-trigger group: the number of trigger PAIRS whose relative order differs between SQLite and Postgres.
    Only pairs that can both be violated by one statement matter for the reported error; the oracle decides which (see plan section 3)."""
    rows = []
    for (tb, tm, ev), g in sorted(groups.items()):
        if len(g) < 2:
            continue
        sq = {t["name"]: i for i, t in enumerate(sqlite_order(g))}
        pg = {t["name"]: i for i, t in enumerate(pg_order(g))}
        names = [t["name"] for t in g]
        inv = [(a, b) for i, a in enumerate(names) for b in names[i + 1:] if (sq[a] < sq[b]) != (pg[a] < pg[b])]
        inv = [(a, b) if sq[a] < sq[b] else (b, a) for a, b in inv]   # (fires first on SQLite, fires second on SQLite)
        rows.append((tb, tm, ev, len(g), len(names) * (len(names) - 1) // 2, len(inv), inv))
    return rows


def esc(s):
    return (s or "").replace("|", "\\|").replace("\n", " ")


def render(tables, indexes, triggers, groups):
    trig_by_table = collections.Counter(t["table"] for t in triggers)
    L = []
    L.append(f"Generated by `tools/schema_inventory.py` from `db/schema.sql`: {len(tables)} tables, {len(triggers)} triggers, {len(indexes)} indexes.\n")
    cls = collections.Counter(t["class"] for t in triggers)
    L.append("### Trigger classes (heuristic, see the script)\n")
    L.append("| class | triggers |\n|---|---|")
    for k in (C_CHECK, C_STATUS, C_CASCADE, C_TIME, C_SQLITE):
        L.append(f"| {k} | {cls.get(k, 0)} |")
    L.append("")
    L.append("### Tables\n")
    L.append("| # | table | columns | CHECKs | FKs | COLLATE NOCASE | triggers |\n|---|---|---|---|---|---|---|")
    for i, t in enumerate(tables, 1):
        L.append(f"| {i} | {t['name']} | {t['columns']} | {t['checks']} | {t['fks']} | {t['nocase'] or ''} | {trig_by_table.get(t['name'], 0) or ''} |")
    L.append("")
    L.append("### Indexes\n")
    L.append("| # | index | table | unique | columns | partial predicate |\n|---|---|---|---|---|---|")
    for i, x in enumerate(indexes, 1):
        L.append(f"| {i} | {x['name']} | {x['table']} | {'yes' if x['unique'] else ''} | {esc(x['columns'])} | {esc(x['where']) if x['where'] else ''} |")
    L.append("")
    L.append("### Triggers\n")
    L.append("`rank` is the SQLite fire position inside its (table, timing, event) group: 1 fires first (newest-first = reverse file order). "
             "An `UPDATE OF` trigger only joins the group when the statement's SET list names one of its columns.\n")
    L.append("| # | trigger | table | timing | event (OF) | rank/group | class | rule text (first RAISE message) | note |\n|---|---|---|---|---|---|---|---|---|")
    for t in triggers:
        ev = t["event"] + (f" OF {', '.join(t['update_of'])}" if t["update_of"] else "")
        msg = t["messages"][0] if t["messages"] else "(no RAISE: writes " + ", ".join(t["writes"]) + ")" if t["writes"] else ""
        more = f" (+{len(t['messages']) - 1} more in body order)" if len(t["messages"]) > 1 else ""
        L.append(f"| {t['file_order']} | {t['name']} | {t['table']} | {t['timing']} | {esc(ev)} | {t['sqlite_fire_rank']}/{t['group_size']} | {t['class']} | {esc(msg)}{more} | {esc(t['note'])} |")
    L.append("")
    L.append("### Same-event groups with more than one trigger (SQLite fire order)\n")
    L.append("| table | timing | event | fire order (first to last) |\n|---|---|---|---|")
    for (tb, tm, ev), g in sorted(groups.items()):
        if len(g) > 1:
            L.append(f"| {tb} | {tm} | {ev} | " + " → ".join(t["name"] for t in sorted(g, key=lambda x: -x["file_order"])) + " |")
    L.append("")
    L.append("### Order divergence: SQLite newest-first versus Postgres alphabetical (byte order)\n")
    L.append("| table | timing | event | triggers | pairs | pairs in a different order on Postgres |\n|---|---|---|---|---|---|")
    tot_p = tot_i = 0
    for tb, tm, ev, n, pairs, ninv, inv in divergence(groups):
        tot_p += pairs; tot_i += ninv
        L.append(f"| {tb} | {tm} | {ev} | {n} | {pairs} | {ninv} |")
    L.append(f"| **total** | | | | **{tot_p}** | **{tot_i}** |")
    L.append("")
    L.append("Inverted pairs (first fires before second on SQLite, after it on Postgres):\n")
    for tb, tm, ev, n, pairs, ninv, inv in divergence(groups):
        for a, b in inv:
            L.append(f"- {tb} {tm} {ev}: `{a}` before `{b}`")
    return "\n".join(L) + "\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--json")
    ap.add_argument("--embed", metavar="MD")
    ap.add_argument("--check-embed", metavar="MD")
    a = ap.parse_args()
    tables, indexes, triggers, groups = build()
    got = {"tables": len(tables), "triggers": len(triggers), "indexes": len(indexes)}
    if got != EXPECT:
        print(f"COUNT MISMATCH: got {got}, expected {EXPECT}", file=sys.stderr)
        sys.exit(1)
    if a.json:
        slim = [{k: v for k, v in t.items() if k != "text"} for t in triggers]
        json.dump({"tables": tables, "indexes": indexes, "triggers": slim}, open(a.json, "w"), indent=1)
    if a.embed or a.check_embed:
        path = a.embed or a.check_embed
        doc = open(path, encoding="utf-8").read()
        b, e = "<!-- inventory:begin (generated by tools/schema_inventory.py --embed; do not edit by hand) -->", "<!-- inventory:end -->"
        if b not in doc or e not in doc:
            sys.exit(f"markers not found in {path}")
        head, rest = doc.split(b, 1)
        tail = rest.split(e, 1)[1]
        new = head + b + "\n\n" + render(tables, indexes, triggers, groups) + "\n" + e + tail
        if a.embed:
            open(path, "w", encoding="utf-8").write(new)
            print(f"embedded inventory in {path}")
        elif new != doc:
            print(f"STALE: {path} differs from the generated inventory; run --embed", file=sys.stderr)
            sys.exit(1)
        else:
            print("inventory block is current")
        return
    if a.check:
        cls = collections.Counter(t["class"] for t in triggers)
        print(f"OK {got}  classes={dict(cls)}")
        return
    sys.stdout.write(render(tables, indexes, triggers, groups))


if __name__ == "__main__":
    main()
