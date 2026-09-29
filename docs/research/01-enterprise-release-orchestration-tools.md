_Research report produced 2026-09-28 by a Claude research agent for the Release Management App scope. Every claim cites a page that was actually opened; unreachable pages are flagged._


# Enterprise Release-Management / Release-Orchestration Tool Research

**Scope note on sourcing.** Every claim below is tied to a URL I opened during this session. Where a vendor's docs blocked fetching (robots.txt or JS-only pages), I say so explicitly rather than guess. Two important access limits:
- **Plutora's knowledge base (help.plutora.com)** allowed only four pages before its robots.txt began failing on every subsequent request; the gate/approval, deployment-activity, email-template, and release-form pages were all blocked. plutora.com itself now 302-redirects to planview.com/acquisitions/about-plutora/.
- **ServiceNow's documentation** (`servicenow.com/docs/<hash>` and `docs.servicenow.com/bundle/...`) is robots-disallowed; only one `/docs/r/...` page was reachable. The legacy "Release Management (ITBM/SPM)" module docs returned 404 at every path I tried; ServiceNow now markets this capability as **Digital Product Release** inside ITSM.

---

## 1. Plutora (now "Planview Release")

### 1.1 Core object model
- **Three release types**: *Enterprise Releases* "can contain project and independent releases"; *Project Releases*; *Independent Releases*. The Release Manager grid lets users "view, search and filter their Enterprise, Project, and Independent Releases," expand/collapse "child Releases of parent Enterprise Releases," and add/edit/duplicate/export/delete releases. — [Release Manager intro](https://help.plutora.com/knowledge-base/introduction-to-release-manager/)
- Releases contain **Phases** (with end dates used for highlighting), **Gates** (shown as "Traffic Light indicators"), and **Stakeholders** who can be assigned as approvers. — same page
- Vendor data sheet: "Releases are broken down into definable phases, criteria gates, milestones"; tracks "application lifecycles and project dependencies"; "Allocate features and changes to track release scope"; named modules include **Systems Impact Matrix**, **Deployment Planner & Library**, **Interactive Execution**, **Executive Reports**. — [Plutora Release data sheet (PDF)](https://433568.fs1.hubspotusercontent-na1.net/hubfs/433568/Marketing%20files/DS_Plutora_Release_20220809.pdf)
- **Release Templates**: require the "Manage Release Templates user permission"; can be favorited and toggled for use in Release Manager. The template page does not describe phase/gate nesting. — [Add/Edit Release Template](https://help.plutora.com/knowledge-base/setting-up-release-templates/)
- **Deployment Plans** (the runbook object) nest as **Master Deployment Plans** (marked "M") containing **Dependent Child Deployment Plans**, plus standalone **Independent Deployment Plans**. — [Deployment Plan Library](https://help.plutora.com/knowledge-base/introduction-to-deployment-plan-library/)
- Other objects referenced in integration config: **Change**, **TEBR** (Test Environment Booking Request), **Release**. — [ServiceNow Integration](https://help.plutora.com/knowledge-base/servicenow-integration/)
- Wikipedia-style hierarchy summary: `Enterprise Release → Project/Independent Release → Phase → Gate (criteria) → Activities`; `Release → Master Deployment Plan → Child Deployment Plan → Activities/Groups`.

### 1.2 Gate / approval mechanics
- The dedicated page "Manage Gates And Approvals In Release Manager" exists ([URL](https://help.plutora.com/knowledge-base/manage-gates-and-approvals-in-release-manager/)) but was **blocked by robots.txt on all five attempts**. What is verifiable from reachable pages:
  - Gates render as **Traffic Light indicators** on the release grid; users are assigned as "stakeholders and approvers." — [Release Manager intro](https://help.plutora.com/knowledge-base/introduction-to-release-manager/)
  - Data sheet: "Tailor criteria for gates in each release"; "Set up blackout periods for code and system freezes." — [data sheet](https://433568.fs1.hubspotusercontent-na1.net/hubfs/433568/Marketing%20files/DS_Plutora_Release_20220809.pdf)
  - Planview product page: "Automated workflows and approvals keep every team aligned"; "Ensure consistent governance and simplify approval processes." — [Planview Release](https://www.planview.com/products-solutions/products/planview-release/)
  - A Planview reseller describes "Stage gates with configurable criteria, automated checks, and manual approval workflow" and "Configurable approval chains, notifications, and escalations across teams and release stages" (third-party marketing, not vendor docs). — [Merito](https://www.merito.com/vendors/planview/release-management)
- Waivers/exceptions and evidence capture: **not verifiable** from reachable pages.

### 1.3 Runbook / cutover execution (Deployment Plans)
- Four plan modes, color-coded: **Draft** (editable), **Approved** ("waiting execution; restricted editing"), **Execution** ("in progress; restricted editing"), **Completed** (view-only, deletion restricted). Activity status colors: Blue = completed, Green = in progress, Grey = not started, **Black = overdue/incomplete**. "Granular saving" lets "multiple users [update] the same Deployment Plan" without overwriting each other. — [Deployment Plan Library](https://help.plutora.com/knowledge-base/introduction-to-deployment-plan-library/)
- Data sheet: "Set up notifications for stakeholders to let them know when tasks have begun, completed, or failed"; "Interactive Execution" listed as a feature. — [data sheet](https://433568.fs1.hubspotusercontent-na1.net/hubfs/433568/Marketing%20files/DS_Plutora_Release_20220809.pdf)
- The **Deployment Activities Customization** page ([URL](https://help.plutora.com/knowledge-base/deployment-activities/)) was blocked. Timers/countdowns/escalation: **not verifiable**.

### 1.4 Jira / ServiceNow integration
- **ServiceNow**: configured under *Settings > Customization > Integrations*. **Bi-directional**: "as items change in ServiceNow they will be synced with Plutora. This process will also happen in reverse if you click to select the **Push to ServiceNow** checkbox." Sync is **polling**: "Set the sync rate by selecting a time (in seconds) from **Period (seconds)**. (Mandatory field.)" Tabs for **Release, Change, and TEBR** auto-sync; per-table settings include "Custom Filter as a ServiceNow query string," "Limit of Retrieved Records," "Filter By Last Update Date Range," and **Expression Builder (Pull) / Expression Builder (Push)** for field mapping. Basic auth only ("OAuth is not currently supported"); requires the **Plutora Integration Adaptor** to be online; supports ServiceNow Eureka and later. — [ServiceNow Integration](https://help.plutora.com/knowledge-base/servicenow-integration/)
- **Sync-failure surfacing**: only a **Test Connection** button with "green ticks" or a "red cross" indicating issues with "proxy, username, password, or URL" or the adaptor being offline. No ongoing sync-health dashboard is documented on that page. — same
- **Jira**: no reachable vendor doc. Atlassian Community: "The Atlassian marketplace has nothing for Plutora, so you'll probably need to write something." — [Atlassian Community thread](https://community.atlassian.com/forums/Jira-questions/Integration-of-JIRA-with-Plutora/qaq-p/909452). A customer post-mortem: "the initial integration with Jira wasn't as straightforward as expected," set up only at "MVP level, leaving gaps in data flow between the two systems," forcing "considerable time manually managing the integration, troubleshooting data discrepancies." — [Apwide](https://www.apwide.com/release-management-investment-in-plutora/)
- github.com/plutora/jira returned 404.

### 1.5 Communication / notifications
- An **Email Template Wizard Customization** page exists ([URL](https://help.plutora.com/knowledge-base/email-template-wizard/)) but was blocked. Data sheet confirms stakeholder notifications on task begin/complete/fail and "automated report publishing, canned and custom." — [data sheet](https://433568.fs1.hubspotusercontent-na1.net/hubfs/433568/Marketing%20files/DS_Plutora_Release_20220809.pdf)

### 1.6 Reporting / analytics
- Data sheet: consolidated **Release Calendar**; "See how releases are tracking across each phase. Drill down for details on systems and status"; "heat map of systems impacted by each release"; Executive Reports. — [data sheet](https://433568.fs1.hubspotusercontent-na1.net/hubfs/433568/Marketing%20files/DS_Plutora_Release_20220809.pdf)
- Planview: "real-time visibility into your entire release calendar including blackout periods"; "a comprehensive view of release progress, status, quality, automation and overall health." — [Planview Release](https://www.planview.com/products-solutions/products/planview-release/)
- Value-stream/flow metrics were a separate product ("Plutora Analytics," now Planview Verify). A DevOps.com piece mentions a revamped "release insights dashboard" surfacing "problem areas and hot spots" and "value stream flow metrics" but names no specific metrics. — [DevOps.com](https://devops.com/plutora-enhances-vsm-platform-with-release-insights/)
- Named DORA metrics: **not verified** for the Release product.

### 1.7 Import / export
- Release grid supports "exporting" releases. — [Release Manager intro](https://help.plutora.com/knowledge-base/introduction-to-release-manager/)
- REST API: Swagger UIs exist at usapi.plutora.com / auapi.plutora.com (the page was JS-only and returned no endpoint list to the fetcher). Format-level detail (CSV/PDF/Excel) **not verifiable**.

### 1.8 Known complaints
- "The UI is not very appealing and graphics is not of optimal standard." (Information Services, Dec 2018) and "challenging for individuals who are not part of the IT profession" (Network Engineer, Jun 2024). G2 rating 4.0/5, **2 reviews**. — [G2 Planview Release](https://www.g2.com/products/planview-release/reviews)
- Sister product (Planview Verify / Plutora VSM, 4.7/5, 7 reviews): "loading data in its particular format takes a lot of work"; "lot of efforts required to load information in this software specified format"; "Plutora can be difficult to set up and use at first… customer service is slow." — [G2 Planview Verify](https://www.g2.com/products/plutora-value-stream-management/reviews)
- Customer post-mortem: "$100k for just five seats" in year one, "limited professional service hours for setup and training," and when budgets tightened "Plutora was one of the first tools to go." — [Apwide](https://www.apwide.com/release-management-investment-in-plutora/)
- Review volume is thin: Gartner Peer Insights 4.0/5 with **1 rating** ([Gartner](https://www.gartner.com/reviews/product/plutora-release-management)); Capterra 0 reviews ([Capterra](https://www.capterra.com/p/128571/Plutora/)); PeerSpot 0 reviews ([PeerSpot](https://www.peerspot.com/products/plutora-reviews)).

### 1.9 Pricing
- Contact vendor; no public price ([TrustRadius](https://www.trustradius.com/products/plutora/pricing), [Capterra](https://www.capterra.com/p/128571/Plutora/)). Only anecdote: "$100k for just five seats" ([Apwide](https://www.apwide.com/release-management-investment-in-plutora/)). KnowledgeHut notes "No complimentary or trial version offered." — [KnowledgeHut](https://www.knowledgehut.com/blog/it-service-management/release-management-tools)

---

## 2. Digital.ai Release (formerly XebiaLabs XL Release)

### 2.1 Core object model
- Hierarchy: **Folder → Release → Phase → Task**. "Phases in a template or release represent blocks of activities that occur in succession." Phases are sequentially ordered, have color, start date, due date, duration. Tasks are "the actionable items within each phase." — [Phases and Tasks](https://docs.digital.ai/release/docs/how-to/phases-and-tasks)
- **Task types**: Manual, Notification, User Input, Gate, Script (Jython), Parallel group, Sequential group, Create Release, XL Deploy task, plus plugin tasks. **Task fields**: Title/Description (Markdown), Assigned to (user and/or team), Scheduled start date, Due date, Duration, **Precondition** ("if" statement, Boolean or Jython), Status flags, Attachments. — same
- **Release properties**: Release Name, Description, Release Owner, Tags, Start/Due Date, Auto-Start checkbox, **Risk Profile**, **Flag Status** (yellow "attention needed" / red "at-risk" with message), **Abort on Failure**, Run Automated Tasks As User, Attachments, Release Variables, Calendar Publishing (Outlook/iCal/ICS). — [Release Properties](https://docs.digital.ai/release/docs/24.1/how-to/configure-release-properties)
- **Release Groups**: "group releases together and manage a collection of releases as one group"; "Releases can be added to multiple release groups, and into multiple folders." Group statuses: Planned, In process, Paused, Failing, Failed, Aborted, Completed. — [Release Groups](https://docs.digital.ai/release/docs/how-to/groups)
- **Software Delivery** (the closest "release train" construct): a **Delivery** is a reusable pattern of **Stages** and **Transitions** through which **Tracked Items** move; deliveries "prevent releases from going forward until the tracked items they contain are completed." Six delivery task types: Create Delivery, Find or Create Delivery, Mark Tracked Items, Register Tracked Items, Wait for Tracked Items, Wait for Stage. — [Software Delivery](https://docs.digital.ai/release/docs/how-to/introduction-software-delivery)
- Parent/child via **Create Release** task, now with a "Wait for Release to Finish" option. — [25.1 release notes](https://docs.digital.ai/release/docs/25.1/release-notes/release-notes-release)

### 2.2 Gate / approval mechanics
- **Gate task** "contain[s] conditions that must be fulfilled before the release can continue." Two condition types: **checkbox conditions** ("Add condition"), which must be ticked by release team members, and **dependencies** on other releases/phases/tasks. Since 9.7.0 gates **auto-complete** when all checkboxes are ticked, with a grace period (default 5 min) and check interval (default 10 min). A gate with only dependencies completes when all dependents finish. If a dependency fails the gate enters **Failing**; with "Fail if any dependency fails" it fails immediately and needs manual retry or skip. Dependencies can be variable-based ("Switch to variable"). — [Gate Tasks](https://docs.digital.ai/release/docs/how-to/create-a-gate-task)
- **Task lifecycle** states: Planned → Pending / Ask for Input / In Progress / Failing → Completed / Skipped / Failed / Failure Handler in Progress. Users can **Complete, Skip, or Fail** manually; failed tasks can be restarted or skipped; tasks can be pre-completed/pre-skipped. — [Task Life Cycle](https://docs.digital.ai/release/docs/concept/task-life-cycle)
- **Waiver equivalent** = "Skip" (a distinct terminal state from Completed). Evidence capture = task **Attachments** and comments (32,768-char limit) — [Phases and Tasks](https://docs.digital.ai/release/docs/how-to/phases-and-tasks). Formal "waiver" objects: not documented.
- **Blackout periods**: global; tasks configured to postpone get rescheduled to one minute after the blackout ends; users with Admin or "Edit Task Blackout" permission can override and start a task immediately. Shown "as a black marked area" in the calendar. — [Set Blackout Period](https://docs.digital.ai/release/docs/22.1/how-to/set-blackout-period)
- 25.1 added an explicit **Failing** status on gates when dependencies fail (previously stuck at In Progress). — [25.1 notes](https://docs.digital.ai/release/docs/25.1/release-notes/release-notes-release)

### 2.3 Runbook / cutover execution
- Default durations: 1 hour for manual tasks, 1 minute for automated tasks. Release planner shows **calculated dates** in italic gray, **manually set dates** in black, **actual times** in gray; "Release automatically adjusts other phases and calculates the release duration and end date." Drag phase/task edges to reschedule. Tasks can wait for their scheduled start or start when the flow reaches them. — [Scheduling Releases](https://docs.digital.ai/release/docs/how-to/scheduling-releases)
- Release overview shows Risk, Title, Status, Start/End date (**overdue dates in red**), Duration, Completion %. Risk filter values: **On Track / At Risk / Attention Needed**. — [Release Overview](https://docs.digital.ai/release/docs/how-to/using-the-release-overview)
- Task Overview: filters "My tasks / Teams tasks / All tasks / Unassigned tasks," status filters Failed/In Progress/Pending/Planned; default sort by due date; actions Complete/Skip/Fail/Abort/Assign. No escalation feature documented. — [Task Overview](https://docs.digital.ai/release/docs/how-to/using-the-task-overview)
- Timer/escalation: notifications fire **"Task due soon" at 25% remaining duration** and **"Task overdue"** to owner and admin — [Notifications](https://docs.digital.ai/release/docs/concept/notifications-in-xl-release). No live countdown widget documented. Flow editor docs describe structure only, not live timing. — [Release Flow Editor](https://docs.digital.ai/release/docs/how-to/using-the-release-flow-editor)

### 2.4 Jira / ServiceNow integration
- **Jira plugin** (task-based, bi-directional read/write): Jira: Create Issue, Create Issue Json, Create Subtask, Create Version, Query (JQL), Query For Issue Ids, Update Issue, Update Issues, Update Issues By Query, **Check Issue** and **Check Query** (both **poll** until "Expected Status List" reached, with a configurable **Poll Interval**), Get Versions, Get Issue Details, Get All Sprints, Issue Trigger, Update Labels, Update Fix Versions. No webhook support documented; failures surface as task failure. — [Jira Tasks Plugin](https://docs.digital.ai/release/docs/how-to/jira-plugin). 26.3 moved Jira Cloud to API v3 with ADF rich-text; 25.1 migrated off the deprecated `/rest/api/2|3/search` endpoints. — [26.3 notes](https://docs.digital.ai/release/docs/release-notes/release-notes-release), [25.1 notes](https://docs.digital.ai/release/docs/25.1/release-notes/release-notes-release)
- **ServiceNow plugin**: 30+ task types (23 ITSM incl. Create/Update/Find Change Request, Change Task, Incident, CMDB; 12 Agile/Scrum). **Wait for Status** uses server-configured polling with two progression modes: **exponential backoff** or a **custom list** such as `"10,300 5,900"` (10 tries at 300 s, then 5 at 900 s); "Override Server Wait Config" per task. Outputs `Data`, `Status`, `Number`, `Num Tries`. Basic or OAuth auth; 26.3 adds PAT auth. — [ServiceNow Plugin](https://docs.digital.ai/release/docs/how-to/servicenow-plugin)
- **ServiceNow App integration** (reverse direction): ServiceNow calls the Release REST API (`api/v1/`), pulls templates, passes variables, and when a change is approved, **gate tasks tagged `snow_approved` are automatically completed**. No error-handling documented. — [ServiceNow App Integration](https://docs.digital.ai/release/docs/how-to/xl-release-integration-with-servicenow-app)
- **Sync-health view**: none. Failures are per-task (task goes to Failed, failure handler optional).

### 2.5 Communication / notifications
- Email events: Active task assigned/unassigned, Manual task started, **Task due soon (25% remaining)**, **Task overdue**, Task failed, Task waiting for input, Task flagged, Comment added, Release started/completed/failed/failing/aborted/flagged, @mentions, audit-report job status. — [Notifications](https://docs.digital.ai/release/docs/concept/notifications-in-xl-release)
- Recipient roles per event: **Release Admin, Task Owner, Task Team, Watcher**; falls back to Task Team if no owner. Admins edit templates (priority, subject, Markdown body) with `${release.*}`, `${task.*}`, `${comment.*}` placeholders. — [Notification Settings](https://docs.digital.ai/release/docs/how-to/configure-notification-settings)
- **Notification task** type sends ad-hoc messages from the flow. — [Phases and Tasks](https://docs.digital.ai/release/docs/how-to/phases-and-tasks)
- **Slack plugin** and (new in 25.1) **Microsoft Teams plugin** send "notifications to specific Teams channels… individual users… Teams groups." — [25.1 notes](https://docs.digital.ai/release/docs/25.1/release-notes/release-notes-release) (direct plugin doc page 404'd)

### 2.6 Reporting / analytics
- **Calendar view**: day/week/month/year; status badges (Blue in progress, Green completed, Orange failing, Red failed); filters by owner, status, tags, flagged, risk; **export to Excel**; highlight special days; blackout overlay. — [Calendar View](https://docs.digital.ai/release/docs/how-to/using-the-calendar-view)
- **Dashboards**: Release Dashboard is tile-based, **Export as PDF** ([Release Dashboard](https://docs.digital.ai/release/docs/how-to/using-the-release-dashboard)); dashboard templates = Blank, Deployment, **Release Statistics** ([Dashboard Templates](https://docs.digital.ai/release/docs/saas/concept/dashboard-templates)); home page tiles = Releases (with overdue in red), My Tasks, Templates, Explore Workflows, max 20 tiles ([Home Page](https://docs.digital.ai/release/docs/how-to/new-home-page-dashboard)).
- **DORA**: marketing lists Lead Time for Changes, Deployment Frequency, Mean Time to Recovery, Change Failure Rate ([DORA page](https://digital.ai/products/release/dora-metrics/)); the MCP server doc shows a prompt to "retrieve the DORA metrics from a selection of recently completed releases" ([MCP examples](https://docs.digital.ai/release/docs/how-to/release-mcp-server-usage-examples)). **Caveat**: 26.3 notes say "Analytics removal from the Premium Edition represents a strategic shift toward AI-based insights." — [26.3 notes](https://docs.digital.ai/release/docs/release-notes/release-notes-release). The dedicated DORA dashboard doc pages 404'd.
- Named release metrics on grids: Risk, Status, Duration, Completion %, overdue. Gate cycle time / on-time %: not named.

### 2.7 Import / export
- **Release Audit Report** as **Excel (.xlsx)** per release (Release flow → Export → "Release audit report (Excel)") or bulk zip filtered by folder/tags/title/change number/application/environment, emailed as a download link; API `/api/v1/reports/download/{reportType}/{releaseId}`. Also a **User Permissions Report** (Excel). — [Release Audit Report](https://docs.digital.ai/release/docs/how-to/generate-release-audit-report)
- Calendar → Excel; releases → `.ics` ([Release Overview](https://docs.digital.ai/release/docs/how-to/using-the-release-overview)); dashboard → PDF; templates import/export as **YAML "template as-code" or .zip** ([25.1 notes](https://docs.digital.ai/release/docs/25.1/release-notes/release-notes-release)); full REST API and an **MCP server** ([MCP examples](https://docs.digital.ai/release/docs/how-to/release-mcp-server-usage-examples)).

### 2.8 Known complaints
- "Too much space occupied by the task boxes, better we had a workflow like Flow diagram" (Lead DevOps Consultant, Dec 2021); "XL release can have task for checking Service now change and approval but does not provide a way to actually verify if there was a change or not" (IT Analyst, Aug 2019); "analyzing the root of such problems is not easy" (CDO, Jan 2025). G2 4.0/5, 3 reviews. — [G2](https://www.g2.com/products/digital-ai-release/reviews)
- "Code versioning of templates is very difficult"; "Pagination of data – across tool"; "Futures Timeout Issues"; "Dependency on Universal template/custom plugins creation should be reduced" (Team Lead, Apr 2022); "XL release is really only missing a consolidated calendar view" (2017). — [TrustRadius](https://www.trustradius.com/products/digital-ai-release/reviews)
- UI "feels a bit old and not very appealing"; "too many tasks to get done" before starting a release; form data erases on back-navigation; licenses "expensive," "not ideal for small teams," six-figure annual estimates (all Jun 2026). — [PeerSpot](https://www.peerspot.com/products/digital-ai-release-reviews)
- "Great Release Orchestration tool but not much support"; "Developing plugin is not straight forward." Gartner 4.3/5, 47 ratings. — [Gartner PI](https://www.gartner.com/reviews/product/digitalai-release)
- "a few instances of memory leaks"; support team needs strengthening (Dec 2022). — [Capterra](https://www.capterra.com/p/238299/Digitalai-Release/)

### 2.9 Pricing
- Not public ([TrustRadius](https://www.trustradius.com/products/digital-ai-release/pricing), [G2 pricing](https://www.g2.com/products/digital-ai-release/pricing), [Capterra](https://www.capterra.com/p/238299/Digitalai-Release/)). Reviewers cite enterprise licenses as "expensive" with "six-figure annual costs based on user counts." — [PeerSpot](https://www.peerspot.com/products/digital-ai-release-reviews)

---

## 3. Cutover

### 3.1 Core object model
- **Event → Runbook → Stream (→ Sub Stream) → Task**, with **Teams**/users assigned to tasks and **Templates** as reusable runbook blueprints; workspace folders group runbooks. "A runbook chronologically details all of the actions — referred to as tasks." A stream is "A group of specific, categorized tasks within a runbook." — [Platform Overview](https://help.cutover.com/en/articles/3838588-platform-overview-and-fundamentals)
- Task attributes: Task ID, name, task type (icon shape), stream (icon color), expected duration, teams/users, start timestamp, dependencies → **critical path** ("the longest duration of a path and sequence of tasks"), shown as an orange line in the Nodemap. — [Runbook Overview](https://help.cutover.com/en/articles/5850401-runbook-overview)
- Task fields on create: Title, Description, Stream, Task Type, Duration (`1d2h20m`), Level, Assigned Users/Teams, Predecessors, Fixed Start Time, Due Date, Scheduled Auto Finish, Custom Fields, **"Minimum users to start/complete,"** Auto-start. — [Add tasks](https://help.cutover.com/en/articles/2678639-add-tasks-to-a-runbook)
- Runbook fields: Name, Folder, Timezone, Start/End, **Planned End** (auto-calculated), **RAG Status**, Auto Start, Timing Mode, webhook URLs for incoming runbooks. — [Manage Runbook Details](https://help.cutover.com/en/articles/2678627-manage-runbook-details)
- **Task types**: Normal (2 clicks), Validation (pass/fail/not tested → Test Summary), Milestone (1 click, zero duration), Checklist, Email, SMS, Call, **Branching**, **Linked** (child runbooks), **Integration**, Custom, Snippet. — [Task Types](https://help.cutover.com/en/articles/3849281-task-types)
- **Linked runbooks** (train equivalent): parent runbook's linked task drives child; "a linked runbook cannot begin a run without the parent runbook first initiating the run"; child timing "is dictated by the linked task in the parent runbook." — [Linked Task Types](https://help.cutover.com/en/articles/6906272-use-linked-task-types-in-planning-mode)

### 3.2 Gate / approval mechanics
- No formal "gate" object. Gating is achieved by: (a) **Fixed Start Time** locks ("locked until the specified date and time arrive, even if all predecessors are complete"; only Runbook Admins can override via the clock icon) — [Fixed Start Time](https://help.cutover.com/en/articles/6136290-fixed-start-time); (b) **Minimum users to start/complete** a task — [Add tasks](https://help.cutover.com/en/articles/2678639-add-tasks-to-a-runbook); (c) Validation tasks capturing pass/fail with notes — [Task Types](https://help.cutover.com/en/articles/3849281-task-types); (d) Integration tasks that poll an external approval (e.g., ServiceNow change task state) — [ServiceNow change task example](https://developer.cutover.com/example-integrations/servicenow-integration-attach-a-change-task-and-track-status).
- **Template approval workflow** (governance of the plan, not the run): statuses **Draft, Awaiting Review, Approved, Approved (Pending Re-review), Rejected, Pending**; only users with the **Workspace Template Reviewer** role can approve; rejection requires a comment; email notifications to approvers/creators; default 12-month review cycle; "Edits cannot be made to a template after it has been approved or rejected." — [Template advanced workflow](https://help.cutover.com/en/articles/6680773-create-a-template-advanced-workflow)
- **Waiver equivalent**: Admins can **skip** tasks at any stage "entering required comments visible in task listings." — [Execute a Runbook](https://help.cutover.com/en/articles/3850249-execute-a-runbook)

### 3.3 Runbook / cutover execution (Cutover's core strength)
- Modes: **Rehearsal** (repeatable) and **Live** (completed tasks locked). System "recalculates downstream task timing whenever a task starts or completes," showing "forecast deviation from the planned completion time…in brackets." **Late indicators escalate**: warning after 5 minutes of delay, growing "up to 1 hour of delay"; delays classified as overrun, delayed start, or pushed by predecessor. Pause returns to planning mode; cancel requires duplication. — [Execute a Runbook](https://help.cutover.com/en/articles/3850249-execute-a-runbook)
- Fixed-start "F" marker turns **orange** when pushed into the past, reverts to black when caught up; due-date-only tasks don't affect forecast end. — [Fixed Start/End](https://help.cutover.com/en/articles/3850617-fixed-start-and-fixed-end-tasks)
- **Auto Start** at scheduled time; if blocked, "the system will reattempt to start for up to 7 days." Planned End cannot be edited manually. — [Schedule Runbooks](https://help.cutover.com/en/articles/4470049-schedule-runbooks)
- Live dashboard (see 3.6) shows start delta, forecast finish delta, burn-up chart, per-stream deltas. No explicit countdown timer widget is documented, but forecast/delta fields serve the purpose.

### 3.4 Jira / ServiceNow integration
- **Integration framework**: custom HTTP integrations with a Request tab, response JSON-path mapping, a **Polling tab** "to configure polling settings for monitoring specific API conditions," and **Event Integrations** (outbound webhooks on task finish/skip/abandon, per 2026.18 notes). — [Create an integration](https://developer.cutover.com/create-an-integration), [Release Notes](https://help.cutover.com/en/articles/3839265-release-notes)
- **ServiceNow**: Create Change Request example POSTs to `/api/now/table/change_request` with `"short_description":"{{CustomField['Short Description']}}"` and maps `result.number` back to a custom field; "finish task on success" option. — [Create CR example](https://developer.cutover.com/example-integrations/servicenow-change-request). Change-task tracking is **polling** ("Tick 'Use Polling'… poll for the status every 10 seconds" until state = closed), then "trigger[s] any dependent Cutover tasks." — [Track change task](https://developer.cutover.com/example-integrations/servicenow-integration-attach-a-change-task-and-track-status). Marketing claims bi-directional fields and auto-generated runbooks from tickets. — [ServiceNow integration page](https://cutover.com/integrations/servicenow)
- **Jira**: **bi-directional** via "Atlassian API webhooks and AWS Lambdas"; create a Jira ticket from a task, status "automatically updated" in Cutover, populate custom fields; requires admin in both systems and CSM/partner engineers for bespoke fields. — [Jira integration](https://cutover.com/integrations/jira-to-cutover-integration). Jira Automation → `POST /core/runbook` with Bearer token to create runbooks from issues. — [Jira runbook automation](https://developer.cutover.com/api-use-cases/use-jira-to-automate-the-creation-of-a-runbook)
- **Sync-failure surfacing**: 2026.12 added "Message log filtering for Event Integrations by status, outcome, and specific integration"; 2026.16 "Improved AI failure notifications." Per-task integration failure semantics are not spelled out in the pages opened. — [Release Notes](https://help.cutover.com/en/articles/3839265-release-notes)

### 3.5 Communication / notifications
- **Communication task types**: Email, SMS, Call (up to 1600 chars); "Send A Test Message" and rehearsal "Test Comms only" mode; sent automatically "once the runbook is executed and the task has started"; recipients editable until the task starts. — [Communication Task Types](https://help.cutover.com/en/articles/3850216-communication-task-types)
- **Automated notifications**: runbook start (checkbox "Notify participants of runbook start?"), task start (when predecessors finish), runbook paused/resumed/cancelled with custom message; channels **email and SMS**. — [Automated Notifications](https://help.cutover.com/en/articles/5850522-automated-notifications)
- Slack via example integration "Post to channel." — [Example integrations](https://developer.cutover.com/example-integrations)
- Templated stakeholder comms with merge fields: not documented beyond dynamic `{{…}}` variables (e.g., `{{Runbook.linked_parent_runbook_id}}`). — [Release Notes](https://help.cutover.com/en/articles/3839265-release-notes)

### 3.6 Reporting / analytics
- **Single Runbook Dashboard**: RAG status + message; Task Completion Summary (start delta, forecast finish delta, duration); Completion by Stage (Completed/Abandoned/In Progress/Startable/Not Yet Startable, **late counts per status**); **Burn-up chart** (planned gray vs actual blue); Stream Summary with deltas; Milestone list; Startable and In-Progress lists with forecasts; starred Comments. Views: Exec Summary, Linked Runbooks, Test Summary. — [Single Runbook Dashboard](https://help.cutover.com/en/articles/9353643-single-runbook-dashboard)
- **Multi-Runbook Dashboard** widgets: **Lateness**, RAG Status, Rehearsal Count, Runbook Size/Stage Summary, Runbook Completion Matrix, Runbook Volume Over Time, Task/Stream Completion, **RTO Status** (Pass/Pending/Invalid/Fail), average runbook duration, longest tasks; filter by folder, type, team, stream, event, test result, custom fields. — [Multi-Runbook Dashboard](https://help.cutover.com/en/articles/3850679-multi-runbook-dashboard-overview)
- Views: List, **Timeline** (calendar of runbooks; templates excluded), Table, Nodemap, Dashboard (exportable). — [Runbook Views](https://help.cutover.com/en/articles/3850029-runbook-views)
- DORA metrics: **none** documented.

### 3.7 Import / export
- Export runbook or tables as **CSV / Excel / PDF** with timezone selection; CSV import (max 100 columns, UTF-8, no linked tasks/dependencies via CSV, auto-creates teams). — [Export and Import Tasks](https://help.cutover.com/en/articles/3850069-export-and-import-tasks)
- CSV template columns: Task ID, Title, Description, Stream, Sub Stream, Task Type, Task Level, Predecessor IDs, Planned Duration (dd:hh:mm), Planned Fixed Date/Time, Planned due date/time, Assigned Team, custom fields. — [Task Upload Template](https://help.cutover.com/en/articles/6171869-task-upload-template-guide)
- Public API (`Core-Url` header now mandatory) — [Release Notes](https://help.cutover.com/en/articles/3839265-release-notes)

### 3.8 Known complaints
- G2 4.3/5, 30 reviews. Themes: "Expensive, especially for larger organizations due to high subscription and integration costs" (5 reviews); "Limited customization options, especially regarding reporting and third-party tool integrations" (5); "Integration issues… with smaller third-party tools" (4); "Difficult learning curve" (3). Quotes: "The Pricing could be better" (PM, Mar 2023); "Self-service integration capabilities have not always been as turnkey as expected" (Healthcare Analyst, Jul 2026). — [G2 Cutover](https://g2.com/products/cutover/reviews)
- "Integration with on prem services is more complicated." (Head of IT Resilience, Banking, Feb 2024). Gartner 5.0/5, 3 ratings. — [Gartner PI](https://www.gartner.com/reviews/product/cutover-collaborative-automation-platform)

### 3.9 Pricing
- Quote-based on team size, usage/integrations, and advanced functionality. — [Cutover pricing](https://cutover.com/pricing)
- AWS Marketplace "Starter Instance": **$1,850/month** for 10 registered users, 20 runbooks, 3 integrations, 1,000 API calls. — [AWS Marketplace](https://aws.amazon.com/marketplace/pp/prodview-dvsk2b6mu5vkm)

---

## 4. Enov8 Release Manager (Environment & Release Manager / "EcoSystem ERM")

### 4.1 Core object model
- **Program → Release → Project → Activities/Milestones/Gates**, plus **Implementation Plan → Task**. Release fields: Release (short name), Summary, **Type (Enterprise, Divisional, Infrastructure, Agile)**, RAG Status, Status, Start/End Date, Program, Release Manager. Project statuses: In-Scope, Draft, Submitted, De-Scoped, Rejected, Closed. **Release Milestone/Gate** = "A group of activities (e.g. Phase and Milestones)." Activity fields: Name, Type, Timeline, RAG, Assigned To, Environment Group. — [Release Management Overview](https://docs.enov8.com/docs/enov8-platform/release-modules/release-train-management/)
- Projects define systems involved, request environment bookings, and hold **WorkItems** ("a feature, enhancement or a bug"); gates carry plan name, project, RAG, assigned groups, completion status. — [Project Management](https://docs.enov8.com/docs/enov8-platform/release-modules/project-management/)
- **Agile Release Trains / PI planning** are first-class marketing concepts: "Establish Agile Release Trains (ARTs)"; **Release Board** shows "RAG Status, Go Live Date, Number of Product Teams, Systems Impacted, Work Items, Total Booking, Events, and Test Completion Status"; **Release Fact Sheets**. — [Enterprise Release Management](https://www.enov8.com/enterprise-release-management/)
- Environment-side objects: Environment Groups, Systems, Instances, Components, Microservices; **Events** (eight types incl. Environment Release, Environment Freeze, Blockout Period) with **Runsheet templates/instances**. — [Event Management](https://docs.enov8.com/docs/enov8-platform/environment-modules/event-management/)
- REST resources confirm the model: Release, Project, Activity, **Gate**, PIR, Runsheet, Task, Booking, EnvEvent, SystemInstance, LeanSR, etc. — [REST API](https://docs.enov8.com/docs/enov8-platform/rest-api/)

### 4.2 Gate / approval mechanics
- **Implementation Plan approval**: fields Plan name, Release, Assigned To, Status, **Approvers**; workflow **Draft → Submitted → Approved**; setting Submitted "triggers approval emails automatically" with "Approve or Reject button directly in the email notification"; "Implementation plans can only be created or updated in Draft or Submitted status"; approved plans needing change "must go through the approval process again." Email can be regenerated/resent. — [Release Management Overview](https://docs.enov8.com/docs/enov8-platform/release-modules/release-train-management/)
- Runsheet task types include **Approval** (plus Manual, Automated, Notification, Hybrid) with approvers per task. — [Event Management](https://docs.enov8.com/docs/enov8-platform/environment-modules/event-management/)
- Gates/milestones are RAG-tracked groups of activities; blocking semantics (hard stop vs indicator) are **not stated**; waivers/evidence not documented.

### 4.3 Runbook / cutover execution
- **Implementation Plan tasks**: Task Name, Type, Status, Task Group, Description, Timeline, Environment Instance, Version, Assigned To; "manual or automated," "connected to the DevOps library to integrate with third party products"; bulk CSV upload or import from existing plan. — [Release Management Overview](https://docs.enov8.com/docs/enov8-platform/release-modules/release-train-management/)
- **Runsheets** execute against Events with task dependencies, scripts with parameters, notification recipients, approvers, and "Kanban-style status management." Reports: Runsheet Dashboard, Runsheet Activity. — [Event Management](https://docs.enov8.com/docs/enov8-platform/environment-modules/event-management/)
- Marketing: "scale to 1000s of tasks"; "Implementation Plan Dashboard" for command-center visibility. — [Product Overview](https://www.enov8.com/product-overview/)
- Timers/countdown/late escalation: **not documented**. Orchestration module gives scheduled/immediate script execution, log retention, execution results. — [Orchestration Management](https://docs.enov8.com/docs/enov8-platform/orchestration-management/)

### 4.4 Jira / ServiceNow integration
- **Jira → Enov8, one-way, webhook-based**: "leverages JIRA webhooks to automatically send information back to the Enov8 platform"; maps issues into **LeanSR** (service requests) via a `config.json` in a webhook-listener script (e.g., `issue.fields.summary` → Summary; type transform "Task:EnvIncident|Bug:EnvBug"). No error/log view documented. — [Jira integration](https://docs.enov8.com/docs/enov8-platform/integrations/ITSM/jira/)
- **ServiceNow → Enov8, one-way, webhook-based**: ServiceNow Business Rule + `RESTMessageV2` POSTs Change Requests and Incidents on create/update; payload includes sys_id, number, short_description, state, priority, impact, assignment_group, assigned_to. Errors are visible only on the **ServiceNow** side ("System Logs → All" / "Outbound HTTP Requests"). — [ServiceNow integration](https://docs.enov8.com/docs/enov8-platform/integrations/ITSM/servicenow/)
- **Jenkins → Enov8**: HTTP PUT to `/ecosystem/api/environmentinstance` to record deployed version; docs recommend adding "retry or failure-handling logic." — [Jenkins](https://docs.enov8.com/docs/enov8-platform/integrations/Build & Deploy/jenkins/)
- Dashboards have "Third Party" widgets for JIRA, ServiceNow, Jenkins. — [Custom Dashboards](https://docs.enov8.com/docs/enov8-platform/custom-dashboard/)
- No sync-health view documented on the Enov8 side.

### 4.5 Communication / notifications
- Object-level email notifications via **"Assigned To"** (edit rights) and **"Notification"** (inform-only) properties; SMTP config; enable per class under "Notifications > Mail Class"; user opt-out. — [Product Administration](https://docs.enov8.com/docs/enov8-platform/product-administration/)
- **Microsoft Teams** via Webhook Management (trigger class + action `objectCreate` / `ObjectUpdate` / `PropertyUpdate`) rendering **Adaptive Cards** with a `config.json` template (title field, status→color mapping Attention/Good/Warning, facts list). — [MS Teams](https://docs.enov8.com/docs/enov8-platform/integrations/Communication/msteam/)
- Runsheet **Notification** task type; event-creation emails to assigned groups. — [Event Management](https://docs.enov8.com/docs/enov8-platform/environment-modules/event-management/)

### 4.6 Reporting / analytics
- Named reports: **Release Board**, **Program Dashboard**, **Release Dashboard**, **Release Activity**, **Program Insights**, **Release Insights**, **Release Master Plan** (Gantt + table), **Release Milestone Tracker**, **Release Calendar** (Day/Week/Month/Grid, env/system filters, quick editor), **Release Demand** (heat map of system contention), **Release Environment View** (Gantt of releases/projects/bookings/events). — [Release Management Overview](https://docs.enov8.com/docs/enov8-platform/release-modules/release-train-management/)
- **Custom Dashboards / Information Walls** with widget categories (Booking 15, CMDB 20, EnvEvent 10, Data Compliance 8, Custom 13, Third Party, Workitem, Project…) and a **Report Hub**. — [Custom Dashboards](https://docs.enov8.com/docs/enov8-platform/custom-dashboard/)
- DORA in product: not documented. Enov8's own blog lists 20 metrics it recommends (e.g., "Project Releases Delivered on Time," "System Deployment Frequency," "System Post-Deployment Incidents") but ties them only to a "Release Dashboard" screenshot. — [Benchmarking blog](https://www.enov8.com/blog/holistic-release-management-metrics/)

### 4.7 Import / export
- Activities and tasks import "from existing plan or from csv"; copy plans between releases; onboarding via form or REST API. — [Release Management Overview](https://docs.enov8.com/docs/enov8-platform/release-modules/release-train-management/)
- REST API: header auth (`user-id`, `app-id`, `app-key`), GET/POST/PUT on 24+ resources, Postman collection; no CSV/Excel export or webhooks mentioned on that page. — [REST API](https://docs.enov8.com/docs/enov8-platform/rest-api/). Outbound webhooks exist via Webhook Management. — [MS Teams](https://docs.enov8.com/docs/enov8-platform/integrations/Communication/msteam/)

### 4.8 Known complaints
- Effectively **no public review corpus** for the release module: Gartner has 1 review (5.0) for Enov8 *Test Data Management*, none for RM ([Gartner](https://www.gartner.com/reviews/product/enov8-test-data-management)); SoftwareSuggest 0 reviews ([SoftwareSuggest](https://www.softwaresuggest.com/enov8)); Trustpilot 2 reviews from 2016–17 ([Trustpilot](https://www.trustpilot.com/review/enov8.com)); DecideSoftware aggregated user score 7.0/10 with lower marks for Performance (6.7) and Customer Support (6.5) ([DecideSoftware](https://decidesoftware.com/enov8/)). Treat this as a signal of small market footprint, not of quality.

### 4.9 Pricing
- "Contact us for SaaS and On-Premise pricing." — [Price overview](https://www.enov8.com/priceoverview/)
- AWS Marketplace AMI is usage-metered: **$0.10 per Application Instance (Environment) Under Management**, $0.40 per TDM data source, $0.80 per TB storage pack (hourly units), cancel anytime. — [AWS Marketplace](https://aws.amazon.com/marketplace/pp/prodview-5gogz34vwrtxy)

---

## 5. ServiceNow — DevOps Change Velocity (DCV) and Digital Product Release

### 5.1 Core object model
- **DCV**: App (DevOps application), **Pipeline** (Azure DevOps, GitLab, Jenkins, GitHub), **Step** (pipeline stage where change control is enabled), **Change Request** (auto-created in **Assess**, then Implement → Closed), **Change Policy** (decision framework using "commits, test results, security metrics"), **Change Receipts** (lets pipelines proceed without pausing; CR auto-transitions to post-implementation states). Dependent apps: DevOps Data Model, DevOps Integrations, DevOps Insights. — [Accelerate your DevOps change process (official doc)](https://www.servicenow.com/docs/r/it-service-management/devops-change-velocity/dev-ops-change-acceleration.html), [Quick Start Guide](https://www.servicenow.com/community/devops-articles/devops-change-velocity-quick-start-guide/ta-p/3061845)
- Apps are onboarded by associating "deployment plan, git repository, and CI/CD pipeline." — [Lab Guide](https://www.servicenow.com/community/devops-articles/devops-change-velocity-lab-guide/ta-p/3019491)
- **Digital Product Release** (current ITSM release-management offering): Release with start/end dates, **release phases and milestones**, tasks, change requests, **release policies**, **release templates**, **multiproduct release** consolidation, service releases. — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)
- Legacy Release Management (rm_release / release phase / release task in ITBM/SPM): docs **unreachable** (404 / robots).

### 5.2 Gate / approval mechanics
- "Policies define conditions and approval criteria used by flows to determine whether changes are auto-approved, auto-rejected, or require manual approval." Three base flows: **Manual Approval** (default), **Minimal Automation**, **Advanced Automation** (incl. scheduled state transitions). — [official doc](https://www.servicenow.com/docs/r/it-service-management/devops-change-velocity/dev-ops-change-acceleration.html)
- Policy example from a practitioner: "If SonarQube quality score ≥ 80%, auto-approve the change," with manual-approval ranges. — [Medium](https://medium.com/@sajankumar005/accelerating-change-management-with-servicenow-devops-change-velocity-997f0ec02e6a)
- Digital Product Release: "Use out-of-the-box release policies to automate readiness checks"; "Customize reusable templates that map out tasks and policies for every step of a release"; "automated risk scoring." — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)
- The pipeline **pauses at the change-control step** until the CR is approved, then "Pipeline resumes and begins the next job." — [GitLab docs](https://docs.gitlab.com/solutions/components/integrated_servicenow/). Azure Pipelines gate: "Desired state of change request" or expression criteria like `and(eq(root['result'].state, 'New'),eq(root['result'].risk, 'Low'))`; supports Normal/Standard/Emergency; "Invalid ServiceNow fields/values are silently ignored." — [Microsoft Learn](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/servicenow?view=azure-devops)
- Waivers = Emergency change type / manual override; evidence = pipeline artifacts (commits, tests, scans) attached to the CR as "single source of truth." — [Quick Start](https://www.servicenow.com/community/devops-articles/devops-change-velocity-quick-start-guide/ta-p/3061845)
- Change-approval-policy field-level docs were **robots-blocked** (`servicenow.com/docs/yR3mJtaNCVtXVpDM2Ql0SA`).

### 5.3 Runbook / cutover execution
- DCV has **no runbook/cutover execution**; execution lives in the CI/CD tool. Digital Product Release has release tasks and a readiness dashboard but no documented timers, dependencies, or live countdowns. — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)

### 5.4 Integration approach (Jira and CI/CD)
- **Webhooks, auto-registered**: "ServiceNow registers webhooks in the GitLab instance automatically once you choose the GitLab projects and pipelines" — [Medium](https://medium.com/@sajankumar005/accelerating-change-management-with-servicenow-devops-change-velocity-997f0ec02e6a); "The provided Tool URL and credentials will be used to create a Webhook from the Git Repo to ServiceNow." — [cloudnativenow guide](https://cloudnativenow.github.io/docs/pages/automate_change/guide/)
- Planning tools (Jira, Azure Boards) feed work items; commits link to work items (e.g., `AB#123`). — [cloudnativenow guide](https://cloudnativenow.github.io/docs/pages/automate_change/guide/)
- **Failure surfacing**: community lab comments report "script-level errors (undefined function calls in DevOpsPipelineUIAPI)" breaking artifact/test-result retrieval and "URL formatting issues and authentication questions." — [Lab Guide](https://www.servicenow.com/community/devops-articles/devops-change-velocity-lab-guide/ta-p/3019491). Azure gate errors are seen in pipeline logs. — [Microsoft Learn](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/servicenow?view=azure-devops)

### 5.5 Communication / notifications
- Manual-approval outcomes send "notifications sent to relevant parties." — [official doc](https://www.servicenow.com/docs/r/it-service-management/devops-change-velocity/dev-ops-change-acceleration.html)
- Digital Product Release: "Built-in release notifications" (customizable) and AI **auto-generated release notes** "based on release data." — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)

### 5.6 Reporting / analytics
- **DevOps Insights** provides "flow metrics, change acceleration metrics and other KPIs"; requires DevOps Performance Analytics jobs. — [Quick Start](https://www.servicenow.com/community/devops-articles/devops-change-velocity-quick-start-guide/ta-p/3061845), [cloudnativenow guide](https://cloudnativenow.github.io/docs/pages/automate_change/guide/). DORA is not named on the pages I could open; the DevOps Insights doc page was blocked.
- Digital Product Release: **"Release readiness dashboard"** with "start and end dates, task and policy status, change requests," risk scoring. — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)

### 5.7 Import / export
- Digital Product Release "imports CI/CD planning data to easily plan, visualize, and manage release versions." — [Digital Product Release](https://www.servicenow.com/products/digital-product-release.html). Generic platform list export/Table API were not verifiable from opened pages.

### 5.8 Known complaints
- Setup friction: lab comments cite script errors and auth/URL confusion — [Lab Guide](https://www.servicenow.com/community/devops-articles/devops-change-velocity-lab-guide/ta-p/3019491). A Digital.ai reviewer's complaint applies here too: checking a ServiceNow change "does not provide a way to actually verify if there was a change or not." — [G2 Digital.ai](https://www.g2.com/products/digital-ai-release/reviews). No DCV-specific G2/Gartner review page was reachable before the search budget ran out.

### 5.9 Pricing
- "DevOps Change Velocity is available with ServiceNow DevOps" ([DCV product page](https://www.servicenow.com/products/devops-change-velocity.html)); Digital Product Release is "available through" the ITSM suite ([Digital Product Release](https://www.servicenow.com/products/digital-product-release.html)); no tier prices listed ([ITSM page](https://www.servicenow.com/products/itsm.html)).

---

## 6. Cross-cutting patterns

### Table stakes (present in 3+ of the 5)
| Capability | Plutora | Digital.ai | Cutover | Enov8 | ServiceNow |
|---|---|---|---|---|---|
| Reusable templates for releases/runbooks (with template governance) | Release Templates | Templates (+YAML as-code) | Templates w/ reviewer approval workflow | Runsheet templates, copy plan | Release templates |
| Hierarchical bundling ("train"): parent release containing child releases/runbooks | Enterprise → Project/Independent | Release Groups + Software Delivery tracked items + Create Release sub-releases | Linked parent/child runbooks | Program → Release → Project; ARTs/PIs | Multiproduct release |
| Gate with named approvers that halts progression | Gates w/ criteria + approvers (traffic light) | Gate task (checkbox conditions + dependencies) | Fixed-start lock, min-users, integration wait | Implementation-plan Approvers, Approval task type | Change policy → manual approver |
| "Skip/waive with comment" as the exception path | not verified | Skip state | Admin skip w/ required comment | not documented | Emergency change |
| Blackout / freeze periods on the calendar | Yes | Global blackout w/ override permission | not found | Environment Freeze / Blockout Period events | change blackout (not verified) |
| Calendar/timeline view of releases | Release Calendar | Calendar view (Excel/ICS export) | Timeline view | Release Calendar, Event Calendar | Release readiness dashboard |
| RAG / flag health status on release | Traffic light gates | Flag (yellow/red) + Risk On Track/At Risk/Attention | RAG + message | RAG on release/project/activity/task | Risk scoring |
| Planned vs actual / forecast timing with overdue highlighting | Black = overdue | Overdue in red; due-soon at 25% | Forecast deltas, late warnings after 5 min | Timeline fields | — |
| Polling-based ITSM/Jira sync (not webhooks) | ServiceNow "Period (seconds)" | Jira/ServiceNow "Poll Interval", backoff lists | ServiceNow poll every 10 s | (webhook inbound instead) | (webhook inbound instead) |
| Email as the primary notification channel, with Teams/Slack as add-ons | Email templates | Email + Slack/Teams plugins | Email + SMS + Call tasks; Slack via integration | Email + Teams adaptive cards | Email |
| CSV/Excel import or export of plans | Export releases | Excel audit report, Excel calendar, YAML templates | CSV/Excel/PDF export, CSV import | CSV import of activities/tasks | CI/CD import |
| REST API | Swagger | `/api/v1`, MCP server | Public API | REST API + webhooks | Table API |
| Per-task attachments/comments as evidence | not verified | Attachments, comments | Comments (starred), validation notes | — | Pipeline artifacts on CR |

### Notable gaps shared across the market (opportunities for the scoped product)
- **No tool ships a dedicated "sync health" view.** Plutora offers only a Test Connection tick/cross; Digital.ai and Cutover surface failures as failed tasks; Enov8 pushes error visibility to the ServiceNow side; Cutover only recently (2026.12) added a filterable integration message log. A first-class per-ticket sync-status panel would be a differentiator.
- **No live countdown timers.** Cutover is closest (forecast deltas + 5-min/1-hour late escalation); Digital.ai has "due soon at 25% remaining" emails; Plutora marks overdue in black. None expose a countdown to window close or gate deadline.
- **System lockout tied to a gate** is not an explicit feature anywhere; the nearest are Digital.ai's blackout postponement and Enov8's Environment Freeze events.
- **One-click templated stakeholder status comms** are partial everywhere: Cutover's email/SMS/call tasks fire on schedule but have no documented merge-field templates; Digital.ai's templates are for system events, not ad-hoc status updates; Plutora's Email Template Wizard exists but could not be inspected.
- **Gate-cycle-time and on-time-% metrics** are not named in any product docs; DORA appears only in Digital.ai (and is being pulled from the Premium edition) and in ServiceNow's DevOps Insights marketing.

### Unique differentiators (one tool only)
- **Plutora**: Systems Impact Matrix / heat map of impacted systems; TEBR object; Master → child deployment plans with "granular saving" for concurrent editors; "Push to ServiceNow" bidirectional checkbox with pull/push expression builders.
- **Digital.ai Release**: Jython preconditions and failure handlers per task; gate auto-complete grace period; Software Delivery tracked items across releases; template-as-code YAML; ICS calendar publishing; Excel Release Audit Report designed "to deliver to the auditors"; MCP server for AI agents; ServiceNow-driven `snow_approved` gate tagging.
- **Cutover**: rehearsal mode with "Test Comms only"; critical-path nodemap; escalating late-task visuals; burn-up chart of planned vs actual; RTO status widget; SMS/voice-call tasks; validation tasks rolling into a Test Summary; minimum-users-to-start/complete; template reviewer role with 12-month re-review cycle.
- **Enov8**: environment booking/contention heat map ("Release Demand"); approve/reject directly from the email; Release Types Enterprise/Divisional/Infrastructure/Agile; Adaptive-Card Teams templates; usage-metered AWS pricing ($0.10/environment).
- **ServiceNow**: evidence-driven auto-approval policies (auto-approve/reject/manual) from pipeline data; Change Receipts for non-blocking pipelines; auto-registered webhooks into GitLab/GitHub; AI-generated release notes; IRM policy-as-code.

### Pricing summary
- Plutora, Digital.ai, ServiceNow, Enov8 (SaaS/on-prem): contact sales. Anecdotes: Plutora "$100k for just five seats"; Digital.ai "six-figure annual costs."
- Cutover: quote-based, but AWS lists **$1,850/month** for a 10-user/20-runbook starter.
- Enov8: AWS metered at **$0.10/hour per managed environment** (+ TDM units).

### Pages I could not open (so their contents are not claimed above)
help.plutora.com gates/approvals, deployment-activities, email-template-wizard, add-or-edit-project-releases, create-a-release-from-template; usapi.plutora.com Swagger (JS-only); plutora.com (redirects); web.archive.org (blocked); docs.digital.ai glossary, DORA dashboard, datasets, slack-plugin, risk-profiles; all `servicenow.com/docs/<hash>` and `docs.servicenow.com/bundle/...` pages; ServiceNow Store DCV listing (JS-only); servicenowspectaculars.com (DNS failure).