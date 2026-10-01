# Release Management App — Claude Code handoff kit

Scoping is complete. This folder is the seed of the repository: drop it in as the repo root and point Claude Code at it.

## What's here
| Path | Purpose |
|---|---|
| `CLAUDE.md` | Rules Claude Code follows every session (stack, layout, non-negotiables, commands) |
| `docs/PROJECT_SCOPE.md` | The full specification: domain, state machines, engines, API, analytics, import/export, NFRs |
| `docs/DECISIONS.md` | D1–D30 confirmed, rejected alternatives, 14 open items with provisional defaults |
| `docs/MILESTONES.md` | M0–M8: a ready-to-paste prompt and a "Done when" acceptance test for each |
| `docs/UI.md`, `docs/ui/tokens.css` | UI rules and design tokens (macOS look, works on Windows, light + dark) |
| `docs/ui/mockups/` | 9 reference screens as standalone HTML + PNG |
| `db/schema.sql` | The schema contract: 48 tables, 42 triggers, 21 indexes |
| `db/analytics.sql` | The 15 metric queries (M1–M15) |
| `tests/reference/test_schema.py` | 82 enforcement tests against `schema.sql` (`python3 tests/reference/test_schema.py`) |
| `tests/reference/seed_and_query.py` | Seeds 6 months of history through the triggers and runs every metric (deterministic) |
| `tests/reference/fixtures/seed.db`, `expected_metrics.json` | M7's acceptance data: the seeded database and every metric's exact expected rows |
| `tests/reference/fixtures/r26-24-runbook.csv`, `r26-24-scenario.json` | M3's acceptance scenario: the runbook plan in CSV import format, plus setup, clock, actuals and the exact expected forecast |
| `docs/research/` | The four research reports behind the scope |
| `docs/QUESTIONS.md` | Where Claude Code parks questions instead of guessing |

## Before the first session
1. Decide **open item 1** in `docs/DECISIONS.md`: is this the Internal Release Management Platform you already scoped? If yes, reconcile that decision record with this one first.
2. Optionally answer open items 2–7; otherwise Claude Code builds to the provisional defaults behind config keys.
3. `git init`, commit this folder as-is, create the GitHub repo.

## Kickoff prompt (paste into Claude Code)
```
Read CLAUDE.md, then docs/PROJECT_SCOPE.md, docs/DECISIONS.md and docs/MILESTONES.md in full.
Confirm python3 tests/reference/test_schema.py passes (87/87).
Then execute milestone M0 exactly as written in docs/MILESTONES.md.
Do not make architectural decisions: anything not covered goes into docs/QUESTIONS.md.
Stop when M0's "Done when" test passes and summarise what you built and anything you parked.
```
For later milestones, replace "M0" with the next one. After M2 merges, M3, M4 and M5 can run as parallel subagent streams on separate branches.

Visual mockups are also on the claude.ai canvas "Release Management App — Screen Mockups"; the full scope with diagrams is the "Release Management App — Project Scope" doc.

## Run locally
```
python start.py            # starts the API (:6080) and the frontend (:6273), opens the browser
python start.py status     # what is the running instance doing?
python start.py reset      # pull the latest from origin/main, restart both, the browser tab reloads
python start.py stop       # stop both and exit
```
Needs Python 3.9+, the .NET 10 SDK and Node 22. In the app, **Reset** and **Sign out and exit** appear in the toolbar while it runs under `start.py`.
