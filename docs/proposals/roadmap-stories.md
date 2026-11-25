# Roadmap: stories, sprints and points (Sprints 8 to 13)

**Created:** 25 November 2026 in Jira project REOS, board 34. **Jira is the source of truth;** this file is the readable index. Decisions behind it: D54 to D64 in `docs/DECISIONS.md`. Sprints are 1 week by the board's habit (Sprint 7 ran 25 Oct to 1 Nov); the new sprints (ids 498 to 503) have goals but **no dates** until you start them. Points are Fibonacci relative sizes, **not yet calibrated**: no velocity exists for this kind of work, so treat the per-sprint totals as a planning signal and rebalance after Sprints 8 and 9.

## Epics

| Epic | Theme |
|---|---|
| REOS-157 | Platform: public API, webhooks, email, integrations |
| REOS-158 | CAB module: lightweight change advisory board add-on |
| REOS-159 | Governed AI: advisory risk, drafting, read-only query |
| REOS-160 | SaaS foundation: Postgres, tenancy, SSO/SCIM, billing, regions |
| REOS-90 / REOS-9 | existing epics that received the Figma task and the pilot |

## Sprint 7 (current, closing)

REOS-142 (extend QA), REOS-155 (Service catalog screen), REOS-156 (Environments screen): branches built, merge being regressed locally, then Done after sign-off.

**Status 2026-11-25:** REOS-162, REOS-163 and REOS-161 (the public API foundations of D61) are built on branch `reos-api-v1` (draft PR, not merged): `docs/API_VERSIONING.md`, `docs/api/public-v1.openapi.json`, judgement calls in `docs/QUESTIONS.md` Q-161a.

## Sprint 8: close v1 and lay foundations (50 pts plus the 5 cross-browser stories REOS-149 to 153)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-161 | Public API v1: surface, versioning, OpenAPI | 157 | 8 |
| REOS-162 | API tokens: scoped, hashed, rotatable | 157 | 5 |
| REOS-163 | Public API conventions: errors, pagination, idempotency, rate-limit headers | 157 | 5 |
| REOS-174 | Module entitlements with an "Enable" button | 158 | 8 |
| REOS-193 | Decision: self-hosted edition alongside SaaS | 160 | 2 |
| REOS-194 | Postgres port plan (inventory, strategy, error-order proof) | 160 | 8 |
| REOS-207 | Update the Figma frames to the token shell sizes (human or Figma tools) | 90 | 3 |
| REOS-208 | Pilot: one real release (human-led) | 9 | 8 |
| REOS-209 | Hands-on ServiceNow trial (human) | 158 | 3 |
| REOS-149 to 153 | Cross-browser: CI projects, Firefox, Edge, Safari, gate | none | not sized |

## Sprint 9: webhooks, CAB data, AI platform, dual-dialect oracle (76 pts)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-164 | Outbound signed webhooks | 157 | 13 |
| REOS-165 | Developer documentation and Integrations settings | 157 | 5 |
| REOS-175 | CAB data model and services (schema first) | 158 | 13 |
| REOS-176 | Change request screens with visible risk rules | 158 | 13 |
| REOS-177 | Separation of duties for change approval (database-enforced) | 158 | 3 |
| REOS-183 | AI platform: provider abstraction, routing, secrets | 159 | 8 |
| REOS-184 | AI audit trail and inventory | 159 | 8 |
| REOS-195 | Dual-dialect schema oracle (Postgres) | 160 | 13 |

## Sprint 10: CAB workbench, email, AI guards, Postgres spike (68 pts)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-166 | Inbound signed webhook receiver | 157 | 8 |
| REOS-167 | Email delivery, preferences, bounce handling | 157 | 13 |
| REOS-178 | CAB meeting scheduling and invitations | 158 | 5 |
| REOS-179 | CAB workbench screen | 158 | 13 |
| REOS-180 | Conflict, freeze and blackout visibility | 158 | 5 |
| REOS-185 | AI allowance, ledger and caps | 159 | 8 |
| REOS-186 | AI safety: role-scoped retrieval, injection tests | 159 | 8 |
| REOS-196 | EF Core Postgres spike | 160 | 8 |

## Sprint 11: Jira write-back, AI risk and comms, tenancy, trigger port 1 (73 pts)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-168 | Jira write-back and inbound webhooks | 157 | 13 |
| REOS-181 | CAB minutes PDF, evidence pack, metrics | 158 | 8 |
| REOS-182 | CAB module QA (module-off regression) | 158 | 5 |
| REOS-187 | Explainable release risk score (advisory) | 159 | 13 |
| REOS-188 | Drafted release notes and comms with citations | 159 | 8 |
| REOS-197 | Trigger port batch 1: core governance | 160 | 13 |
| REOS-199 | Tenant model and row-level security | 160 | 13 |

## Sprint 12: ServiceNow and GitHub, AI summaries and query, SSO, queue, secrets, trigger port 2 (86 pts, heaviest)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-169 | ServiceNow write-back | 157 | 13 |
| REOS-170 | GitHub App: release manifest | 157 | 8 |
| REOS-189 | Readiness and evidence summaries | 159 | 5 |
| REOS-190 | Read-only MCP server and natural-language query | 159 | 13 |
| REOS-198 | Trigger port batch 2 and data migration | 160 | 13 |
| REOS-200 | Per-tenant SSO and SCIM | 160 | 13 |
| REOS-201 | Durable job queue | 160 | 13 |
| REOS-202 | Per-tenant secrets and KMS | 160 | 8 |

## Sprint 13: gates in GitHub, Microsoft 365, Slack, drafting, billing, regions (84 pts)

| Key | Story | Epic | Pts |
|---|---|---|---|
| REOS-171 | GitHub deployment protection rule | 157 | 8 |
| REOS-172 | Microsoft 365: Outlook calendar and Teams | 157 | 13 |
| REOS-173 | Slack app with interactive approvals | 157 | 8 |
| REOS-191 | Runbook and rollback drafting | 159 | 8 |
| REOS-192 | PIR drafting and clustering | 159 | 5 |
| REOS-203 | Plans, entitlements and billing | 160 | 13 |
| REOS-204 | Regional stacks and data residency | 160 | 13 |
| REOS-205 | Tenant export, deletion, offboarding | 160 | 8 |
| REOS-206 | Per-tenant observability, SLOs, status page | 160 | 8 |

**Total: 437 points across Sprints 8 to 13** (49 new issues, REOS-161 to 209 plus the earlier epics REOS-157 to 160).

## Dependencies worth watching

- The edition decision (REOS-193) and the Postgres plan (REOS-194) gate the whole Postgres track; they are in Sprint 8 on purpose. The owner decision cannot be skipped.
- Module entitlements (REOS-174) gates the CAB data model, the AI platform and billing.
- Tenant model (REOS-199) gates SSO/SCIM, the queue, per-tenant secrets, regions, billing and offboarding.
- The Microsoft 365 and Slack apps depend on per-tenant secrets (REOS-202), which is Sprint 12.
- Human-needed tasks (Figma frames, pilot, ServiceNow trial) have no engineering substitute.

## Honest risks

- **Sprint load rises** from 50 to 86 points; Sprints 12 and 13 look too heavy. Re-plan after measuring Sprints 8 and 9.
- **Postgres port estimates are guesses** until the plan (REOS-194) and spike (REOS-196) finish.
- **AI provider and billing provider choices** are not made; both appear inside stories and need owner input before Sprint 9 and 13.
- **Third-party limits** (Atlassian review, GitHub custom rules in preview, Slack Marketplace) can change; each integration story records them.
- Cross-browser stories (REOS-149 to 153) are unsized and sit beside the heavy Sprint 8; consider moving some to Sprint 9.
