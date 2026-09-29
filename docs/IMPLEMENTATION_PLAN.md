# Implementation plan

Tracked in Jira project **REOS** (https://amortis.atlassian.net/browse/REOS-1). One epic per milestone; stories carry the milestone's acceptance tests.

## Order of work
```
M0 → M1 → M2 → (M3 ∥ M4 ∥ M5) → M6 → M7 → M8
```
M3, M4 and M5 run as parallel streams on separate branches once M2 merges, talking only through `INotifier` and `IReadinessService`. M6 and M7 wait for all three.

## Epics and stories
| Epic | Milestone | Stories |
|---|---|---|
| REOS-1 | M0 Foundation | REOS-10 skeleton + CI (Windows, macOS) · 11 SQLite/EF conventions · 12 host, Serilog, /healthz, TimeProvider · 13 auth + roles · 14 backup + restore · 15 web shell, light/dark |
| REOS-2 | M1 Domain + enforcement | REOS-16 entities · 17 triggers migration + DbRule 422 · 18 port 82-case suite · 19 domain services, audit, concurrency · 20 business-day DueOn · 21 seed + admin screens |
| REOS-3 | M2 Workspace + session | REOS-22 Stream + header · 23 readiness · 24 products/timeline/checklist/Inspector · 25 INotifier + SignalR · 26 session engine · 27 conflict UI + a11y |
| REOS-4 | M3 Checklists + runbook | REOS-28 bulk parser · 29 runbook editor · 30 runs · 31 forecast · 32 live runbook screen |
| REOS-5 | M4 Governance | REOS-33 change record + Go/No-Go · 34 freezes, overrides, waivers · 35 evidence · 36 rollback, PIR, known issues · 37 notifications + escalation · 38 My work, inbox, audit, templates |
| REOS-6 | M5 ITSM sync | REOS-39 connectors · 40 poller + alerts · 41 watchdog + mismatch · 42 Sync Health screen |
| REOS-7 | M6 Communications | REOS-43 library + schedule · 44 hydration · 45 dispatch + drawer |
| REOS-8 | M7 Analytics + exchange | REOS-46 endpoints · 47 screen · 48 CSV import · 49 exports · 50 PDFs + evidence pack · 51 calendar + ICS |
| REOS-9 | M8 Hardening + pilot | REOS-52 load + DR · 53 a11y + security · 54 ops runbook + pilot |

## Cross-cutting requirements
- **Light and dark mode** on every screen from M0 (tokens + `data-theme`), checked by axe in both themes.
- CI on `windows-latest` and `macos-latest` (D31).
- Role changes clarified in D32 and reflected in PROJECT_SCOPE §1.
- Anything not covered goes to `docs/QUESTIONS.md`.

## Working rules
Branch `main` (the `Team` branch is retired, 2026-09-29); one story at a time; each story's acceptance criteria is its test. Commit per story referencing the REOS key.
