#!/usr/bin/env python3
"""REOS-102: WCAG 2.2 contrast of every text token on every surface it can sit on, both themes, read from docs/ui/redesign-tokens.json.
Prints a table and exits 1 only with --strict (the failures are recorded as owner decisions in docs/QUESTIONS.md, not silently changed)."""
import json, sys, pathlib

root = pathlib.Path(__file__).resolve().parents[2]
tokens = json.loads((root / "docs/ui/redesign-tokens.json").read_text())

def lum(h):
    h = h.lstrip("#"); c = [int(h[i:i+2], 16) / 255 for i in (0, 2, 4)]
    c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]

def ratio(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)

# Find the colour list whatever the JSON nesting is.
def walk(o):
    if isinstance(o, dict):
        if "name" in o and ("light" in o or "Light" in o): yield o
        for v in o.values(): yield from walk(v)
    elif isinstance(o, list):
        for v in o: yield from walk(v)

modes = {"light": {}, "dark": {}}
for e in walk(tokens):
    for m in modes:
        v = e.get(m) or e.get(m.title())
        if isinstance(v, str): modes[m][e["name"]] = v
        elif isinstance(v, dict) and "hex" in v: modes[m][e["name"]] = v["hex"]

TEXT = ["text/primary", "text/secondary", "text/tertiary", "accent/text", "status/success", "status/warning", "status/danger"]
SURF = ["bg/canvas", "bg/surface", "bg/subtle", "bg/muted", "bg/accent-subtle", "bg/accent-faint", "bg/success-subtle", "bg/warning-subtle", "bg/danger-subtle"]
fails = 0
for m, c in modes.items():
    print(f"\n== {m} ==")
    for t in TEXT:
        tk = next((k for k in c if k.endswith(t) or k == t), None)
        if tk is None: continue
        row = []
        for s in SURF:
            sk = next((k for k in c if k.endswith(s) or k == s), None)
            if sk is None: continue
            r = ratio(c[tk], c[sk]); ok = r >= 4.5; fails += not ok
            row.append(f"{s.split('/')[-1]}={r:.2f}{'' if ok else '!'}")
        print(f"{tk:22} {c[tk]}  " + "  ".join(row))
print(f"\n{fails} pair(s) below 4.5:1 (! marks them)")
sys.exit(1 if fails and "--strict" in sys.argv else 0)
