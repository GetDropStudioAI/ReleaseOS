import { useCallback, useEffect, useState } from 'react'
import { ApiError, type Me } from '../api.ts'
import { errMsg } from '../format.ts'
import { canAdmin } from '../shell/nav.ts'
import { listTokens } from '../tokens/tokensApi.ts'
import { getOverview } from '../webhooks/webhooksApi.ts'
import type { ApiToken } from '../tokens/tokensLogic.ts'
import type { Subscription } from '../webhooks/webhooksLogic.ts'
import { ApiTokens } from '../tokens/ApiTokens.tsx'
import { Webhooks } from '../webhooks/Webhooks.tsx'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, MetricCard, NoPermissionState, Notice, PageHeading, Panel, StatusChip, TabPanel, Tabs, Time, TwoLine, type Column } from '../ui'
import '../mgmt/mgmt.css'
import '../tokens/tokens.css'
import './integrations.css'
import { DOCS, OPENAPI_PATH, TABS, byHealth, healthDetail, overallHealth, quickstart, subscriptionHealth, tokenSummary, webhookSummary, type TabId } from './integrationsLogic.ts'

/**
 * Integrations (REOS-165, Management group): where an integrator's credentials and receivers are managed. One page, four tabs: an overview with the health of tokens and webhook deliveries, the API tokens
 * and the webhooks (the same screens as /api-tokens and /webhooks, drawn inside the tab, so there is one set of endpoints, rules and tests), and the developer documentation. RTE and Release Manager only
 * (the API's Admin policy); anyone else gets the no-permission page and no API is called. The overview adds no endpoint: it reads GET /api-tokens and GET /webhooks.
 */
const WHO = 'Release Train Engineers and Release Managers'
const TRAIL = ['Management', 'Integrations']

export function Integrations({ me }: { me: Me }) {
  const [tab, setTab] = useState<TabId>('overview')
  if (!canAdmin(me.roles)) {
    return <div className="os-page"><PageHeading trail={TRAIL} title="Integrations" /><NoPermissionState what="manage integrations" who={WHO} /></div>
  }
  return (
    <div className="os-page">
      <PageHeading trail={TRAIL} title="Integrations"
        description="API tokens and signed webhooks for the systems that call ReleaseOS or listen to it, with the health of every delivery." />
      <div className="int-page">
        <Tabs label="Integrations sections" group="integrations" value={tab} onChange={t => setTab(t as TabId)} tabs={TABS} />
        <TabPanel id="overview" group="integrations">{tab === 'overview' && <Overview onGo={setTab} />}</TabPanel>
        <TabPanel id="tokens" group="integrations">{tab === 'tokens' && <ApiTokens me={me} embedded />}</TabPanel>
        <TabPanel id="webhooks" group="integrations">{tab === 'webhooks' && <Webhooks me={me} embedded />}</TabPanel>
        <TabPanel id="docs" group="integrations">{tab === 'docs' && <DeveloperDocs />}</TabPanel>
      </div>
    </div>
  )
}

function Overview({ onGo }: { onGo: (t: TabId) => void }) {
  const [tokens, setTokens] = useState<ApiToken[] | null>(null)
  const [subs, setSubs] = useState<Subscription[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [denied, setDenied] = useState(false)

  const load = useCallback(() => Promise.all([listTokens(), getOverview()]).then(([t, w]) => { setTokens(t.tokens); setSubs(w.subscriptions); setError(null) })
    .catch(e => { if (e instanceof ApiError && e.status === 403) setDenied(true); else setError(errMsg(e, 'Could not load the integrations.')) }), [])
  useEffect(() => { void load() }, [load])

  if (denied) return <NoPermissionState what="manage integrations" who={WHO} />
  if (error) return <ErrorState what="The integrations" message={error} onRetry={() => void load()} />
  if (!tokens || !subs) return <LoadingState what="the integrations" />

  const t = tokenSummary(tokens, new Date())
  const w = webhookSummary(subs)
  const overall = overallHealth(subs)
  const rows = byHealth(subs)
  const columns: Column<Subscription>[] = [
    { key: 'name', header: 'Webhook', cell: s => <TwoLine primary={s.name} secondary={<span className="mg-mono">{s.host}</span>} /> },
    { key: 'health', header: 'Delivery health', cell: s => { const h = subscriptionHealth(s); return <StatusChip tone={h.tone}>{h.word}</StatusChip> } },
    { key: 'detail', header: 'Detail', cell: s => healthDetail(s) },
  ]

  return (
    <div className="mg-stack">
      {w.deadLetters > 0 && (
        <Notice tone="danger" title={`${w.deadLetters} ${w.deadLetters === 1 ? 'delivery was' : 'deliveries were'} dead-lettered`} action={<Button onClick={() => onGo('webhooks')}>Open webhooks</Button>}>
          A receiver stayed unreachable until the attempts ran out. Fix the receiver, then replay the deliveries from the delivery log.
        </Notice>
      )}
      {t.expiringSoon > 0 && (
        <Notice tone="warning" title={`${t.expiringSoon} ${t.expiringSoon === 1 ? 'token expires' : 'tokens expire'} within 14 days`} action={<Button onClick={() => onGo('tokens')}>Open API tokens</Button>}>
          A token that expires stops working with a 401. Rotate it with a new expiry before then.
        </Notice>
      )}
      <div className="int-metrics">
        <MetricCard label="Active API tokens" value={t.active} detail={t.neverUsed > 0 ? `${t.neverUsed} never used` : t.active > 0 ? 'All have been used' : 'None made yet'} tone="neutral" />
        <MetricCard label="Active webhooks" value={w.active} detail={w.paused > 0 ? `${w.paused} paused` : w.total > 0 ? 'None paused' : 'None added yet'} tone={w.paused > 0 ? 'warning' : 'neutral'} />
        <MetricCard label="Delivery health" value={overall.value} detail={overall.detail} tone={overall.tone} />
        <MetricCard label="Next token expiry" value={t.nextExpiry ? <Time at={t.nextExpiry} date /> : 'None'} detail={t.expired > 0 ? `${t.expired} already expired` : t.nextExpiry ? 'Of the active tokens' : 'No active token expires'} tone={t.expired > 0 ? 'warning' : 'neutral'} />
      </div>
      <Panel title="Webhook delivery health" subtitle="Failing and paused webhooks come first" meta={`${w.total} ${w.total === 1 ? 'webhook' : 'webhooks'}`}>
        {rows.length === 0
          ? <EmptyState title="No webhooks yet" action={<Button variant="primary" onClick={() => onGo('webhooks')}>Add a webhook</Button>}>Add one to have ReleaseOS post signed events to your systems.</EmptyState>
          : <div className="mg-tablewrap"><DataTable caption="Webhook delivery health" columns={columns} rows={rows} rowKey={s => s.id} footer={`${w.pending} waiting · ${w.deadLetters} dead-lettered`} empty="No webhooks." /></div>}
      </Panel>
    </div>
  )
}

function DeveloperDocs() {
  const origin = window.location.origin
  return (
    <div className="mg-split">
      <Panel title="Your first call" subtitle="From nothing to a working request in a few minutes">
        <div className="mg-stack">
          <ol className="int-steps" aria-label="Quickstart steps">
            <li>On the <strong>API tokens</strong> tab, make a token with the <strong>Read</strong> access and copy it: it is shown once.</li>
            <li>Call the API with it as a bearer token:</li>
          </ol>
          <pre className="int-code" tabIndex={0} aria-label="Example request">{quickstart(origin)}</pre>
          <p className="mg-note">The answer names the person the token acts as, their role in the workspace and the workspace. A token never has more rights than that person.</p>
          <ol className="int-steps" start={3} aria-label="Webhook steps">
            <li>On the <strong>Webhooks</strong> tab, add your https address and choose the events. Copy the secret: it is shown once.</li>
            <li>Choose <strong>Send test event</strong>. Your receiver gets <code>webhook.ping</code>, signed with the secret; verify it with one of the samples.</li>
          </ol>
          <p><a href={OPENAPI_PATH}>The OpenAPI document (JSON, no token needed)</a></p>
        </div>
      </Panel>
      <div className="mg-side">
        <Panel title="Documentation" subtitle="In the repository's docs folder">
          <ul className="int-docs" aria-label="Documents">
            {DOCS.map(d => <li key={d.path}><TwoLine primary={d.title} secondary={<><code>{d.path}</code>. {d.text}</>} /></li>)}
          </ul>
        </Panel>
      </div>
    </div>
  )
}
