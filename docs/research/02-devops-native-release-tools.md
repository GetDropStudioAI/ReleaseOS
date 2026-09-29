_Research report produced 2026-09-28 by a Claude research agent for the Release Management App scope. Every claim cites a page that was actually opened; unreachable pages are flagged._

# Release-Management Features in Developer-Platform Tools — Research Report

**Method note.** Every claim below cites a page I actually opened via WebFetch. Where a page truncated or 404'd, it's flagged inline. Jira Cloud support pages truncated heavily (the fetcher returned mostly nav), so for some Jira details I fall back to Data Center equivalents and community threads. Harness has recently reorganized its docs — the `/docs/...` paths from search results all 404; the `developer.harness.io/continuous-delivery/...` and `/harness-platform/...` paths worked.

---

## 1. Jira (Software Cloud Releases/Versions + Plans + Jira Align)

### 1.1 Object model
- **Jira Software Cloud:** `Version` is project-scoped ("Versions represent points in time for your space"), issues carry a `Fix version`; statuses are Unreleased / Released / Archived; versions have start date and release date. ([Release your team's work in versions](https://support.atlassian.com/jira-software-cloud/docs/release-your-teams-work-in-versions/); [Create a version](https://support.atlassian.com/jira-software-cloud/docs/create-a-version-in-a-classic-project/) — both truncated, field list not fully verified)
- **Jira Plans (Advanced Roadmaps):** "Releases in your plan are referred to as Fix versions in Jira." Two kinds: single-project releases and **cross-project releases**. ([Manage releases in your plans](https://support.atlassian.com/jira-software-cloud/docs/manage-releases-in-advanced-roadmaps/))
- DC doc on cross-project releases: "Cross-project releases are used to manage joint releases, dates and milestones across multiple individual projects." "When you create a cross-project release and commit it to Jira, the release will be transformed into a regular release in each of the Jira projects that are part of the cross-project release." ([Creating and configuring cross-project releases, DC](https://confluence.atlassian.com/advancedroadmapsserver/creating-and-configuring-cross-project-releases-813891772.html)) — i.e., the cross-project release is a *plan-only* construct; Jira itself only stores per-project versions.
- **Jira Align hierarchy:** Portfolio → Program ("Also called a release train in the Scaled Agile Framework") → Program Increment ("a standard, often quarterly planning cycle") → Sprint; plus **Anchor Sprint** milestones. **Release Vehicle** = "an actual release to market or to internal end users of software." Feature "fits within a single PI." ([Jira Align Glossary](https://help.jiraalign.com/hc/en-us/articles/13170459905172-Glossary))
- Release Vehicle fields: Name; **Status**: Planning / In Progress / Launched / Closed / Canceled / Archive; **Health**: At Risk / On Track / On Hold; **Type**: major/minor/support; Start Date, Ship Date; Owner; "Program Increment Shipped In"; Programs and Teams. "When the release vehicle's status is set to Launched, the Go Live date is automatically set for today's date." Features must be explicitly assigned via the **Release Vehicle** field. ([Create a release vehicle](https://help.jiraalign.com/hc/en-us/articles/115002846688-Create-a-release-vehicle-for-an-actual-release-to-market))
- Jira Align ↔ Jira mapping: "Jira Fix Versions → Release Vehicles"; "Jira Align will associate a newly created release vehicle to the Jira Align program increment whose start date and end date range contains the Jira fix version's release date." ([Jira data synchronization](https://help.jiraalign.com/hc/en-us/articles/115000088393-Jira-data-synchronization?mobile_site=true))

### 1.2 Approvals and gates
- Jira Cloud now has **version approvers**: "If you've been added as an approver, you can approve or decline the work in the release. Approval indicates you're happy for the release to go ahead…" ([Use the release page](https://support.atlassian.com/jira-software-cloud/docs/use-the-release-page-to-check-the-progress-of-a-version/) — page truncated mid-sentence; whether approval *blocks* the Release button is not verified).
- Historical context: in Aug 2023 Atlassian stated "Currently, Jira SW does not have the option to add approvals for release" (feature request JSWCLOUD-16329); a community member listed missing pieces: "notifications to approvers, notification triggers upon approval, configurable release workflows, and audit trails with timestamps." ([Jira Release Approval thread](https://community.atlassian.com/forums/Jira-questions/Jira-Release-Approval/qaq-p/872996))
- **Automated warnings (not gates)** on the release page, DC doc: "Open pull requests", "Open reviews", "Unreviewed code", "Failing builds" — e.g. "An issue is done, but is linked to a failing build." Release action lets you "Move incomplete issues to another version." ([Using the release page, DC](https://confluence.atlassian.com/jirasoftwareserver/using-the-release-page-to-check-the-progress-of-a-version-938845471.html))
- No timeouts, no lockout, no "blocked" state on versions. Jira Align release vehicles carry Status/Health but no approval workflow (nothing found in the RV docs).

### 1.3 Freezes / blackout windows
- None in Jira Software Cloud, Plans, or Jira Align docs opened. Not a feature.

### 1.4 Runbooks
- None. Jira has no runbook concept.

### 1.5 Rollup
- Plans cross-project release: "the release dates are aggregated from the project-specific releases. The start date will be the earliest start date and the end date will be the latest end date." "Align dates" applies a config "to all the releases that are part of the cross-project release." ([DC cross-project releases](https://confluence.atlassian.com/advancedroadmapsserver/creating-and-configuring-cross-project-releases-813891772.html)). Releases tab filters: "off track," "on track," "released," "unreleased." ([View and edit releases in your plan](https://support.atlassian.com/jira-software-cloud/docs/edit-releases-in-advanced-roadmaps/))
- **Limitation:** "For a Cross Project Release it is only possible to select only 1 release from a Project" — confirmed by a consultant; workaround is a marketplace app with a "Package" concept. ([community thread](https://community.atlassian.com/forums/Jira-questions/cross-project-releases-with-more-than-one-fixversion-from-1/qaq-p/2885252))
- Jira Align: **Release vehicle roadmap** is "an interactive grid" of features × release vehicles "tied to the configured program and PIs", with a per-RV burndown icon; **Program increment roadmap** shows "PIs and release vehicles in a scrolling timeline." ([Release vehicle roadmap](https://help.jiraalign.com/hc/en-us/articles/115001852573-Release-vehicle-roadmap); [More actions for PIs](https://help.jiraalign.com/hc/en-us/articles/360012923134-More-actions-for-program-increments))

### 1.6 Jira / ServiceNow integration
- Jira is the ticket system; its **deployments feature** ingests from "Bitbucket, GitHub, GitLab, Jenkins, Azure DevOps" and marketplace apps; teams "reference work item keys in commits, branch names, and pull requests." Deployment data surfaces in the issue dev panel, board icon, "Releases hub", JQL, and Automation. ([What is the deployments feature?](https://support.atlassian.com/jira-cloud-administration/docs/what-is-the-deployments-feature/))
- Jira Align ↔ Jira sync is "bidirectionally for most fields"; PI on stories is Jira→Align only. No sync frequency or health view documented. ([Jira data synchronization](https://help.jiraalign.com/hc/en-us/articles/115000088393-Jira-data-synchronization?mobile_site=true))

### 1.7 Reporting
- **Release burndown**: "See how work added and removed during the sprint has affected your team's overall progress"; "Predict how many sprints it will take to complete the work for a version"; company-managed projects only. ([What is the release burndown report?](https://support.atlassian.com/jira-software-cloud/docs/what-is-the-release-burndown-report/))
- Jira "Development page" shows "deployment frequency and cycle time… from the first commit until the code is deployed to production." ([deployments feature](https://support.atlassian.com/jira-cloud-administration/docs/what-is-the-deployments-feature/))
- Jira Align reports: Program Increment Burndown/Burnup by Feature/Story, PI Progress Drill Down, PI Scope Change, PI Countdown, PI Heat Map, Program Board, Program Predictability, Release Vehicle Roadmap. ([Comprehensive list of Jira Align reports](https://community.atlassian.com/forums/Jira-Align-articles/Comprehensive-List-of-all-Jira-Align-Reports/ba-p/2060292); [Program-level reports](https://help.jiraalign.com/hc/en-us/sections/115001716768-Program-level-reports))

### 1.8 Export / API
- REST: per-project versions only. "there exists no such API where you can fetch all versions from Jira… Only way to do this is per project basis." ([community thread](https://community.atlassian.com/forums/Jira-questions/Rest-API-for-Releases-of-all-projects/qaq-p/1225577)). The official [Project versions API group](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-project-versions/) exists but the page returned only navigation — endpoint list not verified.
- Jira Align reports offer a "Print Friendly Version" / PDF at page footer. ([Release vehicle roadmap](https://help.jiraalign.com/hc/en-us/articles/115001852573-Release-vehicle-roadmap))
- No CSV export of the Releases page found in docs opened.

### 1.9 Complaints
- "Jira doesn't support cross-project releases out of the box"; gadgets "treat versions with identical names across different projects as separate entities." ([Release management across different jira projects](https://community.atlassian.com/forums/Jira-Cloud-Admins-discussions/Release-management-across-different-jira-projects/td-p/1432145))
- Only one fix version per project per cross-project release. ([thread](https://community.atlassian.com/forums/Jira-questions/cross-project-releases-with-more-than-one-fixversion-from-1/qaq-p/2885252))
- Release approvals lacked "notifications to approvers… audit trails with timestamps." ([thread](https://community.atlassian.com/forums/Jira-questions/Jira-Release-Approval/qaq-p/872996))

---

## 2. Azure DevOps

### 2.1 Object model
- Classic: **Release pipeline** (definition) → **Release** = "a construct that holds a versioned set of artifacts… a snapshot of all the information required to carry out all the tasks… such as stages, tasks, policies, and deployment options." **Stages** (e.g., Dev → QA → Prod Ring 1 → Prod Ring 2). **Deployment** = "the execution of the tasks defined for a single stage in a release." "A release can be deployed multiple times to the same stage." Workflow: pre-deployment approval → queue → agent → download artifacts → tasks → logs → post-deployment approval. Multiple artifact sources (Jenkins, Azure Artifacts, TeamCity). ([Classic release pipelines](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/?view=azure-devops)) — no deprecation statement on that page.
- YAML: **Environment** = "a group of resources that you can target with deployments from a pipeline"; resource types "Kubernetes and virtual machine"; provides "Deployment history", "Traceability" ("view the commits and work items that were newly deployed"), "Diagnostic resource health", "Security". Roles: Creator/Reader/User/Administrator. ([Environments](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/environments?view=azure-devops))

### 2.2 Approvals and gates
- **Classic approvals:** pre- and post-deployment; "When a group is specified as approvers, only one user from that group is needed"; "If no approval is granted within the Timeout period, the deployment is rejected." Policies: requestor cannot approve; MFA re-sign-in; auto-approve if same user approved previous stage. Emails to approvers. ([Classic approvals](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/approvals?view=azure-devops))
- **Classic gates:** Invoke Azure Function, Query Azure Monitor alerts, Invoke REST API, Query work items ("Ensure the number of matching work items returned from a query is within a threshold"), Check Azure Policy compliance, plus marketplace. Options: "Delay before evaluation", "Time between re-evaluation of gates", "Timeout after which gates fail" ("The deployment is rejected if the timeout is reached before all gates succeed during the same sampling interval"), and ordering: pre-deployment defaults to approvals first then gates; post-deployment defaults to gates then approvals. Pre-deployment gate delay "capped at 48 hours." ([Gates](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/gates?view=azure-devops); [Overview](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/?view=azure-devops))
- **YAML checks on environments/resources:** Approvals (min approvers; "Allow approvers to approve their own runs"; default timeout 30 days → stage skipped; **deferred approvals** to a future time), Branch control, **Business hours** (waits for window; timeout → stage failed), Evaluate artifact, **Exclusive lock** (`runLatest` | `sequential`), Invoke Azure Function / REST API (re-evaluated on "Time between evaluations"), Query Azure Monitor alerts, Required template, ServiceNow Change Management. Evaluation order: static checks → pre-check approvals → dynamic checks → post-check approvals → exclusive lock. "A single final negative decision causes pipeline denial and stage failure." Admins can **Bypass** approvals/business hours/function/REST checks (logged). "The list of users who can review an Approval is fixed when approvals & checks start running." ([Pipeline deployment approvals/checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops))
- Sprint 226: "you can retry a stage when approvals and checks time-out." ([Sprint 226](https://learn.microsoft.com/en-us/azure/devops/release-notes/2023/pipelines/sprint-226-update))
- "Blocked" = stage waits on checks; on timeout "the stage they belong to is skipped. Stages that have a dependency on the skipped stage are also skipped."

### 2.3 Freezes
- No freeze object. The inverse exists: the **Business hours** check restricts deployments to a window; "If execution hasn't started by end of business hours… the approval is automatically withdrawn and reevaluation is scheduled for the next day." ([checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops)) Blackouts are otherwise done via ServiceNow change windows (see 2.6).

### 2.4 Runbooks
- No runbook object. Closest: "Manual Intervention" and "Manual Validation" tasks that "allow users to follow specific instructions and resume/reject deployment." ([overview](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/?view=azure-devops))

### 2.5 Rollup
- A classic **release** bundles multiple artifacts from multiple builds/sources and shows per-stage deployment status; that is the multi-service rollup unit. ([Classic release pipelines](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/?view=azure-devops)) No cross-pipeline "train" object in YAML; environments show per-environment history.

### 2.6 Jira / ServiceNow
- **ServiceNow Change Management extension (Azure→SNOW):** gate/check with Action "Create new change request" or "Use existing"; Change type Normal/Standard/Emergency; planned start/end (UTC); category/priority/risk/impact/CI/assignment group; extra `u_` fields; success criteria "Desired state of change request" or "Advanced success criteria" JEXL-like expression; output vars `CHANGE_REQUEST_NUMBER`, `CHANGE_SYSTEM_ID` via `$(PREDEPLOYGATE.gate1.CHANGE_REQUEST_NUMBER)`. Flow: "When the change is ready for implementation and moved to Implement state, the pipeline resumes"; "The change request closes automatically after deployment." Requires the Azure Pipelines app on the SNOW side with role `x_mioms_azpipeline.pipelinesExecution`. **Update ServiceNow Change Request** task (agentless) sets state/work notes. No SNOW→Azure trigger documented. ([ServiceNow integration](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/approvals/servicenow?view=azure-devops))
- **ServiceNow DevOps Change Velocity (SNOW side):** discovers "Azure Boards… Azure Repositories… Azure Pipelines" for "Build (CI) pipelines… and Release (CD) pipelines"; components "ServiceNow DevOps service connection, ServiceNow DevOps Release Gate, Azure build (CI) pipeline agent and server job custom tasks"; "run commits listed for a task execution are limited to 200." ([SNOW Azure DevOps integration](https://www.servicenow.com/docs/bundle/zurich-it-service-management/page/product/enterprise-dev-ops/concept/azure-devops-integration-dev-ops.html)). Change Velocity "uses flows and DevOps data (such as work items, commits, code coverage, code security, risk, and test results) to update the state of a change request and automatically approve it based on change approval policies." ([SNOW FAQ](https://www.servicenow.com/community/devops-articles/faq-for-devops-change-velocity/ta-p/3018723))
- **Jira:** Microsoft's "Azure Pipelines for Jira" is **deprecated** ("This Integration is no longer maintained"; 2.1/5 stars); replacement is Atlassian's "Azure DevOps for Jira Official". ([Marketplace listing](https://marketplace.atlassian.com/apps/1220515/azure-pipelines-for-jira)). Original intent: "bi-directional linking between releases in Azure Pipelines and issues in Jira software cloud… tracking Jira issues mentioned in GitHub commits deployed with releases." ([Sprint 154](https://learn.microsoft.com/en-us/azure/devops/release-notes/2019/sprint-154-update))

### 2.7 Reporting
- Dashboard widgets: "Build history", "Deployment status", "Release pipeline overview", "Requirements quality", "Test results trend", "Test results trend (Advanced)". **No DORA metrics** on that page. ([Pipeline widgets](https://learn.microsoft.com/en-us/azure/devops/pipelines/reports/pipeline-widgets?view=azure-devops)) DORA requires marketplace extensions (search surfaced "DORA Metrics" / "Devops Performance Metrics" extensions; not opened).

### 2.8 Export / API
- Release REST API 7.1 groups: Releases, Definitions, Approvals, Deployments, Environments, Gates, Manual Interventions. Page last updated 2022-07-07. ([Release REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/release/?view=azure-devops-rest-7.1)) No CSV/PDF export of releases in docs opened; Analytics/OData is for work items/tests.

### 2.9 Complaints
- Classic→YAML parity: "post approvals can not be supported in YAML… can we set revaluation, time outs etc in same way as gates we are not sure… Service now integration does it work same way in YAML" — question was redirected, unanswered. ([Q&A thread](https://learn.microsoft.com/en-us/answers/questions/496996/classic-vs-yaml-pipelines-in-azure-devops-and-appr))
- G2: "Debugging of failure is very hard to identify due to YAML pipelines." (AVP, Enterprise); "Troubleshooting pipeline failures can sometimes be difficult, especially when the error messages are not very clear." ([G2 Azure Pipelines reviews p.3](https://www.g2.com/products/azure-pipelines/reviews?page=3))

---

## 3. Octopus Deploy

### 3.1 Object model
- Space → Project (in Project Group) → **Release** ("snapshots of your deployment process and other associated assets (references to package versions, scripts, variables) as they existed when the release was created") → **Deployment** (a release to an environment, optionally a tenant). "Releases can be deployed 0 to N times before production." **Prevent release progression** blocks a release from further environments. ([Releases and deployments best practices](https://github.com/OctopusDeploy/docs/blob/main/src/pages/docs/best-practices/deployments/releases-and-deployments.md))
- **Lifecycle** "control[s] the order of release promotion"; contains "1 to N phases… A phase can have 0 to N environments"; phases are required ("at least one environment must have a successful deployment before the release can proceed") or optional; phases can auto-deploy; "you cannot have an optional phase with automatic deployments." Retention policies per environment. "A project references lifecycles via channels and can reference 1 to N lifecycles." ([Lifecycles and environments](https://github.com/OctopusDeploy/docs/blob/main/src/pages/docs/best-practices/deployments/lifecycles-and-environments.md))
- **Channels** "allow you to dynamically change the deployment logic and lifecycle of a project based on the version being deployed"; types: Lifecycle channels and Ephemeral Environment channels; version rules (SemVer 2.0.0 or publish date); step and tenant scoping. ([Channels](https://octopus.com/docs/releases/channels))

### 3.2 Approvals and gates
- **Manual Intervention and Approval step:** Instructions; "Responsible teams" — "If no team is specified, anybody with permission to deploy the project can perform the manual intervention. Specifying a team makes the step a required step that cannot be skipped."; "Assign to me" ("Interruptions can only be assigned to one person at a time"); **Proceed** or **Abort** ("fail and stop the deployment"); Notes captured in `Octopus.Action[Step Name].Output.Manual.Notes`. No timeout documented. ([Manual intervention step](https://octopus.com/docs/projects/built-in-step-templates/manual-intervention-and-approvals))
- Lockout: a 2019 change made pending interventions block other deployments; fix added "Allow concurrent deployment to start while awaiting intervention" checkbox. ([Issue #5666](https://github.com/OctopusDeploy/Issues/issues/5666))
- Automated gates are external: ITSM (ServiceNow/JSM) "Change Controlled" environments (3.6). Best-practice doc: "Use ITSM integration (ServiceNow, JIRA) for production approvals rather than manual intervention steps." ([lifecycles doc](https://github.com/OctopusDeploy/docs/blob/main/src/pages/docs/best-practices/deployments/lifecycles-and-environments.md))
- Pattern for "approve once, deploy later": "Prod Approval" environment — "approve at 11 AM and schedule it to deploy at 7 PM." ([Release management blog](https://octopus.com/blog/release-management-with-octopus))

### 3.3 Freezes (strongest of the five)
- Name; start/end; recurrence "Daily, Weekly, Monthly, Annual, Custom…"; scope "Projects, Environments, Tenants" (project scope, tenant scope, or both). Enforcement: "New deployments are prevented from being created"; "Any existing deployments that start executing during the freeze will fail"; "scheduled deployment or automatic lifecycle promotions, are blocked" but target-trigger auto-deploys allowed. **Override** needs `DeploymentFreezeAdminister` and users must "enter a reason for the override" (kept "in the audit trail"). Multi-project freezes are enterprise-only (2025.2.7631+); per-project for all. API: REST/Go client, Terraform `octopusdeploy_deployment_freeze`. ([Deployment freezes](https://octopus.com/docs/deployments/deployment-freezes); [blog](https://octopus.com/blog/deployment-freeze))
- Known bug (fixed 2024.3.223): override failed when deploying to a frozen + unfrozen environment together. ([Issue #8793](https://github.com/OctopusDeploy/Issues/issues/8793))

### 3.4 Runbooks (first-class, only tool with them)
- "A runbook is a set of instructions that help you consistently carry out a task, whether it's routine maintenance or responding to an incident." "Runbooks belong to projects" (Deploy ➜ Runbooks). ([Runbooks](https://octopus.com/docs/runbooks))
- vs deployments: "No release needs to be created to execute a runbook"; "Lifecycles do not apply to runbooks"; "Runbook executions are not displayed on the deployment dashboards"; share project variables; different permissions; run settings restrict environments. ([Runbooks vs deployments](https://octopus.com/docs/runbooks/runbooks-vs-deployments))
- **Snapshots:** Draft vs Published; snapshot captures "the process, variables, and packages"; consumers with `RunbookRunCreate` but not `RunbookSnapshotCreate` "will always execute the published snapshot." ([Runbook publishing](https://octopus.com/docs/runbooks/runbook-publishing))
- **Scheduling:** scheduled triggers by days of week / dates of month / CRON (6-field, e.g. `0 0 06 * * Mon-Fri`); "Only published snapshots can be used"; target environments/tenants. ([Scheduled runbook triggers](https://octopus.com/docs/runbooks/scheduled-runbook-trigger))
- **Webhook triggers:** `POST /api/{spaceId}/webhook/{webhookId}`, ≤5 MB body, shared secret or API key; "Run a diagnostic runbook when your monitoring tool raises an alert"; returns 200 when queued. ([Webhook runbook triggers](https://octopus.com/docs/runbooks/webhook-runbook-trigger/))
- Monitoring: task log + Manual Intervention steps inside runbooks (same step template); no countdown timers or live checklist UI.

### 3.5 Rollup
- No native "train" object. Documented pattern: parent "Release Orchestration" project using the **"Deploy Child Octopus Deploy Project"** step template — selects child release by channel + version pattern (e.g. `2021.1.0.*`), "Find the release that was last successfully deployed to the previous environment", skips if already deployed, waits with "30-minute default timeout", can auto-approve child manual interventions by matching teams, aggregates `ReleaseNotes` output variable; motivation: "approvers like to approve a release once, not for each child project." ([blog](https://octopus.com/blog/release-management-with-octopus))

### 3.6 Jira / ServiceNow
- **ServiceNow (enterprise, 2022.3+):** mark environment and project **Change Controlled**; deployment auto-creates CR — "`Normal` change, or a `Standard` change if the project has a `Change Template Name` set"; Emergency from 2024.2; deployment waits while State is New/Assess/Authorize/Scheduled and proceeds at `Implement`; supply existing CR via `Octopus.ServiceNow.ChangeRequest.Number`. Octopus→SNOW outbound only. ([ServiceNow integration](https://octopus.com/docs/approvals/servicenow))
- **Jira Service Management (enterprise):** same model; creates a "Request a change" issue titled `Octopus: Deploy "{project}" version {ver} to "{env}"`; proceeds at `Implementing`; variable `Octopus.JiraServiceManagement.ChangeRequest.Number`; from 2025.2 the number can be set at deployment creation. ([JSM integration](https://octopus.com/docs/approvals/jira-service-management))
- **Jira Software:** "Octopus Deploy for Jira" app — deployment status in issues, "Automatically populate release notes in Octopus with your Jira issues"; 3.3/5 (11 reviews), 1,127 installs. ([Marketplace](https://marketplace.atlassian.com/apps/1220376/octopus-deploy-for-jira))

### 3.7 Reporting
- **Insights** (DORA): Deployment Lead Time ("time between the creation date of the release immediately following the previously successful release and the completion date of the deployment"), Deployment Failure Rate ("fail to deploy, require guided failure or have their release marked as 'Do not promote'"), Deployment Frequency, Mean Time to Recovery. Project-level free; space-level "Insights reports" enterprise. Filters: time, channel, environment, tenant. ([Insights](https://octopus.com/docs/insights))
- Reporting: "As an XML feed, which can be consumed by tools like Microsoft Excel or PowerBI… As a table in the Octopus SQL database" (self-hosted only for SQL). ([Reporting](https://octopus.com/docs/administration/reporting))

### 3.8 Export / API
- Insights "DOWNLOAD CSV" with 17 columns (Deployment ID, Project, Release, Channel, Completed, Environment, Tenant, State, Guided Failure, Lead Time, …, Time to Recovery); "The same data is available in JSON format via an API call." ([CSV download](https://github.com/OctopusDeploy/docs/blob/main/src/pages/docs/insights/csv-download.md))
- Full REST API, Go client, Terraform provider (freezes), CLI. ([Deployment freezes](https://octopus.com/docs/deployments/deployment-freezes))

### 3.9 Complaints
- G2: "Now octopus deploy charging based on no of deployment target licenses which turns out to be costly"; "Sometimes runbooks don't run and just hang"; "Better built-in reporting and analytics would make tracking deployment metrics easier"; "The UI, while powerful, sometimes feels cluttered when managing large projects". ([G2 pros/cons](https://www.g2.com/products/octopus-deploy/reviews?qs=pros-and-cons))
- Manual-intervention lockout regression: "With thousands of pending approvals, no new deployments could start." ([Issue #5666](https://github.com/OctopusDeploy/Issues/issues/5666))

---

## 4. Harness

### 4.1 Object model
- "Pipeline > Stage > Service + Environment + Infrastructure Definition". "A CD Pipeline is a series of Stages where each Stage deploys a Service to an Environment." Services = "what you're deploying"; Environments (Production / PreProduction type) contain Infrastructure Definitions; scoped at account/org/project. **No "release" object** — "release" is used only functionally. Strategies: rolling, canary, blue-green, custom. ([CD overview](https://developer.harness.io/continuous-delivery/new-to-continuous-delivery/overview))

### 4.2 Approvals and gates
- **Harness Approval step/stage:** User Groups; "Number of approvers that are required at this step"; "Disallow the executor from approving the pipeline… even if they're in an allowed group"; **Approver Inputs** (approvers can set variables consumed downstream); default timeout "1d (24 hours)", max 53 weeks; **Auto-Reject** "reject old executions waiting for approval when a latest step is approved"; **Auto approval** "at a specific date and time" (≥15 min ahead); notifications "Approval Required" and "Approved or Rejected" via user-group channels. ([Harness approval stages](https://developer.harness.io/harness-platform/use-harness-platform/approvals/adding-harness-approval-stages); YAML: `approvers.userGroups / minimumCount / disallowPipelineExecutor`, `isAutoRejectEnabled` at [CD approval steps](https://developer.harness.io/continuous-delivery/use-continuous-delivery/cd-building-blocks/cd-steps/approvals/using-harness-approval-steps-in-cd-stages))
- **Jira/ServiceNow Approval steps** poll a ticket: Conditions (`=`, `!=`, `in`, `not in`) and/or JEXL (`<+ticket.state.displayValue> == "New"`); Rejection Criteria; Retry Interval; "Do not use a brief timeout"; **Approval Change Window** — "Once this step is approved, Harness proceeds if the current time is within this window." SNOW dates must be UTC. ([Jira/SNOW approval steps](https://developer.harness.io/continuous-delivery/use-continuous-delivery/cd-building-blocks/cd-steps/approvals/using-jira-and-service-now-approval-steps-in-cd-stages); [SNOW approvals](https://developer.harness.io/harness-platform/use-harness-platform/approvals/service-now-approvals))
- "Blocked" = step waiting; on rejection/timeout the step fails and the pipeline stops (failure strategies apply).

### 4.3 Freezes
- Freeze windows at account/org/project; rules over services, environments, environment types (Production/Pre-Production), orgs/projects with exceptions; timezone; start/end or duration ("1d"); recurrence "Does not repeat, Daily, Weekly, Monthly, Yearly" with end dates. Enforcement: running pipeline "will continue to run until the current stage… has executed… no further stages will execute"; status **"Aborted By Freeze"**. **Global Freeze** overrides all. **Override** role "can still perform deployments." Notifications on "Freeze window is enabled", "…enabled and active", "Deployments are rejected due to freeze window" via Slack/Email/PagerDuty/Teams. RBAC by environment type. ([Freeze deployments](https://developer.harness.io/continuous-delivery/use-continuous-delivery/manage-deployments/deployment-freeze))

### 4.4 Runbooks
- None as an object. Custom stages / pipelines serve as scripted procedures; Approval steps supply instructions via "approvalMessage". Not documented as runbooks.

### 4.5 Rollup
- Multi-service pipelines: DORA data captured "for each service-environment combination within a pipeline". No train/bundle object. ([DORA dashboard](https://developer.harness.io/continuous-delivery/use-continuous-delivery/monitor-deployments/dora-metrics-dashboard))

### 4.6 Jira / ServiceNow
- **Outbound steps:** Jira Create / Jira Update / Jira Approval; ServiceNow Create ("change requests, incident tickets, change tasks, and problem type"; "Create From Form Template"; standard-change templates for "pre-approved change types"; custom fields by field name) / Update / Approval. Chaining via expressions like `<+pipeline.stages.Jira_Stage.spec.execution.steps.Jira_Create.issue.key>` (referenced step must precede). ([SNOW Create](https://developer.harness.io/continuous-delivery/use-continuous-delivery/cd-building-blocks/cd-steps/ticketing-systems/create-service-now-tickets-in-cd-stages); [Jira/SNOW approvals](https://developer.harness.io/continuous-delivery/use-continuous-delivery/cd-building-blocks/cd-steps/approvals/using-jira-and-service-now-approval-steps-in-cd-stages))
- **ServiceNow DevOps Change Velocity (inbound):** SNOW docs say connect "to your Harness instance to discover pipeline definitions and configure real-time notifications or polling to enable change traceability" via Harness webhooks ([SNOW Harness page](https://www.servicenow.com/docs/bundle/zurich-it-service-management/page/product/enterprise-dev-ops/concept/harness-integration-with-devops-change-velocity.html) — intro only). SNOW Store app "Integration for Harness Software Delivery Platform" page returned only metadata ("Integrate Service Now flows seamlessly within your Devops pipelines"). ([Store](https://store.servicenow.com/store/app/fb2b6b2a1b246a50a85b16db234bcb33))
- **Change intelligence:** Harness SRM "Change Impact Analysis" tracks "deployments, infrastructure changes, incidents, feature flags, and chaos experiments" in a "Changes dashboard" and "Service Health dashboard" to find "recent change events in your service around the time performance deteriorated." ([Change Impact Analysis](https://developer.harness.io/service-reliability-management/use-srm/change-impact-analysis/change-impact-analysis))

### 4.7 Reporting
- **DORA dashboard:** Deployment Frequency, Mean Time to Restore ("only works for executions reverted using post production rollback feature"), Change Failure Rate, "Lead Time to Production… median duration of deployments"; "does not measure regressions or failures that occur after a production deployment is complete." ([DORA dashboard](https://developer.harness.io/continuous-delivery/use-continuous-delivery/monitor-deployments/dora-metrics-dashboard))
- Built-in Deployments and Services dashboards (Dashboards > By Harness); Looker-based custom dashboards with explores "Deployments and Services" (views incl. Approval Stage, Reverted Deployments) and "…V2" (Harness Approval Step Execution, Jira Step Execution). ([Monitor CD deployments](https://developer.harness.io/continuous-delivery/use-continuous-delivery/monitor-deployments/monitor-cd-deployments); [Custom dashboards](https://developer.harness.io/continuous-delivery/use-continuous-delivery/monitor-deployments/using-cd-custom-dashboards))

### 4.8 Export / API
- Dashboard → PDF or zipped CSV; tile → "TXT… Excel… CSV, JSON, HTML, Markdown, PNG". ([Download dashboard data](https://developer.harness.io/harness-platform/use-harness-platform/harness-dashboards/dashboard-legacy/download-dashboard-data)) Approval API referenced in docs (not opened).

### 4.9 Complaints
- G2: "The deployment graphs always extend off the side of the screen and key parameters are hidden away in a very cluttered UI."; "Pricing scales with usage and features, which can get expensive as pipeline complexity and deployment frequency grow."; "The developer API reference documentation could be improved." ([G2 Harness Platform](https://g2.com/products/harness-platform/reviews))
- Docs churn: every `/docs/...` Harness URL surfaced by search 404s (observed during this research).

---

## 5. GitLab

### 5.1 Object model
- **Release** = Git tag + release notes + assets + milestones + evidence; "GitLab automatically tags your code"; created via UI, Releases API, `release` CI keyword (release-cli legacy). Badges: **Upcoming Release** (future `released_at`) / **Historical release**. Milestones show "statistics about the issues." Tiers Free/Premium/Ultimate. ([Releases](https://docs.gitlab.com/user/project/releases/))
- **Release evidence:** "a snapshot of data… saved in a JSON file" incl. "test artifacts, linked milestones, and matching packages"; for "external audits"; can be re-collected via API. ([Release evidence](https://docs.gitlab.com/user/project/releases/release_evidence/))
- **Environment / Deployment:** deployment created "When you deploy a version of your code to an environment"; "full history of deployments to each environment"; environment tier (production/staging…); rollback creates "a new deployment." ([Deployments](https://docs.gitlab.com/ci/environments/deployments/)) Release and deployment are *not* linked objects.

### 5.2 Approvals and gates
- **Deployment approvals** (Premium/Ultimate, requires protected environment): "Multiple approval rules" by user/group/role with required count; "the user who triggers a deployment pipeline can't also approve" unless "Allow pipeline triggerer to approve deployment"; jobs show "blocked" label — "All jobs deploying to the environment are blocked and wait for approvals"; approve/reject with comment via UI or API; "A user can give only one approval per deployment"; **"Deployment approval doesn't automatically start the corresponding deployment job. You must manually run the job."** No expiry documented. ([Deployment approvals](https://docs.gitlab.com/ci/environments/deployment_approvals/))
- **Protected environments:** "Allowed to deploy" (Maintainers/Developers/users/groups); "Approvers"; unauthorized run → "the deployment job fails with an error message"; group-level protection by **deployment tier**. ([Protected environments](https://docs.gitlab.com/ci/environments/protected_environments/))
- Other safety: "Prevent outdated deployment jobs" ("failed outdated deployment job"); `resource_group` for one-at-a-time. ([Deployment safety](https://docs.gitlab.com/ci/environments/deployment_safety))
- No automated gates beyond CI job outcomes; external gates via ServiceNow (5.6).

### 5.3 Freezes (weakest enforcement)
- **Deploy freeze** = cron `freeze_start` / `freeze_end` + `cron_timezone`, per project; API `GET/POST/PUT/DELETE /projects/:id/freeze_periods`. ([Freeze Periods API](https://docs.gitlab.com/api/freeze_periods))
- Enforcement is **opt-in via job `rules`**: "deploy freezes will not be respected until `.gitlab-ci.yml` is edited to make the deployment jobs aware of deploy freezes"; frozen pipelines are skipped, "It will need to be manually restarted once the freeze is over"; group-level out of scope. ([Issue #24295](https://gitlab.com/gitlab-org/gitlab/-/issues/24295))
- Evaluated at **pipeline start, not job start**: "it will be based on when the pipeline starts, not when the job starts" — long/manual pipelines can deploy inside a freeze. ([Issue #233047](https://gitlab.com/gitlab-org/gitlab/-/work_items/233047))
- Cron bug: freeze "will only apply if the cron syntax specifies a time period that won't repeat 'within' the specified freeze period." ([Issue #370472](https://gitlab.com/gitlab-org/gitlab/-/work_items/370472))

### 5.4 Runbooks
- None. Releases can "Attach release assets, like runbooks" — as files only. ([Releases](https://docs.gitlab.com/user/project/releases/))

### 5.5 Rollup
- No multi-project release. Cross-project visibility only via **Environments Dashboard** (Premium/Ultimate): "cross-project environment-based view", "latest commit, pipeline status, and deployment time", up to 150 projects, "up to three environments per project". ([Environments dashboard](https://docs.gitlab.com/ci/environments/environments_dashboard))

### 5.6 Jira / ServiceNow
- **GitLab for Jira Cloud app:** syncs "branches, commits, merge requests, pipelines, deployments, and feature flags" to the dev panel; one-way ("Jira does not gain any access to GitLab"); link via Jira issue key; initial sync "batches of 20 projects per minute", history limited to "last 400 merge requests" / "last 400 branches". ([GitLab for Jira Cloud app](https://docs.gitlab.com/integration/jira/connect-app/))
- **ServiceNow Integrated Change Management** (Premium/Ultimate; Xanadu+): via DevOps Change Velocity; "the pipeline job with the change control function runs" and "will remain running until the change request is approved"; policy-based auto-approval; custom-image option sets "Change Request title, description, change plan, rollback plan, and data related to artifacts". ([Integrated ServiceNow](https://docs.gitlab.com/solutions/components/integrated_servicenow/)) SNOW FAQ lists GitLab among OOB orchestration tools. ([SNOW FAQ](https://www.servicenow.com/community/devops-articles/faq-for-devops-change-velocity/ta-p/3018723))

### 5.7 Reporting
- **DORA (Ultimate):** Deployment frequency = "average number of deployments per day to a given environment, based on… `finished_at`", production-tier only; Lead time = commit → running in production; Time to restore = "median time an incident was open"; Change failure rate = incidents ÷ deployments, assumes "strictly one-to-one relationship". Shown in "Value Streams Dashboard," "CI/CD analytics charts," "Insights reports"; GraphQL + REST. ([DORA metrics](https://github.com/diffblue/gitlab/blob/master/doc/user/analytics/dora_metrics.md))

### 5.8 Export / API
- Releases REST + GraphQL; Freeze Periods API; Release evidence JSON; DORA via API. No CSV/PDF export of releases documented in pages opened.

### 5.9 Complaints
- "Environment Approval Workflow Unusable": approvers "have to ask that person through email/IM/manual method outside of Gitlab, point them to the specific job and environment and ask them to Approve"; approvers face "50+ pages" of jobs. ([Issue #428658](https://gitlab.com/gitlab-org/gitlab/-/issues/428658)); "I want to be notified when an approval is awaiting my action" — still an MVP proposal. ([Issue #357026](https://gitlab.com/gitlab-org/gitlab/-/issues/357026))
- Deploy freeze semantics (pipeline-start, cron recurrence) — see 5.3.

---

## Cross-cutting patterns

### Table stakes (3+ tools)

| Capability | Jira | Azure DevOps | Octopus | Harness | GitLab |
|---|---|---|---|---|---|
| Manual approval with named approvers / groups | Version approvers (new, thin) | Yes (classic + YAML) | Manual Intervention + teams | Approval step + user groups | Deployment approvals + rules |
| Self-approval prevention | – | "requestor cannot approve" policy | – | "Disallow the executor" | default: triggerer can't approve |
| Approval timeout → failure/skip | – | 30d default (YAML); classic timeout → rejected | none documented | 1d default, max 53w | none documented |
| Approval comment/notes captured | – | yes | Notes output var | comments + approver inputs | comment |
| Environment/stage concept with deployment history | – (fixVersion only) | Environments | Environments + Lifecycle phases | Environments + infra defs | Environments + tiers |
| Freeze / blackout window | – | Business hours (inverse) | Yes, hard, recurring, override+reason | Yes, hard, recurring, override role | Cron, soft/opt-in |
| ServiceNow CR auto-create + wait for Implement | – | Yes (gate/check) | Yes (Change Controlled env) | Yes (Create/Approval steps) | Yes (Change Velocity) |
| Jira issue linkage from deployments | native target | via Atlassian app | via Octopus app | Jira Create/Update/Approval | GitLab for Jira app |
| DORA metrics built-in | partial (dev page) | no (marketplace) | Insights | DORA dashboard | Ultimate |
| REST API for releases/approvals | per-project versions | full Release API | full | full | full |
| CSV/PDF export | print-to-PDF (Align) | – | Insights CSV, XML feed | PDF/CSV/XLSX/JSON | – |

### Differentiators (one tool)

| Feature | Tool | Why it matters to an RTE product |
|---|---|---|
| First-class **runbooks** with draft/published snapshots, cron + webhook triggers, no release required | Octopus | Closest analogue to "live runbooks"; still no countdown timers, checklists, or live status board |
| **Deferred approvals** (approve now, execute at a set time) | Azure DevOps YAML; Harness "Auto approval at date/time"; Octopus "Prod Approval env" pattern | Two tools + a pattern — near table-stakes for "approve at 11, deploy at 19:00" |
| **Exclusive lock** (`runLatest` / `sequential`) on environments | Azure DevOps | Native lockout of a stage while another run holds it |
| **Auto-Reject previous executions on approval** | Harness | Prevents stale approvals piling up (the exact failure mode in Octopus #5666) |
| **Approver Inputs** (approvers set variables used downstream) | Harness | Gate owner supplies runtime facts (e.g., CR number) at approval |
| **Freeze override with mandatory reason in audit trail** | Octopus | Auditable break-glass |
| **"Aborted By Freeze" mid-pipeline + freeze notifications** | Harness | Freeze becomes an active event, not just a pre-check |
| **Release evidence JSON snapshot** | GitLab | Audit artifact per release |
| **Parent/child "release orchestration"** aggregating child release notes and auto-approving child interventions | Octopus (step template) | The only documented multi-service "train" pattern; still a workaround, not an object |
| **Cross-project release with date aggregation + Align dates** | Jira Plans | Only tool with a multi-project release object — but 1 version per project and plan-only |
| **Release Vehicle Status + Health + PI Countdown + Program Board** | Jira Align | Only tool with SAFe train vocabulary (ART/PI/RV) |
| **Change Impact Analysis** correlating deployments, incidents, flags, chaos with service health | Harness SRM | "Change intelligence" — no equivalent elsewhere |
| **Approval Change Window** on ticket-based approvals | Harness | Deploy only inside the CR's window |
| **Gate evaluation options** (delay, re-evaluation interval, "succeed in the same sampling interval", min success duration) | Azure DevOps classic | Most mature automated-gate semantics |

### Gaps relevant to the product being scoped (observed across all five)
- **No tool has a "release train" object bundling multiple product versions with per-stage gate owners** — Jira Plans is closest (aggregates dates, one version per project), Octopus needs a step-template pattern, Harness/GitLab have none.
- **No tool has live runbooks with countdown timers or a deployment-window status board** — Octopus runbooks are scheduled scripts; Azure DevOps Manual Validation shows instructions only.
- **No tool exposes a "sync health" view for Jira/ServiceNow ticket sync** — GitLab documents batch sync rates and 400-item history caps; Jira Align documents field direction but no health; all ServiceNow integrations are fire-and-poll with no reconciliation UI.
- **Templated status communications** — none found; notifications are event-driven (Harness freeze/approval, Azure email to approvers), not RTE-authored comms.
- **Approval notification gaps are a recurring complaint** (GitLab #428658/#357026; Jira release-approval thread) — a notification/escalation layer around gate owners is a proven pain point.

### Pages that were unreachable or truncated
- Jira Cloud: [Create a version](https://support.atlassian.com/jira-software-cloud/docs/create-a-version-in-a-classic-project/), [What are approvals?](https://support.atlassian.com/jira-software-cloud/docs/what-are-approvals/), [How do single-space and cross-space releases differ?](https://support.atlassian.com/jira-software-cloud/docs/how-do-single-project-and-cross-project-releases-differ/), [Check the release status of a version](https://support.atlassian.com/jira-software-cloud/docs/check-the-release-status-of-a-version/), [Project versions REST API](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-project-versions/) — all returned navigation/partial text only.
- Harness: all `developer.harness.io/docs/...` URLs → 404 (incl. Approvals & Ticketing FAQs). ServiceNow Store "Integration for Harness" → metadata only. SNOW Harness/Change Velocity page → intro only.
- Initial direct WebFetch attempts on hand-typed URLs were refused ("PROVENANCE_REQUIRED"); only URLs surfaced by WebSearch could be opened, and the search budget (200) was exhausted at the end.