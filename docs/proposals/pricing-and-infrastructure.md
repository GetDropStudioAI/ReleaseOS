# Pricing options, infrastructure cost and the cost of being a SaaS

**Date:** 25 November 2026 · **Status:** proposal, nothing decided · Companion to `saas-market-and-gap-analysis.md` and `servicenow-comparison.md`

## 0. Evidence quality

- **Official** = the agent opened the vendor page or API. **Search-summary** = seen in a search result of an official page that did not render. **3rd-party** = vendor or auditor blogs with a commercial interest. **Assumption** = a modelling input, not a fact.
- AWS unit prices come from the AWS Price List API (publication 2026-11-24, on-demand, us-east-1 and eu-central-1). All **quantities, traffic and tenant sizes are assumptions**. Azure was not priced. No reserved-instance or savings-plan discounts are modelled.
- AI cost uses Anthropic's published prices. Token counts per feature and calls per tenant are assumptions and must be replaced by measurements from a pilot.
- Compliance costs have **no official price lists**; they are vendor-blog ranges.
- Every number below is an estimate for decision-making, not a quote.

## 1. How comparable tools price (Official unless marked)

| Vendor | Model | Notable figures | Viewers / approvers |
|---|---|---|---|
| Harness | Usage "units", no seat price; free plan 25 users | 180 units per service per month for deployments (Enterprise); AI metered at 1,667 units per million AI calls | Seats not charged |
| Octopus Deploy | Annual tier, plus machine and tenant add-ons | Cloud: Free $0, Professional $4,330/yr, Enterprise $24,600/yr; machines and tenants $770/yr per block | Unlimited users on paid tiers |
| LaunchDarkly | Usage; unlimited seats | $10 per service connection per month, $8.33 per 1,000 client MAU, AI $5 per 1,000 runs | Unlimited seats |
| Atlassian JSM | Per **agent** only | Standard about $21.50, Premium about $55.28 per agent per month (search-summary, low confidence) | **Requesters and approvers free** (search-summary) |
| Atlassian Compass | Per user | Free up to 3 full users; Standard $7.67, Premium $23.96 | Free plan has unlimited basic users |
| GitHub | Per user | Free, Team $4, Enterprise $21 | No viewer role |
| GitLab | Per user | Premium $29; AI credits $1 each; Ultimate includes guests | Unlimited guests on Ultimate |
| PagerDuty | Per user or **flat platform fee with seats included** | Platform: $2,800 (5 seats), $12,000 (25 seats), $48,000 (50 seats) per year; Professional $21 to $25 per user | Platform seats include stakeholder licences |
| incident.io | Per user plus on-call add-on | Team $15 to $19, Pro $25; AI SRE is an add-on, price not shown | Status-page subscribers unlimited |
| Linear / Notion / Slack | Per user | Linear $10 to $16; Notion $10 to $20 with AI credits per seat; Slack $7.25 to $15 | Guests free or limited by tier |
| Opsera, CloudBees | Quote only | Nothing published | Not stated |

**Patterns worth copying:** (1) price the people who *do the work* and let approvers and viewers in free (JSM, PagerDuty platform tiers, GitLab guests); (2) a flat platform fee with seats included is an established pattern (PagerDuty); (3) AI is either included (Harness, Linear), credit-pooled (Notion, GitLab) or metered per call (LaunchDarkly); (4) add-on modules are gated by tier or bought as a pack.

## 2. Pricing options for ReleaseOS

| # | Option | How it works | Pros | Cons | Infra fit |
|---|---|---|---|---|---|
| A | Per seat, everyone | Charge every user | Familiar | Penalises approvers, the people governance depends on; users share logins | Revenue scales with users, cost scales with tenants |
| B | **Builder seats, free viewers/approvers** | Charge only RTEs, release managers, governance officers, admins | Fits the ServiceNow complaint; easy to explain | Needs a clear definition of "builder"; sharing risk | Good |
| C | Per workspace / team | Flat price per workspace | Predictable | Big workspaces underpay | Cost is per tenant, so flat fits |
| D | Per active release train | Price by volume | Tracks value | No competitor evidence; punishes busy teams | Weak |
| E | Usage-based (events, API calls) | Pay as you go | Fair for light users | Unpredictable bills; buyers dislike metering | Mirrors cost, but hurts trust |
| F | **Platform tier fee with included builder seats, plus add-on modules** | Tiers set limits and included features; CAB module and AI are add-ons | Clear upgrade path; matches the module-switch idea; PagerDuty precedent | Tier design takes care | Good |
| G | Per environment / target | Octopus-style | Aligns with deployment scale | Penalises many small environments; not how governance buyers think | Weak |

## 3. My recommendation (opinion, not research)

**F built on B:** a platform tier per workspace with a number of builder seats included, unlimited approvers and viewers, and add-on modules switched on per tenant:

| Tier (illustrative) | Aimed at | Includes | Price anchor (hypothesis only) |
|---|---|---|---|
| Team | one team or programme | core governance, 1 workspace, 5 builders, unlimited approvers/viewers, standard integrations, email | Octopus Professional is $4,330/yr and Compass Standard $7.67 per user; test roughly $4k to $8k per year |
| Business | multi-team programme | several workspaces, more builders, executive analytics, audit export, SSO | PagerDuty Essential is $12,000/yr; test roughly $12k to $30k per year |
| Enterprise | whole organisation | SSO/SCIM, regional data, SLA, SIEM streaming, dedicated support | PagerDuty Plus is $48,000/yr; quote above about $40k per year |
| Add-on: CAB module | organisations that don't have a change system | the ServiceNow-lite module (meetings, agenda, decisions, change record) | flat annual add-on per workspace |
| Add-on: AI | organisations that want it | drafted notes, summaries, risk explanations with an included monthly credit allowance | included allowance, then credit packs |

The price points above are **anchors from the comparables, not a pricing study**; they need customer interviews. Principles I'd hold: publish prices; include approvers and viewers; never meter core features; meter only AI beyond an allowance; no per-release pricing (no evidence it works).

## 4. Infrastructure cost (estimate, AWS, list prices)

Architecture assumed: two full regions (US primary, EU for residency, about 30% of tenants in the EU), each with RDS PostgreSQL Multi-AZ, Fargate (API, SignalR, workers) behind an ALB, Valkey cache and SignalR backplane, S3 for evidence packs and exports, SQS job queue, SES email, CloudWatch, KMS, Secrets Manager, WAF and CDN. A tenant = 20 builders, 100 viewers/approvers, about 500,000 API requests per month.

| Scenario | Users | Monthly total (range) | Per tenant per month |
|---|---|---|---|
| 10 tenants | 1,200 | $1,200 to $2,200 | $120 to $220 |
| 100 tenants | 12,000 | $4,500 to $9,000 | $45 to $90 |
| 1,000 tenants | 120,000 | $28,000 to $55,000 | $28 to $55 |

What moves it: log volume and retention (biggest swing at scale), the fixed floor of the second region (why 10 tenants is expensive per tenant), per-tenant keys ($1 per tenant per month per region), and discounts not modelled (savings plans and reservations lower compute and database lines). Not priced: NAT gateway, inter-AZ and internet transfer (included as allowances), instance sizes above m7g.2xlarge.

**Reading:** at the tier anchors in section 3, infrastructure is a small share of revenue (a few percent at 100 tenants). It is not the constraint. The constraints are AI usage, compliance, support and people.

## 5. AI cost per tenant per month (estimate)

Prices used (Anthropic official page): Haiku 5.5 $0.10 input / $0.50 output per million tokens; Sonnet 5.5 $2 / $10; Opus 5.5 $4 / $20; batch API 50% off; cache reads about 5% of fresh input price.

| Usage level (assumed calls per month) | Haiku 5.5 | Sonnet 5.5 | Opus 5.5 |
|---|---|---|---|
| Low (20 risk, 5 notes, 20 summaries, 100 chat queries) | about $0.41 | about $8 | about $16 |
| Medium (100, 20, 100, 500) | about $2 | about $41 | about $82 |
| High (500, 60, 500, 3,000) | about $12 | about $238 | about $475 |

Chat and tool-using queries are about 85 to 90% of the cost in every row. At 1,000 tenants all on medium usage with Sonnet, that is about $41,000 per month, about the same as the entire infrastructure line. **Therefore:** AI needs an allowance and a credit pack, not unlimited inclusion; use Haiku-class models for most calls and Sonnet-class only for drafting; cap or meter chat first; use caching and batch for scheduled summaries. OpenAI and Google prices were not verified.

## 6. Costs that are not infrastructure

| Item | Estimate (3rd-party, wide ranges) |
|---|---|
| SOC 2 Type II | audit $8k to $60k for a small company ($18k to $110k mid-tier firms); first-year programme $25k to $200k+ with tooling and staff time; re-audit about 70 to 80% of the first |
| ISO 27001 | certification audit $8k to $60k; programme $50k to $200k; surveillance about $5k per year |
| Penetration test | about $8k to $15k for a focused SaaS test; more with SSO, roles and integrations |
| Cyber insurance, support staffing | not found; these are quotes and headcount decisions |

**Marketplace revenue shares (Official unless noted; confirm before contracting):** Microsoft AppSource 3% store fee on transactable offers; GitHub Marketplace 5% (paid plans need a verified publisher); Atlassian Forge apps partner payout 84% from April 2026 and 83% from October 2026 (100% until $1M lifetime Forge revenue; Connect apps pay more; sources show conflicting dates); Slack not found.

## 7. What this means for decisions

1. **Infrastructure is affordable.** Even the pessimistic 10-tenant case is low four figures a month. Do not let infrastructure cost drive the architecture choices; let security, correctness (the trigger contract) and operations drive them.
2. **AI is the cost to design around.** An unmetered chat feature is the only line that can exceed infrastructure.
3. **The largest early costs are compliance and people.** SOC 2 and ISO 27001 take a long lead time. Support and on-call staffing were not costed.
4. **Free approvers fit the cost model.** Approvers add little infrastructure cost and add stickiness.
5. **Two things to decide before anything is built:** pricing shape (B/F recommended), and the AI allowance policy.

## 8. What I could not verify

Official Jira and JSM price tables (page did not render); free viewers in Jira; CloudBees, Opsera, Harness Essentials, PagerDuty add-ons, incident.io AI, GitHub Copilot and Advanced Security prices; NAT and data transfer costs; ElastiCache rows (several per node type, highest used); Azure prices; OpenAI and Google API token rates; Slack Marketplace terms; any cost for cyber insurance or support staff. All unit-economics inputs (token counts, call volumes, tenant mix, log volume) should be replaced with measurements from a pilot.
