#!/usr/bin/env python3
"""The receiver samples in docs/samples/webhooks (REOS-164) must agree with the server and with each other: each runs its own self-test against the signature vector in docs/WEBHOOKS.md,
and the vector in the doc is the one the .NET side signs (WebhookCatalogTests.A_known_signature_is_fixed...). Python and Node always run; the C# sample runs when `dotnet` is on the PATH.
    python3 tests/tools/test_webhook_samples.py"""
import hashlib, hmac, pathlib, re, shutil, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
S = ROOT / "docs/samples/webhooks"
failed = 0


def check(cond, label):
    global failed
    print(("ok   " if cond else "FAIL ") + label)
    failed += 0 if cond else 1


vector = hmac.new(b"whsec_test_secret", b'1792504800.7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11.{"id":"evt_1","type":"gate.failed"}', hashlib.sha256).hexdigest()
doc = (ROOT / "docs/WEBHOOKS.md").read_text()
check(f"v1={vector}" in doc, "the documented test vector is the HMAC of the documented inputs")
for f in sorted(S.iterdir()):
    check(f"v1={vector}" in f.read_text(), f"{f.name} carries the documented vector")
net = (ROOT / "tests/ReleaseMgmt.Domain.Tests/WebhookCatalogTests.cs").read_text()
check("whsec_test_secret" in net and "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11" in net, "the .NET test uses the same inputs")

def run(cmd, label):
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    check(r.returncode == 0 and r.stdout.strip() == "ok", f"{label}: {r.stdout.strip() or r.stderr.strip()[:200]}")

run([sys.executable, "-I", str(S / "verify.py"), "--selftest"], "Python sample self-test")
if shutil.which("node"):
    run(["node", str(S / "verify.mjs"), "--selftest"], "JavaScript sample self-test")
else:
    print("skip JavaScript sample (node not on the PATH)")
if shutil.which("dotnet"):
    run(["dotnet", "run", str(S / "Verify.cs"), "--", "--selftest"], "C# sample self-test")
else:
    print("skip C# sample (dotnet not on the PATH)")
print(f"\n{failed} failure(s)")
sys.exit(1 if failed else 0)
