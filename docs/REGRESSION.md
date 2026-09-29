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
