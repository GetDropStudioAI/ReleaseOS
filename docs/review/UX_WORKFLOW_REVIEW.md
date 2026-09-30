# UX workflow review: clicks, flow and rage-click risk

I reviewed `main@c9a1180`, `src/ReleaseMgmt.Web/src` (paths below are relative to that folder), against `docs/PROJECT_SCOPE.md` and `docs/UI.md`.

**Main finding:** about 11 screens already disable their buttons while a request is in flight (Bulk, Exports, Evidence, Import/Export, Comms, Sync health, Templates, Connectors, Calendar, Inbox). The most-used screens do not: **Planning, LiveRun, Runbook, Governance, Freezes, Closeout, Admin**.
- On create actions, a double-click sends a duplicate POST and creates a duplicate row.
- On updates, the second request carries the same version and gets a 409. The conflict notice then names the user as the person who changed the record (`Conflict.tsx`).

## Journey map

| Journey | Role | Today | Friction | Proposed |
|---|---|---|---|---|
| Create a train | RTE | There is no create action; trains can only be imported by CSV. That is about 6 clicks plus writing the file outside the app (`ImportExport.tsx:126,157`). The empty state says "Create one…" but offers no action (`App.tsx:217`). | Dead end on first use. | "New train" in the Stream header opens an inline form (title, date, template with its risk pre-filled): 3 clicks. |
| Add tasks to a gate | RTE | Gate → Add task → type → owner → Add. The form closes after each task, and Enter does not submit (`Planning.tsx:168,175-179`). Bulk parser suggestions are plain text, not buttons (`Bulk.tsx:67`). | Repeated clicks for every task. | Keep the form open and focused, submit on Enter, and make suggestions one-click fixes. |
| Work and certify a gate | Gate owner / RTE / GO | Start gate → Mark done for each task (no bulk action) → Certify. None of these has a busy state (`Planning.tsx:195,253-256`). | n+2 clicks, and a double-click causes a false 409. | "Mark all my open tasks done", busy states, and Start offered inline. |
| Waiver | GO | The approver has to find the train, then the gate, then Approve, then Waive (`Freezes.tsx:341-348`). There is no pending-approvals list. | Hunting across trains. | An "Awaiting my approval" group in My work with an inline Approve: 1–2 clicks. |
| Go/No-Go | RM | The header button opens a form far down the page without scrolling to it (`Trains.tsx:110,133`). Record has no busy state and no confirm, though the decision is immutable (`Governance.tsx:98`). | Scroll-hunting, plus double-click risk on an irreversible record. | Scroll to and focus the form, disable Record while sending, and add a summary + Confirm step. |
| Live runbook | RTE / step owner | For each step: click the row → Start step → Mark done. That is 3 clicks per step, about 60 for 20 steps (`LiveRun.tsx:154,226-227`), with no j/k and no busy state. | The highest click volume, under time pressure. | Inline Start and Done on the ready row, "Done & start next", and keyboard keys: about 1 click per step. |
| Freeze windows | RM / GO | Freezes can only be created from inside a train page (`Freezes.tsx:238-248`). Drafts are kept in `useState` and are lost on navigation. | A global object hidden inside a per-train page. | Create freezes from the Calendar or the freeze footer, and save drafts with `useDraft`. |
| Close-out and evidence | RTE / RM / GO | Each PIR action is a single click with no busy state (`Closeout.tsx:105-145`). Exit hypercare has no confirm (`:177`). The evidence pack goes Generate → wait → Download (`ExportsPanel.tsx:147,167`). | Unguarded state changes, and the user must come back to download. | Busy states, inline confirms, and an automatic download when the pack is ready. |
| Comms dispatch | RTE / RM | The drawer picks the next item's template but not `scheduleItemId`, so a send is not logged against that item without an extra click (`CommsDrawer.tsx:210-215`). If there is no schedule, the drawer is a dead end (`:78`). | A hidden extra click, and a cross-screen dead end. | Pre-set the schedule item, and add "Seed schedule" inside the drawer. |
| Import / export | RTE / RM | Choose file → automatic preview → Commit: about 3 clicks, with guarded buttons. | Good as it is. | Add drag-and-drop. |
| Sync health | RTE / RM | Alert row → Resolve now → Confirm. Mismatch rows have no link to their train (`SyncHealth.tsx:295-301`). | Dead end on mismatches. | Make each row open its train. |
| Admin | RTE / RM | There is no success message on save (`Admin.tsx:263`) and no member editor (`:330-335`). The tab is not kept in the URL (`:381`). | Uncertainty about whether a save worked; state lost on refresh. | Show "✓ Saved", add an Inspector member editor, and put the tab in the route. |

## Prioritised findings

| # | Priority | Location | Friction / rage-click risk | Recommendation |
|---|---|---|---|---|
| 1 | High | LiveRun.tsx:196-200, 226-229 | Start step, Mark done and Fail have no busy state. A double-click during a cutover gives a 409 blaming the user. | Disable while pending and label it "Marking done…". |
| 2 | High | LiveRun.tsx:170-172 | "End run: aborted" has no confirm and no busy state. | Inline confirm with a reason. |
| 3 | High | Planning.tsx:253-256 | Start, Certify, Fail gate and Decertify fire on one click. | Busy state on all four, and an inline confirm for Fail and Decertify. |
| 4 | High | Governance.tsx:81, 98-99 | Record decision has no confirm and no busy state. A 409 on a second click is never shown because the form has already closed. | Disable while pending, and add summary + Confirm. |
| 5 | High | Planning.tsx:178; Runbook.tsx:47; Governance.tsx:163; Closeout.tsx:105, 155, 213; Freezes.tsx:248, 296, 348; Admin.tsx:326, 362 (with api.ts:81-163) | Create calls send no If-Match, so a double-click creates **duplicate** tasks, steps, freezes, overrides, waivers and holidays. | Busy-disable every create, plus an `Idempotency-Key` header. |
| 6 | High | api.ts; App.tsx:217; Templates.tsx:279-283 | Trains can only be created by CSV, and templates cannot start a train. | "New train" and "Use template" actions. |
| 7 | High | Trains.tsx:80, 146, 155; Planning.tsx:75 | Load failures look like empty lists ("No trains"). | Show an inline error with Retry. |
| 8 | High | Planning.tsx:162, 195, 240; Closeout.tsx:119-121, 144-145, 177, 189-191 | Task toggles and PIR actions have no busy state. | Shared `useAction` hook (below). |
| 9 | Med | LiveRun.tsx:135-160; rowNav.ts | The Stream, Checklist, Runbook and LiveRun have no row keyboard navigation and no inline actions. | Extend `useRowNav` and add action keys. |
| 10 | Med | Planning.tsx:168, 175-179; Runbook.tsx:33, 41-48 | Add forms close after each add, Enter doesn't submit, and a new step's start time isn't preset. | Keep the form open, wrap it in `<form>`, and preset start = previous end. |
| 11 | Med | Trains.tsx:110, 133 | Record Go/No-Go opens off-screen. | `scrollIntoView` plus focus. |
| 12 | Med | Trains.tsx:130-138 | The train page is one long scroll of about 10 sections. | A sticky section index. |
| 13 | Med | App.tsx:212 | Switching train resets the mode to Plan. | Keep Live when the target train has an open run. |
| 14 | Med | App.tsx:222; CommsDrawer.tsx:203 | Esc closes the drawer even while typing, and closing discards the draft. | Ignore Esc in inputs, and keep the draft on close. |
| 15 | Med | CommsDrawer.tsx:210-215 | `scheduleItemId` is not pre-set. | Pre-set it to the next unsent item. |
| 16 | Med | CommsDrawer.tsx:78; CommLibrary.tsx:161, 330 | With no schedule the drawer is a dead end, and seeding elsewhere defaults to the first train. | Seed from inside the drawer. |
| 17 | Med | CommsDrawer.tsx:238 | The copy says to add webhooks "in Admin", but the allowlist is on Sync health. | Fix the copy and make it a link. |
| 18 | Med | Freezes.tsx:200-201, 318; Closeout.tsx:62-64, 95-96, 165-166; Templates.tsx:188; CommLibrary.tsx:134; LiveRun.tsx:182, 186 | Forms keep drafts in `useState`, so typed text is lost on navigation (PROJECT_SCOPE §5.4). | Use `useDraft`. |
| 19 | Med | AuditViewer.tsx:158; SyncHealth.tsx:311; Calendar.tsx:229; CommLibrary.tsx:161 | Pickers default to All or the first train, so the user re-selects the same train on every screen. | Default to the active train. |
| 20 | Med | SyncHealth.tsx:295-301 | Mismatch rows are a dead end. | Link each row to its train. |
| 21 | Med | Admin.tsx:293, 371 | Deactivate and Remove holiday have no confirm. | Inline confirm. |
| 22 | Med | Admin.tsx:330-335 | Team members cannot be edited in the UI. | Member editor. |
| 23 | Med | Planning.tsx:182-199; CommSchedule.tsx:274-282 | No bulk actions. | Multi-select plus a bulk action. |
| 24 | Low | Governance.tsx:68, 158 | Close condition and Remove CI have no guard. | Busy state plus confirm. |
| 25 | Low | Trains.tsx:33 | The Stream filters by title only. | Add state, risk and "mine" filters. |
| 26 | Low | Trains.tsx:14-17 | Aborted trains vanish from the Stream. | Add a collapsed Aborted group. |
| 27 | Low | Admin.tsx:263, 315, 350; Planning.tsx:45, 168 | No success confirmation on save. | "✓ Saved" line. |
| 28 | Low | Admin.tsx:381; Calendar | Tab and view state are not in the URL. | Put them in the route. |
| 29 | Low | LiveRun.tsx:226-229 | The mockup's "Escalate" action is missing. | Build it, or drop it from the spec. |
| 30 | Low | Trains.tsx:113 | The Advance-disabled reason is only in a tooltip. | Show it as text on the line below. |

## Cross-cutting recommendations

1. **A shared `useAction()` hook.** It returns `{run, pending, error, conflict}`: it disables the trigger, shows a pending label, ignores re-entry, and handles a 409 with "Retry with latest". It should recognise the user's own earlier request and refetch quietly. This replaces 8 hand-rolled `act`/`run` helpers.
2. **Idempotency keys** on create endpoints, so a double-click can never create duplicates even if the UI guard is missed.
3. **A persistent train context.** The Stream selection becomes the default for every picker, with "for this train" deep links, and Live mode is kept when switching between executing trains.
4. **A command palette plus action keys.** Cmd/Ctrl-K jumps to a train, gate or step. `useRowNav` extends to the Stream, Checklist and Runbook, with Space for done, `s` for start and Enter to open (keeping the existing typing guard).
5. **One `<ConfirmInline reason?>` component** for every destructive or irreversible action, since there is no undo anywhere.
6. **All drafts through `useDraft`.** Closing a drawer sets `open:false` instead of discarding the draft.
7. **A live runbook built for speed.** Inline Start/Done, "Done & start next", and automatic selection of the next ready step. This takes a 20-step cutover from about 60 clicks to about 20.
8. **Every empty or error state offers a next action:** New train, Seed schedule, Retry, Open train, and "Awaiting my approval" in My work.

Mockup **E · Next Action** in the design canvas shows recommendations 3, 4 and 8 as a screen.
