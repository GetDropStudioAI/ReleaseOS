# Regression gate

Jira workflow: **To Do → Ready → In Progress → In Review → Done**. **In Review is the regression hold** (agreed 2026-09-29).
A story moves to In Review when its own acceptance criteria pass. It stays there until the **whole sprint** clears the full regression below, then every story in the sprint moves to Done together. Any failure sends the affected story back to In Progress; fix, then rerun the full suite (not only the failing part) before anything closes.

## Full regression (run on the sprint's final head commit)
| # | Check | Command | Pass means |
|---|---|---|---|
| 1 | Build, warnings as findings | `dotnet build ReleaseMgmt.sln -c Release --no-incremental` | 0 errors, 0 warnings |
| 2 | All .NET tests | `dotnet test ReleaseMgmt.sln -c Release` | 0 failed |
| 3 | Schema oracle | `python3 tests/reference/test_schema.py` | 82 passed, 0 failed |
| 3b | Tooling tests | `python3 tests/tools/test_start.py` | all pass |
| 4 | Metrics oracle | `python3 tests/reference/seed_and_query.py` | runs clean; matches `expected_metrics.json` once M7 lands |
| 5 | Web build | `cd src/ReleaseMgmt.Web && npm ci && npm run build` | builds, no TypeScript errors |
| 6 | CI | GitHub Actions `build-test` on `windows-latest` and `macos-latest` | both green on the head commit |
| 7 | UI, both themes | screenshots of every screen changed this sprint in light and dark (Playwright + axe from M2 on) | no layout breaks; axe: no serious violations |
| 8 | Human-only acceptance items | listed per story in Jira | signed off by the named person |

## Sprint log
### AMO Sprint 1 (REOS-10 to 21): started 2026-09-29
| Check | Result | Head |
|---|---|---|
| 1 build | 0 errors, 0 warnings (after fixing 6 warnings found by this run) | see git log |
| 2 tests | 40 infrastructure + 33 API passed | |
| 3 oracle | 82/82 | |
| 4 metrics | ran clean | |
| 5 web build | ok | |
| 6 CI | green on Windows and macOS on `3361369` (the head after the warning fixes) | `3361369` |
| 7 UI | shell and admin screens checked in light and dark | |
| 8 human items | REOS-14 second-machine restore **waived by john.selph 2026-09-29**: the automated restore tests (`BackupTests`, green in CI on both OSes) count as acceptance. The timed DR drill stays in REOS-52. | |

**Closed 2026-09-29:** all 12 stories (REOS-10 to 21) moved In Review → Done together. Epics REOS-1 (M0) and REOS-2 (M1) closed with them.

### AMO Sprint 2 (REOS-22 to 30, plus REOS-56 to 59): started 2026-09-29, closed 2026-09-30
Final regression re-run on head `f3f2bac` (after the display-zone change that touched Sprint 2 screens; the earlier run on `baa4e29` is superseded).

| Check | Result | Head |
|---|---|---|
| 1 build | `dotnet build -c Release --no-incremental`: 0 errors, 0 warnings | `f3f2bac` |
| 2 tests | 45 Domain + 44 Infrastructure + 81 API passed, 0 failed | |
| 3 oracle | 82/82 | |
| 3b tooling | `tests/tools/test_start.py` OK | |
| 4 metrics | `seed_and_query.py` ran clean | |
| 5 web build | `npm ci && npm run build`: builds, 0 TypeScript errors | |
| 6 CI | green on `windows-latest` and `macos-latest` for `f3f2bac` (run 49), including the Playwright job (19 browser tests, app started and stopped through `start.py`) | `f3f2bac` |
| 7 UI, both themes | 19 Playwright tests incl. axe (wcag2a/aa, 2.1 a/aa, best-practice) on sign-in, workspace with Inspector, Admin and the live runbook in light and dark: no serious/critical violations; screenshots reviewed in both themes | |
| 8 human items | REOS-56 to 58: **Mac run by john.selph 2026-09-30, no problems seen** (expected-unbuilt features aside). **Windows manual run waived by john.selph 2026-09-30**: CI starts and stops the app through `start.py` on `windows-latest` on every push, which already found and fixed the one Windows defect (`localhost` resolving to IPv6 only). Not exercised on Windows: toolbar Reset/Exit buttons and the browser opening. | |

Defects the regression found and fixed this sprint: axe contrast (selected Stream row 4.37:1; selected table rows; status colours on the selection tint) and nested-interactive timeline; the 409 notice vanishing on refetch; Windows `localhost`/IPv6 timing out the e2e job; a provisional browser-time-zone choice that contradicted D24.

**Closed 2026-09-30:** REOS-22 to 30 and REOS-56 to 59 (13 stories) moved In Review -> Done together. Open confirmations left in `docs/QUESTIONS.md`, built to provisional defaults and not blockers: Q-007 (product Health), Q-010 (plan lock while a Live run is open), Q-011 (forecast edge rules).

### AMO Sprint 3 (REOS-31 to 38): started 2026-09-30, closed 2026-09-30
Full regression on head `a1a0ed0` (the final merged head of all eight stories).

| Check | Result | Head |
|---|---|---|
| 1 build | `dotnet build ReleaseMgmt.sln -c Release --no-incremental`: 0 errors, 0 warnings | `a1a0ed0` |
| 2 tests | 45 Domain + 60 Infrastructure + 196 API passed, 0 failed | |
| 3 oracle | 82/82 | |
| 3b tooling | `tests/tools/test_start.py` OK | |
| 4 metrics | `seed_and_query.py` ran clean | |
| 5 web build | `npm ci && npm run build`: ok, no TypeScript errors | |
| 6 CI | run 55: `build-test` green on `windows-latest` and `macos-latest` (build, tests, oracle, tooling, Playwright on both) | `a1a0ed0` |
| 7 UI, both themes | Playwright 35/35 locally on the merged tree; axe (serious/critical) scans in light and dark for the Stream and workspace, admin, live runbook, governance panels (freeze, waivers, close-out, evidence), My work, Inbox and Audit | |
| 8 human items | none listed on REOS-31 to 38 | |

Notes:
- Stories REOS-34 to 38 were built by parallel agents in isolated worktrees and merged one at a time; the merged tree, not the individual branches, is what this regression covers. Merge conflicts were only appended-line collisions (`Program.cs`, `api.ts`, `QUESTIONS.md`, `app.css`, `App.tsx`).
- Two spec fixes came out of the merged run (My work/Inbox: the drill notifies every RTE and a 25-minute-late step is level 1; Audit: two events so j/k have a second row on a fresh database). No product defect.
- Open confirmations, not blocking: Q-007, Q-010, Q-011, Q-037a (level-2 escalation reading). Not built and recorded: train creation from a template or a prior train (Q-038t3).

**Closed 2026-09-30:** all eight stories (REOS-31 to 38) moved In Review to Done together.

## Sprint 4 (REOS-39 to 54)

Full regression on the merged head (REOS-53 merge on top of 733b3b2).

| Check | Result |
|---|---|
| 1 build | `dotnet build --no-incremental`: 0 errors, 0 warnings |
| 2 tests | 298 Domain + 200 Infrastructure + 523 API + 2 DR drill passed, 0 failed |
| 3 oracle | 82/82 |
| 3b tooling | `tests/tools/test_start.py` OK |
| 4 metrics | `seed_and_query.py` ran clean |
| 5 web | build ok, 13 web unit tests pass |
| 6 UI, both themes | Playwright 77/77 on a freshly started host (`Pdf__QuestPdfLicense=Evaluation`); axe plus keyboard-only walkthrough of all 12 views, both themes |
| 7 CI | run 70 on head `12e8f25`: `build-test` green on `windows-latest` and `macos-latest` (build, .NET tests, oracle, tooling, Playwright on both). Runs 64 to 69 were red on test-only defects, all fixed at root: Windows CRLF in a fixture, a fixed-window write-count assertion, the PDF trailer /ID mask, a sign-out navigation race |
| 8 human items | REOS-54: pilot run and evidence-pack acceptance by a Governance Officer is human-led and not done |

Notes:
- A full-suite run on the merged tree failed because Playwright reused a stale API process built before REOS-50; the train view crashed on a non-array export-jobs response. Fixed by making the panel show an inline error. Always start e2e on a fresh host.
- REOS-52 found and fixed three product defects (train-list readiness lookups, audit entity filter, API start on a restored database). REOS-53 found and fixed a Viewer able to complete or reopen tasks (Q-053a), sessions surviving user deactivation (Q-053c), four 500s on bad input, and missing keyboard focus on bottom-rule inputs.
- Not fixed, needs owner decision: webhook URLs stored in clear (Q-053e), key ring plain XML (Q-052e), QuestPDF licence tier (OI-2), NBomber licence (Q-052a).
- Load and DR numbers are from a 4-core shared container, not the pilot host.

**Closed 2026-09-30:** REOS-39 to 53 moved In Review to Done together. REOS-54 stays In Review: the pilot run and Governance Officer acceptance of its evidence pack are human-led.
