#!/usr/bin/env python3
"""Timed disaster-recovery drill (REOS-52): the restore runbook, executed and timed.

    python3 tools/dr_drill.py                       production-scale instance (200 trains, 100 000 audit rows, 60 x 1 MB attachments)
    python3 tools/dr_drill.py --scale small         the same drill on the small data set the CI test uses (seconds)
    python3 tools/dr_drill.py --audit-rows 1000000 --attach-count 200   a larger instance, to see how the timings grow
    python3 tools/dr_drill.py --write docs/dr-drill-latest.md

What it does (all inside a scratch directory, nothing outside it is touched, no network):
  1. builds a populated instance by running the real API: demo data, bulk history, evidence attachments, connector credentials written through the real
     credential store (encrypted with the Data Protection key ring), an ICS token
  2. takes a backup with the application's own BackupService while it runs, and copies data/keys, data/secrets and data/attachments off the box
  3. simulates total loss: deletes the database, WAL, key ring, credentials, attachments and logs
  4. restores with `ReleaseMgmt.Api restore <backup> data/releasemgmt.db`, copies the three directories back, starts the API and waits for /healthz,
     timing every step with a monotonic clock
  5. verifies: integrity_check, the 42 triggers of db/schema.sql, row counts in every table, the audit trail (SHA-256 over every row), /healthz, the API serves the data,
     the connector credentials decrypt with the restored key ring and FAIL with a clear error without it, attachment SHA-256 values, the ICS token
  6. asserts the total restore time is under 30 minutes
Then it runs the live-backup consistency test: backups of a WAL database taken while writers are running (integrity_check plus a transaction-consistency invariant).

The drill is implemented as xUnit tests in tests/ReleaseMgmt.DrDrill.Tests (a real API process needs the built solution and the .NET SDK, which this repo already
requires); this script is the operator-facing entry point. Requires Python 3.9+ and the .NET 10 SDK on PATH. Exit code 0 = everything passed.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests" / "ReleaseMgmt.DrDrill.Tests"


def dotnet(*args: str, env: dict | None = None) -> int:
    return subprocess.run(["dotnet", *args], cwd=ROOT, env=env).returncode


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--scale", choices=["small", "full"], default="full")
    ap.add_argument("--trains", type=int)
    ap.add_argument("--audit-rows", type=int)
    ap.add_argument("--attach-count", type=int)
    ap.add_argument("--attach-kb", type=int)
    ap.add_argument("--live-seconds", type=float, default=15, help="how long writers run during the live-backup consistency test")
    ap.add_argument("--write", help="write the markdown tables here")
    args = ap.parse_args()

    if dotnet("build", str(ROOT / "ReleaseMgmt.sln"), "-c", "Release", "--nologo", "-v", "q") != 0:
        print("build failed", file=sys.stderr)
        return 2

    out = Path(tempfile.mkdtemp(prefix="reos-dr-report-")) / "dr-report.json"
    env = {**os.environ, "DR_DRILL_SCALE": args.scale, "DR_DRILL_REPORT": str(out), "DR_LIVE_SECONDS": str(args.live_seconds)}
    for key, value in (("DR_TRAINS", args.trains), ("DR_AUDIT_ROWS", args.audit_rows), ("DR_ATTACH_COUNT", args.attach_count), ("DR_ATTACH_KB", args.attach_kb)):
        if value is not None:
            env[key] = str(value)

    rc_drill = dotnet("test", str(PROJECT), "-c", "Release", "--no-build", "--filter", "FullyQualifiedName~DrDrillTests", env=env)
    rc_live = dotnet("test", str(PROJECT), "-c", "Release", "--no-build", "--filter", "FullyQualifiedName~LiveBackupConsistencyTests",
                     "--logger", "console;verbosity=detailed", env=env)

    if out.exists():
        report = json.loads(out.read_text(encoding="utf-8"))
        print("\n" + report["markdown"])
        print(f"TOTAL RESTORE TIME: {report['restoreSeconds']:.2f} s (budget {report['budgetSeconds']:.0f} s)")
        if args.write:
            Path(args.write).write_text(report["markdown"], encoding="utf-8")
    ok = rc_drill == 0 and rc_live == 0
    print("RESULT:", "PASS" if ok else f"FAIL (drill exit {rc_drill}, live-backup exit {rc_live})")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
