#!/usr/bin/env python3
"""Run the REOS-52 load test (NBomber, 50 virtual users, 200 trains, 100 000 audit rows) and collect its report.

    python3 tools/run_load_test.py                       both profiles (stress, then typical), 60 s measured each
    python3 tools/run_load_test.py --profile stress      only the closed-loop worst case
    python3 tools/run_load_test.py --duration 30 --users 50 --trains 200 --audit-rows 100000
    python3 tools/run_load_test.py --report docs/load-latest.md

Profiles (both use the same seeded data and the same weighted action mix):
  stress   no think time: every user fires the next action as soon as the last one returned (saturation; latency = users / throughput)
  typical  1-3 s think time between actions, the pace of a person working in the app

Exit code 0 only when every profile passes: overall p95 and every read request p95 under the budget (default 300 ms), no unexpected error,
no SQLite busy/locked. It is NOT part of `dotnet test` or CI (it takes minutes and its numbers depend on the machine).
Needs the .NET 10 SDK on PATH and Python 3.9+. The server (the built ReleaseMgmt.Api.dll) runs as a child process on loopback; no network is used.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests" / "ReleaseMgmt.LoadTests"
RESULTS = PROJECT / "test-results"

PROFILES = {
    "stress": {"LOAD_THINK_MIN_MS": "0", "LOAD_THINK_MAX_MS": "0"},
    "typical": {"LOAD_THINK_MIN_MS": "1000", "LOAD_THINK_MAX_MS": "3000"},
}


def dotnet(*args: str, env: dict | None = None) -> int:
    return subprocess.run(["dotnet", *args], cwd=ROOT, env=env).returncode


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--profile", choices=["stress", "typical", "both"], default="both")
    ap.add_argument("--users", type=int, default=50)
    ap.add_argument("--duration", type=int, default=60, help="measured seconds per profile")
    ap.add_argument("--warmup", type=int, default=10, help="warm-up seconds (not measured)")
    ap.add_argument("--trains", type=int, default=200)
    ap.add_argument("--audit-rows", type=int, default=100_000)
    ap.add_argument("--budget-ms", type=int, default=300)
    ap.add_argument("--report", help="also write the combined markdown report here (default: tests/ReleaseMgmt.LoadTests/test-results/load-report.md)")
    args = ap.parse_args()

    # Build the whole solution first: the load runner starts the API exactly as built for src/ReleaseMgmt.Api.
    if dotnet("build", str(ROOT / "ReleaseMgmt.sln"), "-c", "Release", "--nologo", "-v", "q") != 0:
        print("build failed", file=sys.stderr)
        return 2

    RESULTS.mkdir(parents=True, exist_ok=True)
    names = ["stress", "typical"] if args.profile == "both" else [args.profile]
    failed = []
    for name in names:
        report = RESULTS / f"load-{name}.md"
        env = {**os.environ, **PROFILES[name], "LOAD_PROFILE": name, "LOAD_USERS": str(args.users), "LOAD_DURATION_S": str(args.duration),
               "LOAD_WARMUP_S": str(args.warmup), "LOAD_TRAINS": str(args.trains), "LOAD_AUDIT_ROWS": str(args.audit_rows),
               "LOAD_BUDGET_MS": str(args.budget_ms), "LOAD_REPORT": str(report)}
        print(f"\n=== load profile: {name} ===", flush=True)
        rc = dotnet("run", "-c", "Release", "--no-build", "--project", str(PROJECT), env=env)
        if rc != 0:
            failed.append(name)

    combined = "\n\n".join((RESULTS / f"load-{n}.md").read_text(encoding="utf-8") for n in names if (RESULTS / f"load-{n}.md").exists())
    out = Path(args.report) if args.report else RESULTS / "load-report.md"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(combined, encoding="utf-8")
    print(f"\ncombined report: {out}")
    print("RESULT:", "FAIL (" + ", ".join(failed) + ")" if failed else "PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
