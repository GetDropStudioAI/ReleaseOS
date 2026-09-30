# WCAG 2.1 A/AA accessibility audit: ReleaseMgmt.Web

- **Audited code:** `main@c9a1180`, `src/ReleaseMgmt.Web` (every `.tsx`/`.ts` screen, `src/app.css`, `index.html`, `docs/ui/tokens.css`). Paths below are relative to `src/ReleaseMgmt.Web`.
- **Method:** a static code review against WCAG 2.1 Level A and AA. There was no runtime testing with a screen reader. The axe e2e suite already in `tests/e2e` catches the rules axe can detect; this audit targets the ones it cannot (focus management, keyboard paths, announcements, meaning conveyed by glyph or colour).

**Summary:** 67 issues: 1 Critical, 5 Serious, 26 Moderate and 35 Minor. The recurring themes are:
- focus is lost when inline forms, confirms or drawers open and close;
- live and async results are not announced;
- selection is shown only by a faint tint and by `aria-selected` on plain tables;
- some glyphs stand alone without a word.

## Findings

| # | Severity | WCAG SC | Location (file:line) | Issue | Fix |
|---|---|---|---|---|---|
| 1 | Critical | 2.1.1 Keyboard (A) | src/Admin.tsx:38 | User rows are selectable only with `<tr onClick>`: no tabIndex, key handler or button. Keyboard users cannot edit a handle or deactivate a user. | Put a `<button className="plainlink">` in the Name cell, as Connectors.tsx:174 does. |
| 2 | Serious | 2.4.7 Focus Visible (AA) | src/app.css:71 (src/Planning.tsx:126) | `.timeline .tl-gate { outline: none }` overrides the global focus ring, and the replacement (an underline on an SVG circle) renders nothing. Keyboard focus on a gate is invisible. | Remove `outline:none`, or stroke `.tl-dot` with a 2px `--accent` on `:focus-visible`. |
| 3 | Serious | 1.4.10 Reflow (AA) | src/app.css:4, 18, 104, 129; src/App.tsx:187-209 | The toolbar is a single non-wrapping, all-`nowrap` row in a fixed 44px track. At 320 CSS px (400% zoom) the page scrolls horizontally. | Allow `flex-wrap` with an auto row height, or collapse the nav into a menu below 900px. |
| 4 | Serious | 1.1.1 Non-text Content (A) / 1.3.1 (A) | src/Trains.tsx:124 | On the "To reach Executing" readiness line, met or not met is shown only as ✓/✗ in green or red, with no word. | Add "met" / "not met" (visible or `sr-only`) and `aria-hidden` the glyph. |
| 5 | Serious | 2.4.3 Focus Order (A) | src/CommsDrawer.tsx:203, 282-284 | Opening the Comms drawer does not move focus into it. Close and Esc unmount it without returning focus, so focus drops to `<body>`. | Focus the drawer heading on open and restore focus to "Communicate" on close. |
| 6 | Serious | 4.1.3 Status Messages (AA) | src/ExportsPanel.tsx:43-50, 63 | Export job status changes (polled every 3s) and the new Download link are not announced. | Add one persistent `aria-live="polite"` line ("Evidence pack ready to download"). |
| 7 | Moderate | 2.4.2 Page Titled (A) | index.html:7; src/route.ts:45-49 | `document.title` is "Release Management" on every view. | Set `"<view or train> · Release Management"` on each route change. |
| 8 | Moderate | 2.4.3 (A) / 4.1.3 (AA) | src/route.ts:45-49; src/App.tsx:190-201, 216 | Client-side navigation swaps `<main>` but leaves focus on the nav link, and nothing is announced. | Focus the new `<h1 tabIndex={-1}>` after navigation. |
| 9 | Moderate | 2.1.4 Character Key Shortcuts (A) | src/rowNav.ts:17-25; src/SyncHealth.tsx:228-239; src/AuditViewer.tsx:136-148; src/Connectors.tsx:154 | The single-key `j`/`k`/`Enter` handlers on `window` cannot be turned off and are not scoped to the table. | Handle keys only while focus is in the table, or add an off switch. |
| 10 | Moderate | 4.1.2 Name, Role, Value (A) | src/rowNav.ts:13-27; src/MyWork.tsx:80; src/Inbox.tsx:84; src/SyncHealth.tsx:152, 220-226 | `j`/`k` changes the highlighted row without moving DOM focus. `aria-selected` on `<tr>` in a plain table is not announced. | Use `role="grid"` with a roving tabIndex, or focus the row's button. |
| 11 | Moderate | 2.1.1 (A) / 4.1.2 (A) | src/SyncHealth.tsx:152, 160-161 | Resolved-alert rows open only through `<tr onClick>`, with no focusable control. | Render the same "Details" button that open alerts have. |
| 12 | Moderate | 4.1.2 (A) | src/Templates.tsx:241-242; src/CommLibrary.tsx:248, 319 | Clickable `<tr tabIndex=0>` has no role or name and ignores Space. | Put a `<button>` in the name cell and remove tabIndex and the key handler from the row. |
| 13 | Moderate | 2.4.3 Focus Order (A) | src/Planning.tsx:50, 57-58; src/Runbook.tsx:117-119; src/Calendar.tsx:276, 294-307; src/SyncHealth.tsx:128-135, 392-393; src/Inbox.tsx:90; src/App.tsx:81-89; src/LiveRun.tsx:229; src/Templates.tsx:280; src/Governance.tsx:98; src/Freezes.tsx:102-103, 129-130, 178-181; src/Closeout.tsx:84, 127, 131, 192-197; src/Admin.tsx:85, 90-91; src/CommSchedule.tsx:100-108; src/CommLibrary.tsx:240, 248, 301, 319; src/AuditViewer.tsx:79, 143; src/Evidence.tsx:89 | Inline edit and confirm patterns unmount the focused button (Edit → inputs, Remove → Confirm, Mark sent → text), which drops focus to `<body>`. | Focus the first new control, and return focus to the trigger on Cancel, Close or Save. |
| 14 | Moderate | 2.4.3 Focus Order (A) | src/App.tsx:220-228; src/Planning.tsx:126-127, 219; src/LiveRun.tsx:154, 188; src/Bulk.tsx:34, 45; src/Trains.tsx:111; src/Planning.tsx:173 | The Inspector, Step drawer and Bulk drawer open without focus and close without returning it. There is no scoped Esc handler, although docs/UI.md requires "Esc closes the drawer". | Focus the drawer heading on open, add a scoped Esc handler, and restore focus on close. |
| 15 | Moderate | 2.4.3 Focus Order (A) | src/Trains.tsx:112 | "Export" scrolls to `#exports` but leaves focus on the button. | Also focus the Exports heading (`tabIndex=-1`). |
| 16 | Moderate | 4.1.3 Status Messages (AA) | src/App.tsx:156-162, 192; src/live.ts:32-36 | SignalR pushes (gate, task and step changes; new notifications) update the UI silently. | Add one app-level polite live region that summarises pushes. |
| 17 | Moderate | 4.1.3 Status Messages (AA), misuse | src/App.tsx:104 | `LiveStatus` is `role="status"` and contains the clock, so the time is announced every minute on every screen. | Keep only Live / Reconnecting / Offline inside the live region. |
| 18 | Moderate | 1.4.11 Non-text Contrast (AA) / 1.4.1 (A) | docs/ui/tokens.css:97; src/app.css:31, 53, 205; src/Admin.tsx:38; src/Connectors.tsx:173; src/AuditViewer.tsx:182; src/CommsDrawer.tsx:84, 133 | The selected row, current train and selected day are marked only by `--selection` (1.16:1 light, 1.35:1 dark). | Add a cue of at least 3:1: a 3px accent inset edge, a bold name or a "▸" marker. |
| 19 | Moderate | 1.4.1 Use of Color (A) | src/Trains.tsx:162 | In the freeze footer, Freeze and Chill differ only by red vs orange text. | Prefix the kind in words ("▲ Freeze · …" / "◐ Chill · …"). |
| 20 | Moderate | 1.1.1 (A) / 1.3.1 (A) | src/LiveRun.tsx:49 | The running step shows `● 3:12` with no word. | Show "● Running 3:12". |
| 21 | Moderate | 3.3.4 Error Prevention (AA) | src/LiveRun.tsx:170-172, 228; src/Planning.tsx:253-256; src/Templates.tsx:282 | End run (aborted, rolled back, completed), step Fail, Fail gate, Decertify and Retire act on one click with no confirm or undo. | Use the inline reason + "Confirm" pattern from docs/UI.md. |
| 22 | Moderate | 3.3.2 Labels or Instructions (A) | src/LiveRun.tsx:223-224 | The requirement for a skip reason appears only in the placeholder. There is no `aria-required`. | Label it "Reason for skipping (required)" and set `aria-required`. |
| 23 | Moderate | 4.1.2 (A) | src/Governance.tsx:79-80; src/Freezes.tsx:71, 102-103; src/Closeout.tsx:72, 131, 192; src/Admin.tsx:85; src/CommLibrary.tsx:240 | Disclosure buttons (History, Record Go/No-Go, New freeze window, Attest, Edit) lack `aria-expanded` and `aria-controls`. | Add both, as Freezes.tsx:101 already does. |
| 24 | Moderate | 2.5.3 Label in Name (A) | src/AuditViewer.tsx:183 | The visible timestamp is not part of the button's `aria-label`. | Start the label with the visible text. |
| 25 | Moderate | 1.1.1 (A) / 1.3.1 (A) | src/Connectors.tsx:180 | "▲3" in the Links cell has no word. | `▲ 3 <span className="sr-only">stale</span>`. |
| 26 | Moderate | 1.3.1 (A) | src/ImportExport.tsx:195 | Old and new values are distinguished only by CSS line-through. | Use `<del>`/`<ins>` or sr-only "was … now …". |
| 27 | Moderate | 1.3.1 (A) | src/ImportExport.tsx:115, 166, 176, 183, 213; src/CommsDrawer.tsx:283, 306, 313, 328, 334, 370; src/CommLibrary.tsx:33, 77 | Visual section titles are `<div>`/`<span className="cap">`, not headings. | Use `<h2>`/`<h3 className="cap">`. |
| 28 | Moderate | 1.3.2 Meaningful Sequence (A) | src/AnalyticsScreen.tsx:159-160 | "View data" reveals a table that comes before the toggle in DOM order. | Render the table after the button, or focus it when it opens. |
| 29 | Moderate | 4.1.3 (AA) | src/AnalyticsScreen.tsx:55-65, 84 | After Apply, new KPIs and charts replace the old ones silently. | Add a polite "Loaded 13 charts for …" line. |
| 30 | Moderate | 4.1.3 (AA) | src/Evidence.tsx:56, 61, 69 | Upload and delete successes are not announced. | Add a persistent `role="status"` line. |
| 31 | Moderate | 4.1.3 (AA) | src/Admin.tsx:25-28, 56-57, 79, 114 | Save handle, Deactivate, Create team and Add holiday give no success message. | Show "✓ Saved" in a status region. |
| 32 | Moderate | 1.3.1 (A) / 4.1.2 (A) | src/Governance.tsx:83-84; src/Freezes.tsx:75 | Go / Go with conditions / No-Go and Freeze / Chill are `aria-pressed` buttons with no group name, and the glyphs are read as part of the names. | Use `role="radiogroup"` with a label, and `aria-hidden` the glyphs. |
| 33 | Minor | 1.4.10 Reflow (AA) | src/app.css:143 | `.sync-add input.line.url` is 26em wide with no max-width. | Add `max-width: 100%`. |
| 34 | Minor | 1.4.1 (A) | src/LiveRun.tsx:123, 157; src/SyncHealth.tsx:86 | The deadline countdown, variance and stale "Last success" change colour only. | Add a glyph and word ("▲ under 30 min"). |
| 35 | Minor | 1.4.3 Contrast (AA) | src/app.css:31 | `.muted` on the hover tint is 4.38:1. | Apply the label-colour override on `:hover` too. |
| 36 | Minor | 1.4.11 (AA) | src/app.css:92, 95 | The planned Gantt bar and its swatch use `--separator-strong` (1.52:1). | Use `--tertiary` (3.26:1). |
| 37 | Minor | 4.1.2 (A) | src/Planning.tsx:87, 189; src/Runbook.tsx:62; src/LiveRun.tsx:152 | `aria-selected` on `<tr>` in a non-grid table. | Use `aria-current` on the row's button. |
| 38 | Minor | 4.1.2 (A) | src/Trains.tsx:55, 158; src/CommsDrawer.tsx:143, 309, 315; src/AuditViewer.tsx:171-172 | `aria-label` on role-less span, div or pre elements is ignored. One alert is nested inside a status region. | Use sr-only text or add a role; do not nest the alert in the status. |
| 39 | Minor | 4.1.2 (A) | src/Planning.tsx:126 | The selected timeline gate exposes no state. | Add `aria-pressed` or `aria-current`. |
| 40 | Minor | 4.1.2 (A) | src/LiveRun.tsx:84 | Mode links use `href="#"`. | Use `buildPath(...)` hrefs. |
| 41 | Minor | 1.3.1 (A) | src/LiveRun.tsx:143 | Runbook section rows are `<td colSpan>`. | Use `<th scope="rowgroup">`. |
| 42 | Minor | 1.3.1 (A) | src/Planning.tsx:83, 183; src/Runbook.tsx:56; src/LiveRun.tsx:135; src/Templates.tsx:69, 89, 114, 139, 154, 169, 235 | Data tables have no caption or accessible name. | Add `aria-labelledby` pointing at the section heading. |
| 43 | Minor | 1.3.1 (A) | src/LiveRun.tsx:188, 220; src/Bulk.tsx:44, 65, 69; src/Trains.tsx:159; src/Planning.tsx:219 | Sub-headings are `<p>`, `<span>` or `<div>`. | Use `<h3 className="cap">`. |
| 44 | Minor | 1.3.5 Identify Input Purpose (AA) | src/App.tsx:123 | The sign-in email field has no `autoComplete`. | Add `autoComplete="email"`. |
| 45 | Minor | 3.3.1 Error Identification (A) | src/Runbook.tsx:29, 42-46; src/Planning.tsx:166, 176-177 | Validation errors don't name the field, and no field gets `aria-invalid`. | Name the field and set `aria-invalid` and `aria-describedby`. |
| 46 | Minor | 4.1.3 (AA) | src/Runbook.tsx:89 | Step load errors are muted text without `role="alert"`. | Use `<p className="bad" role="alert">`. |
| 47 | Minor | 4.1.3 (AA) | src/Calendar.tsx:261, 271; src/Bulk.tsx:63; src/App.tsx:77; src/SyncHealth.tsx:322; src/Freezes.tsx:85; src/Connectors.tsx:98, 136-139; src/CommsDrawer.tsx:349, 352; src/ImportExport.tsx:132, 147; src/CommLibrary.tsx:91 | `role="status"` elements are mounted together with their text, which VoiceOver often skips. "✓ Copied" changes inside the button. | Keep one status region always mounted and change only its text. |
| 48 | Minor | 4.1.3 (AA) | src/SyncBanner.tsx:68-69 | The alert text includes a failure count, so it is re-announced on every sync cycle. | Leave the count out of the alert text. |
| 49 | Minor | 4.1.3 (AA) | src/ExportsPanel.tsx:78; src/AnalyticsScreen.tsx:154 | `role="alert"` on historical failures fires many alerts when the page loads. | Alert only on new failures. |
| 50 | Minor | 4.1.3 (AA) | src/CommLibrary.tsx:32-42 | The token problem count updates while typing but is not live. | Make `.token-summary` polite and debounced. |
| 51 | Minor | 1.1.1 (A) | src/Calendar.tsx:147, 149; glyphs app-wide | Glyphs next to a word (▲ ● ◐ ✓ ✗ ‹ ›) are read aloud. | `aria-hidden="true"` on glyphs that accompany a word. |
| 52 | Minor | 1.3.1 (A) | src/Calendar.tsx:168-178 | Out-of-month days read as a bare "30" under an "October" caption. | Give each cell a full-date label. |
| 53 | Minor | 2.4.1 Bypass Blocks (A) | src/App.tsx:186-215 | There is no skip link, so about 15 toolbar controls plus the Stream sit before `<main>`. | Add "Skip to workspace". |
| 54 | Minor | 3.3.2 (A) | src/Trains.tsx:113; src/Templates.tsx:261; src/Closeout.tsx:121 | The reason an action is disabled appears only in `title`, which is unreachable on a disabled button. | Show the reason as text on the line below. |
| 55 | Minor | 1.3.1 (A) | src/Freezes.tsx:82, 132; src/Connectors.tsx:127, 135; src/CommsDrawer.tsx:346-347 | Disabled-reason text is not tied to the control. | Add `aria-describedby`, as ImportExport.tsx:156 does. |
| 56 | Minor | 1.3.1 (A) | src/SyncHealth.tsx:111 | The fingerprint is truncated, and the full value is only in `title`. | Show it in full or add Copy. |
| 57 | Minor | 1.3.1 (A) / 4.1.2 (A) | src/Admin.tsx:149-151; src/ImportExport.tsx:116, 145; src/CommsDrawer.tsx:292-296 | View switchers are marked up as `<nav>` landmarks, and the panels have no heading. | Use `role="group"` with a label (or a tablist), and give each panel a heading. |
| 58 | Minor | 1.3.1 (A) | src/CommsDrawer.tsx:55-58, 317 | Preview bullets are `<div>• …`, and the subject has no label. | Use `<ul><li>` and add "Subject:". |
| 59 | Minor | 1.4.1 (A) | src/analyticsCharts.ts:66-72 | The Median and p90 bars differ only by colour. | Add a decal or direct labels. |
| 60 | Minor | 1.4.1 (A) | src/AnalyticsScreen.tsx:97 | KPI tone is shown only by text colour. | Add a glyph and word. |
| 61 | Minor | 2.4.6 Headings and Labels (AA) | src/analyticsFindings.ts:24, 216; src/AnalyticsScreen.tsx:145 | Table headers are raw SQL names, and all chart h2s read "Loading…" while loading. | Map columns to human labels and use the subtitle while loading. |
| 62 | Minor | 1.3.1 (A) | src/analyticsFindings.ts:208, 216 | Null or zero values show as "·". | Use "—" with sr-only "none". |
| 63 | Minor | 1.3.1 (A) | src/AuditViewer.tsx:53-55 | The `+`, `−` and `~` diff markers are never explained. | Add a one-line legend. |
| 64 | Minor | 2.5.3 Label in Name (A) | src/CommSchedule.tsx:108 | "Mark sent" becomes "Mark T−3 Kickoff as sent". | Use `aria-label="Mark sent: …"`. |
| 65 | Minor | 1.3.2 / 2.2.2 (A), likely exempt | src/LiveRun.tsx:36-39, 120-124, 209-210 | Clocks tick every second with no way to pause. This is probably essential during a live run. | Optionally allow hiding the clocks. |
| 66 | Minor | best practice | src/Trains.tsx:112 | `scrollIntoView({behavior:'smooth'})` ignores reduced motion. | Use `'auto'` when `prefers-reduced-motion` matches. |
| 67 | Minor | best practice | src/SyncHealth.tsx:342; many `section aria-label` | There is an aside nested in main, and about 15 region landmarks. | Name only the sections that need to be landmarks. |

## Contrast (docs/ui/tokens.css)

| Token | Light: bg / panel / sidebar / selection | Dark: bg / panel / sidebar / selection |
|---|---|---|
| label | 16.8 / 16.3 / 15.5 / 14.5 | 15.3 / 14.4 / 12.8 / 11.3 |
| secondary | 5.07 / 4.91 / 4.66 / **4.38** | 6.48 / 6.10 / 5.42 / 4.80 |
| accent | 5.49 / 5.31 / 5.04 / 4.73 | 6.33 / 5.96 / 5.29 / 4.69 |
| ok | 5.33 / 5.16 / 4.90 / 4.60 | 7.51 / 7.07 / 6.28 / 5.56 |
| warn | 5.87 / 5.68 / 5.39 / 5.06 | 8.38 / 7.89 / 7.01 / 6.21 |
| bad | 6.04 / 5.84 / 5.54 / 5.21 | 5.98 / 5.63 / 5.00 / 4.43 (glyph only) |

**Fiserv Orange for main wording.** Exact `#FF6600` is 2.94:1 on white, which fails even the 3:1 large-text floor in light mode. The ReleaseOS design system therefore uses:
- `#E65C00` for titles 22px and larger in light mode (3.56:1);
- `#B84A00` for smaller wording in light mode (5.23:1);
- exact `#FF6600` in dark mode (5.68:1).

## Done well

- `lang="en"`, and zooming is not blocked.
- Real landmarks, and `aria-current` on the nav and the Stream.
- Choices are real `aria-pressed` buttons.
- Nearly every input is labelled.
- Errors and 409 conflicts use `role="alert"`.
- Status is mostly glyph + word + colour.
- The Calendar is a full `role="grid"` with roving focus.
- Analytics charts are `role="img"` with a finding title and a "View data" table.
- There is a global `:focus-visible` ring and a reduced-motion rule.
- Action columns have sr-only headers.
