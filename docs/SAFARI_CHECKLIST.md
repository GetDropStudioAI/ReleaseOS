# Safari checklist (REOS-152, part 2: run by a person on a Mac)

Playwright's WebKit is the Safari engine, not Safari. This list is the manual check on real Safari. It is signed off by a person; nothing here is automated.

**Setup:** a Mac with Safari, a fresh demo database (`python3 start.py`, demo seed), the app at the printed address. Sign in as the demo RTE. Do the whole list once in Light and once in Dark (System Settings, Appearance, or the account menu's theme choice).

**Record:** Safari version, macOS version, date, your name. For each line write Pass, Fail or N/A, and for a Fail what you saw (a screenshot helps).

| # | Area | What to do | Expect | Result |
|---|---|---|---|---|
| 1 | Sign-in | Sign in, sign out, sign in again | Lands on Overview; no console errors (Develop menu, Show JavaScript Console) | |
| 2 | Shell | Resize the window from wide to narrow | Sidebar and utility bar keep their sizes, wrap under 900px, nothing overlaps | |
| 3 | Releases list | Open Releases, sort, filter, open one | Rows, chips (dot plus label) and counts read correctly | |
| 4 | Release detail | Plan, Gates, Evidence tabs | Dates show UTC with local time beside them; dialogs close and focus returns to the button you clicked | |
| 5 | Orchestration | Open a Live run, pause and resume with confirmation | State follows the server; live updates arrive without reload | |
| 6 | Approvals | Open an approval, decide it | Result shown in words; the item leaves the queue | |
| 7 | Analytics | Open each tab; hover and keyboard-focus a chart | Charts draw (SVG); tooltip and the data table alternative both work | |
| 8 | Calendar | Open the calendar, copy the feed link | A visible "Copied" state appears; pasting elsewhere gives the link | |
| 9 | Downloads | Export one of each: CSV, XLSX, PDF, ICS | Each file downloads, opens, and has the right name | |
| 10 | Dates | Edit a date and time field | The field accepts input, the UTC note shows | |
| 11 | Theme | Switch Light, Dark, System | No hard-coded colours show through; text stays readable | |
| 12 | Keyboard | Tab through Overview, Releases and a dialog (turn on Safari's "Press Tab to highlight each item" in Settings, Advanced) | Every control is reachable, the focus ring is visible, Escape closes dialogs | |
| 13 | Fonts | Look at body text | System font (SF), not a substitute | |
| 14 | Reconnect | Turn Wi-Fi off for 20 seconds, then on | A reconnecting notice appears, then clears; data refreshes | |

**Sign-off:** Tester ______  Date ______  Safari ______  macOS ______  Overall: Pass / Fail

Failures become Jira bugs linked to REOS-152; the story closes when every line is Pass or N/A with a reason.
