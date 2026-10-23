#!/usr/bin/env python3
"""Verifies the audit hash chain of a ReleaseOS database file (REOS-114), from the file alone: no app, no .NET.

  python3 tools/verify_audit_chain.py data/releasemgmt.db            # every chain
  python3 tools/verify_audit_chain.py copy.db --chain default        # one workspace's chain ('*' = the host-wide rows)

Each workspace's audit rows (and, apart, the host-wide rows whose WorkspaceId is NULL) form a chain in Id order:
    Hash = SHA-256( PrevHash + "\\n" + Canonical(row) ),  the first row's PrevHash being 64 zeros.
Canonical(row) is the version line "ReleaseOS.Audit.v1", then the row's Id, OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, BeforeJson, AfterJson
and WorkspaceId, each as "<utf-8 byte length>:<value>" or "-" for NULL, all joined by line feeds. The app seals new rows right after they are written (src/ReleaseMgmt.Domain/Services/AuditChain.cs);
this script repeats the check and names the FIRST row at which a chain stops verifying: a row that was changed, a row after one that was removed or inserted behind the chain, or
rows removed from the end (the chain's head, AuditChainHeads, remembers where it ended). Rows written but not yet sealed ("pending") are not a failure.

Exit code 0: every chain verifies. 1: one does not (the first bad row is printed). 2: the file cannot be read.
The database is opened read-only.
"""
import argparse, hashlib, sqlite3, sys

GENESIS = "0" * 64
VERSION = "ReleaseOS.Audit.v1"
HOST = "*"


def _field(v):
    return "-" if v is None else f"{len(str(v).encode('utf-8'))}:{v}"


def canonical(row_id, occurred_at, actor, train, entity_type, entity_id, action, before, after, workspace):
    return "\n".join([VERSION, _field(row_id), _field(occurred_at), _field(actor), _field(train), _field(entity_type), _field(entity_id), _field(action), _field(before), _field(after), _field(workspace)])


def chain_hash(prev, canon):
    return hashlib.sha256((prev + "\n" + canon).encode("utf-8")).hexdigest()


def verify_chain(db, chain):
    prev = GENESIS
    sealed = pending = last_sealed_id = 0
    last_hash = None
    first_unsealed = None
    bad = None
    where = "WorkspaceId IS NULL" if chain == HOST else "WorkspaceId = ?"
    args = () if chain == HOST else (chain,)
    for (rid, at, actor, train, etype, eid, action, before, after, ws, prev_hash, hash_) in db.execute(
            "SELECT Id, OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, BeforeJson, AfterJson, WorkspaceId, PrevHash, Hash "
            f"FROM AuditEvents WHERE {where} ORDER BY Id", args):
        if hash_ is None:
            pending += 1
            if first_unsealed is None:
                first_unsealed = rid
            continue
        if first_unsealed is not None and bad is None:
            bad = (first_unsealed, "This row was added behind the chain: rows after it were sealed first, so it is not part of the chain")
        if prev_hash != prev and bad is None:
            bad = (rid, "The previous hash does not match the row before it: a row before this one was changed, removed or inserted")
        computed = chain_hash(prev, canonical(rid, at, actor, train, etype, eid, action, before, after, ws))
        if computed != hash_ and bad is None:
            bad = (rid, "The content of this row does not match its hash: the row was altered")
        prev, last_hash, last_sealed_id = hash_, hash_, rid
        sealed += 1
    head = db.execute("SELECT LastId, LastHash FROM AuditChainHeads WHERE ChainId = ?", (chain,)).fetchone()
    if head is not None:
        head_id, head_hash = head
        if head_id > last_sealed_id and bad is None:
            bad = (head_id, f"The chain ends at row {head_id} but that row (or rows after row {last_sealed_id}) is missing: rows were removed from the end")
        elif head_id == last_sealed_id and head_hash != last_hash and bad is None:
            bad = (last_sealed_id, "The last sealed row does not match the chain's head")
        elif head_id < last_sealed_id and bad is None:
            bad = (head_id, "Rows were sealed beyond the chain's head: the head was changed")
    elif sealed > 0 and bad is None:
        bad = (last_sealed_id, "The chain's head record is missing")
    return {"chain": chain, "status": "Verified" if bad is None else "Failed", "sealed": sealed, "pending": pending, "first_bad": bad}


def main(argv=None):
    ap = argparse.ArgumentParser(description="Verify the audit hash chain of a ReleaseOS database file.")
    ap.add_argument("database")
    ap.add_argument("--chain", help="one chain: a workspace id, or '*' for the host-wide rows")
    a = ap.parse_args(argv)
    try:
        db = sqlite3.connect(f"file:{a.database}?mode=ro", uri=True)
        chains = [a.chain] if a.chain else [r[0] for r in db.execute(
            "SELECT COALESCE(WorkspaceId, '*') FROM AuditEvents UNION SELECT ChainId FROM AuditChainHeads ORDER BY 1")]
        results = [verify_chain(db, c) for c in chains]
    except sqlite3.Error as e:
        print(f"cannot read {a.database}: {e}", file=sys.stderr)
        return 2
    failed = False
    for r in results:
        name = "host-wide rows" if r["chain"] == HOST else r["chain"]
        if r["status"] == "Verified":
            print(f"{name}: verified, {r['sealed']} sealed rows, {r['pending']} pending")
        else:
            failed = True
            print(f"{name}: FAILED at row {r['first_bad'][0]}: {r['first_bad'][1]}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
