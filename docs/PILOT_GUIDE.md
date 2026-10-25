# Pilot guide: one real release, no spreadsheet (REOS-54)

M8 is done when **the pilot RTE runs a real release without a spreadsheet and a Governance Officer accepts its evidence pack** (MILESTONES M8, REOS-54). This guide is how to run that pilot. It is for the pilot RTE, the Release Manager and the Governance Officer taking part, and for the operator who sets up the host. The software side of every step below is rehearsed automatically on each CI run by `tests/ReleaseMgmt.Api.Tests/PilotDressRehearsalTests.cs`. So if a step fails in the pilot, the cause is the release, the setup or a gap the test does not cover, never a regression nobody saw. The pilot itself, and the acceptance, are done by people and recorded in section 6.

## 1. Who does what

| Person | Role in the app | Their part |
|---|---|---|
| Pilot RTE | RTE | Creates and plans the train, runs the gates and the runbook, sends the messages, completes the train |
| Release Manager | ReleaseManager | Drafts and approves templates (never one they wrote), records the Go/No-Go decision and its conditions |
| Governance Officer | GovernanceOfficer | Certifies the Compliance gates, closes the conditions they own, holds and closes the PIR, generates and accepts the evidence pack |
| Contributors (developers, QA, ops) | Viewer | Complete the checklist tasks, runbook steps and PIR actions they own; nothing else is open to them |
| Operator | none needed | Installs and runs the host ([RUNBOOK_OPERATIONS.md](RUNBOOK_OPERATIONS.md)) |

Separation of duties is enforced: the person who certifies a Compliance gate must not have completed any of its tasks, and only a Governance Officer certifies one. Plan the task owners accordingly.

## 2. Before the pilot (operator and Release Manager, a week ahead)

1. **Host.** Install on the pilot host as in the runbook, sections 2 to 5, in the **Production** environment (never Development: it has an anonymous sign-in endpoint). Every key in the runbook's "Minimum for production" table is set. In particular, `Pdf:QuestPdfLicense` must be set, or no evidence pack can be generated.
2. **Restore drill.** Do the restore drill on the pilot host (runbook section 7). Record its time in the runbook's restore table.
3. **People.** Everyone in section 1 signs in once with the organisation identity. Check on **Admin** → Users that each person has the role in the table. A person whose group is not in `Auth:RoleMap` is refused at sign-in: fix the mapping, not the person.
4. **Calendar.** On **Admin** → Holidays, check the holidays: gate due dates count business days back from the target date.
5. **Teams.** If tasks or steps belong to a team rather than a person, create the team and its members on **Admin** → Teams. Its handle (`@payments`) is what the pasted checklist uses.
6. **Connectors.** On **Connectors**, enter the Jira and ServiceNow credentials (a read-only service account) and use **Test connection**. **Sync health** must show no open alert.
7. **Template.** On **Templates**, a Governance Officer reviews the "Standard release" template and approves it (it has Compliance gates, so a Governance Officer approves it; nobody who wrote a template approves it). It ships as a Draft with four gates (Code Freeze, QA Sign-off, Compliance Sign-off, CAB Approval) and the T-minus message plan. Change the gates first if the team's process differs: the train copies the template at creation.

## 3. Where the spreadsheet's contents go

Take the team's last release spreadsheet and check that each tab has a home before the pilot starts:

| In the spreadsheet | In the app | Entered by |
|---|---|---|
| Release name, target date, risk | **New train** (title, target date, risk tier, template) | RTE |
| Scope: products, versions, Jira project keys | Products on the train (name, version, project code) | RTE |
| Deployment window | The window on the train (start and end) | RTE |
| Change ticket fields (justification, plans, CAB date), affected CIs | Change record and affected CIs, and a link to the ServiceNow change | RTE |
| Jira fix versions | Links on the train (state shown from Jira) | RTE |
| Gate checklists, one tab per gate | Gates from the template; tasks with **Paste tasks** (section 4, step 4) | RTE |
| Cutover plan / run sheet: step, owner, planned start, duration, depends on, rollback rows | Runbook: **Add step**, dependencies, section Rollback for the rollback rows | RTE |
| Go/No-Go meeting notes and actions | **Record decision** with conditions (text, owner, expiry) | Release Manager |
| Comms tracker | The T-minus schedule from the template, sent with **Communicate** | RTE |
| "Actual start / actual end" columns on cutover night | **Live runbook**: start and done per step, by the step owner | Step owners |
| Post-release issue log | Known issues (hypercare) | RTE, Release Manager, Governance Officer |
| Lessons learned | PIR summary and actions | RTE, Governance Officer |
| Evidence folder | Attachments on the gates (locked when the gate certifies) and the evidence pack | RTE, Governance Officer |

"Without a spreadsheet" means that no spreadsheet is the record of status, owners, timings, approvals or evidence during the pilot. A CSV or XLSX **exported** from the app to share a view is fine; it is an output. If anyone had to keep or consult a spreadsheet for something, write down what and why in the pilot log (section 5). That is a gap to fix, and the acceptance cannot be ticked until it is understood.

## 4. Running the release (pilot RTE, in this order)

The order matters in four places, marked **order**.

1. **Create the train.** **New train**: title, target date, risk tier, the approved template, the window and the products. The gates, their due dates and the T-minus message plan come from the template.
2. **Change record.** Fill the change record, add the affected CIs, and link the ServiceNow change and the Jira fix versions. The links show the external state once the next sync runs.
3. **Runbook, before Code Freeze certifies (order).** Add every step with owner, planned start and duration; set dependencies; put the rollback steps in section Rollback. Certifying Code Freeze captures the baseline (products, gate dates and runbook steps), and **a baseline can never be changed**. A runbook entered later is missing from it. Check the contingency line on the runbook: below 30% of spare time before the rollback deadline is a warning (it never blocks).
4. **Checklists.** On each gate, **Paste tasks**. Lines look like `# Code Freeze`, then `- Release branches cut @dev@corp.example [Payments API]`: `@` names the owner (a team handle, a user handle or an email), `[...]` a product. The preview lists errors (unknown owner, gate or product, with the line number) and warnings (no owner, the same task twice). Fix them in the text and **Parse again**; nothing is saved until you commit.
5. **Evidence before certifying (order).** Attach the evidence to the gate **before** it certifies: certification locks the gate's files, and a locked file cannot be removed or replaced.
6. **Gates.** Start each gate; owners complete their tasks; certify the gates in order (an earlier gate must be certified first). Standard gates: the gate owner, the RTE or the Release Manager certifies. Compliance gates: a Governance Officer who completed none of their tasks. When Code Freeze and QA Sign-off are certified, advance the train to **Gated**.
7. **Messages.** Send each T-minus message when it is due, with **Communicate** (copy, email or an allowlisted webhook). Every message sent is kept word for word in the train's dispatch log, and the schedule shows which were sent late.
8. **Rehearsal (High and Very High risk trains, order).** Switch to **Rehearsal**, run the runbook including the rollback steps, end the run, then attest the rollback rehearsal citing that run. A High-risk train cannot go to Executing without the attestation.
9. **Go/No-Go (Release Manager).** Once the Compliance gates are certified, **Record decision**: Go, No-Go, or Go with conditions (each condition has an owner and an expiry). The condition's owner closes it when it is met. **An open condition past its expiry blocks Executing** (order).
10. **Executing.** The readiness line names anything still missing. Advance the train to **Executing**.
11. **The live run.** Switch to **Live runbook** and start the run. Each step's owner starts it and marks it done (or failed; a skip needs a comment). The forecast and the rollback deadline update as steps finish. If you have to roll back, run the rollback steps and **End run: rolled back**: the app then requires a PIR, due in one business day. Otherwise **End run: completed**.
12. **Complete.** **Complete…** with the close code (Successful, Successful with issues, Unsuccessful) and notes, then send the "Release complete" message. A close code other than Successful requires a PIR automatically.
13. **Hypercare.** Log known issues as they come in. Critical and High ones must be resolved or accepted (by a Release Manager or Governance Officer) before **Exit hypercare**.
14. **PIR.** For a Successful release, start the PIR by hand (the pilot needs one to review the pilot itself). Write the summary, add actions with owners and due dates, mark it held when the review has taken place, and close it when every action is done.
15. **Evidence pack (Governance Officer).** Train → **Export** → **Generate evidence pack** and **Evidence pack with files (ZIP)**. Then section 6.

## 5. Pilot log

Keep it as you go (in the REOS-54 ticket, or a page linked from it). One line per event worth remembering:

| When | What happened | What we did | Needed a spreadsheet? | Raised as |
|---|---|---|---|---|
| | | | yes / no | REOS-… |

Anything that answered "yes" in the fourth column, any step that needed the operator, and any guard message that was not clear to the person who saw it are the pilot's findings.

## 6. Governance Officer: accepting the evidence pack

Generate both forms after the PIR is closed. A pack shows what is on record at the moment it is generated; if anything changes afterwards, generate it again. Every pack generated stays on the train's Exports list with its hash and cannot be deleted.

**Integrity first.** The Exports list shows each file's SHA-256 (the full value is in the tooltip and in the download's `X-Content-SHA256` header). Compute it on the downloaded file and compare: `Get-FileHash -Algorithm SHA256 <file>` (Windows), `shasum -a 256 <file>` (macOS), `sha256sum <file>` (Linux). In the ZIP, `manifest.csv` lists every attachment with its SHA-256; hash each file under `files/` the same way and compare it with its line.

Then read the pack against this list. Each line is either satisfied or written down as a finding:

- [ ] **Header:** the right train, risk tier, target date and close code; the train version printed.
- [ ] **1 · Change record:** justification, implementation, risk and impact, backout, test and communication plans are filled; the affected CIs and products match the release.
- [ ] **2 · Approvals and certifications:** every gate is Certified (or Waived, with an approved waiver in section 4); every Compliance gate was certified by a Governance Officer; the separation-of-duties column shows no conflict.
- [ ] **3 · Go/No-Go decision:** recorded by a Release Manager before the train went to Executing; every condition has an owner and was closed before its expiry.
- [ ] **4 · Exceptions:** each freeze override, waiver and escalation has a reason and the right approver; nothing you did not expect.
- [ ] **5 · Evidence manifest:** each gate's expected evidence is present and locked, and the hashes match the ZIP.
- [ ] **6 · Runbook actuals:** every step outside Rollback is Done or Skipped (a skip with its reason); actual times against the plan; for a High-risk train, the rehearsal and the rollback attestation.
- [ ] **7 · Gate transition log:** no gate certified out of order; any decertification has a reason in the audit events.
- [ ] **8 · Audit events:** every action is by a named person in the role you expect; no shared or unexpected accounts.
- [ ] **9 · Metric snapshot:** present (it is information, not a criterion).
- [ ] **Retention:** the app keeps the generated file for `Exports:RetentionDays` (365 days). If your records policy needs longer, file the PDF and ZIP in the records system now.

**Sign-off record** (copy into the REOS-54 ticket as a comment):

| | |
|---|---|
| Train | |
| Evidence pack file name and SHA-256 | |
| Evidence ZIP file name and SHA-256 | |
| Governance Officer | |
| Decision | Accepted / Accepted with findings / Not accepted |
| Findings (Jira keys) | |
| Pilot RTE confirms the release ran without a spreadsheet | yes / no (see the pilot log) |
| Date | |

REOS-54 moves to Done when this record is on the ticket with "Accepted" (or "Accepted with findings", each finding raised) and the pilot RTE's "yes".

## 7. After the pilot

- Turn off anything the pilot tolerated: `DataProtection:AllowUnprotectedKeys=true`, if it was used, logs a warning at every start (runbook section 10).
- Raise every pilot-log finding as a ticket, and decide with the Release Manager which ones block the next release.
- Repeat the restore drill after any change to the paths or the host.
