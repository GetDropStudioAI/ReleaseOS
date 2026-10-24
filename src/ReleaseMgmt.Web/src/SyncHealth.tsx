import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, delIfMatch, get, getStream, post, type Me, type StreamRow } from './api'
import { notifySyncChanged, onSyncChanged, SYSTEM_NAMES, type ConnectorView, type FailingEntry, type SyncStateDto } from './SyncBanner'
import { announce } from './announce'
import { useFocusWhen } from './focus'
import { useRowNav } from './rowNav'
import { serverNow } from './time'
import { useAction } from './useAction'
import { dueText } from './shell/approvalsApi.ts'
import { Field, SelectInput, TextInput, When } from './mgmt/forms'
import {
  Button, ConfirmDialog, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, MetricCard, PageHeading, Panel, PropertyList, ReadOnlyNotice, StatusChip, TwoLine,
  type Column, type Tone,
} from './ui'
import './sync.css'

/**
 * Screen 5, Sync health (REOS-42, mockups/SyncHealth.html): connector table, mismatches, open alerts with counts, resolved history, the alert detail
 * in the right-hand column (same split layout as Admin and Audit), and the webhook allowlist. It refetches when a SyncAlertRaised push arrives (App
 * forwards it through notifySyncChanged), so a new alert appears without a reload. Keyboard, while focus is in the alert tables: j / k move the selection
 * (and focus), Enter moves focus into the detail, Esc closes it. REOS-130 (G8) restyled it into the redesign (D41): ui/ components and tokens, status as chips, times in UTC with the local time beside (D44), removing a channel behind a ConfirmDialog; resolving an alert keeps its inline confirm (Q-130a). See Q-042a..f.
 */
interface AlertItem {
  id: string; trainId: string | null; trainTitle: string | null; source: string; kind: string; fingerprint: string; message: string; occurrenceCount: number
  firstOccurredAt: string; lastOccurredAt: string; isResolved: boolean; resolvedAt: string | null; resolvedByUserId: string | null; resolvedByName: string | null; version: number
}
interface AlertPage { items: AlertItem[]; openCount: number; resolvedCount: number; resolvedWithinDays: number; limit: number; asOf: string }
interface LinkProblem { id: string; trainId: string; trainTitle: string | null; entityType: string; entityId: string; source: string; externalKey: string; expected: string | null; reported: string | null; syncState: string; lastSyncedAt: string | null }
interface WebhookRow { id: string; name: string; kind: string; host: string; displayUrl: string; version: number; usedByTeams: number; usedByDispatches: number }

const SOURCES = ['Jira', 'ServiceNow', 'SyncEngine', 'Backup', 'Export', 'Webhook', 'Notifications']
const KINDS = ['AuthFailed', 'Unreachable', 'NotFound', 'Mismatch', 'RateLimited', 'ParseError', 'Stalled', 'BackupFailed', 'ExportFailed', 'DeliveryFailed']
const WINDOWS: { days: number; label: string }[] = [{ days: 1, label: '24 h' }, { days: 7, label: '7 days' }, { days: 30, label: '30 days' }]
const STALE_MIN = 15   // display only: the server's Sync:StaleAfterMinutes decides what counts as stale

/** Cause, what it affects and how to clear it, per alert kind (the mockup's Inspector). Static guidance: nothing here is read from the alert. */
const KIND_INFO: Record<string, { cls: 'bad' | 'warn'; glyph: string; cause: string; impact: string; steps: string[] }> = {
  AuthFailed: { cls: 'bad', glyph: '✗', cause: 'The credential was rejected: a password rotated, or a token revoked or expired, on the other system.',
    impact: 'Links to this system show as stale, with their last known state and time. Lockout does not read Jira or ServiceNow, so no train is blocked by this outage.',
    steps: ['An admin re-enters the credential in Connectors', 'Wait for the next cycle (every 5 min, every 1 min during a deployment window)', 'The alert resolves itself after one good cycle; history stays'] },
  Unreachable: { cls: 'bad', glyph: '✗', cause: 'The system did not answer: a network, DNS or firewall problem, or an outage on its side.',
    impact: 'Links to this system stop refreshing and show as stale with their last known state.', steps: ['Check the system status page and the network path from this server', 'The alert resolves itself after one good cycle; history stays'] },
  NotFound: { cls: 'bad', glyph: '✗', cause: 'The ticket or version was deleted or moved.', impact: 'That one link cannot be checked; the rest of the connector is fine.',
    steps: ['Open the train and relink or remove the link', 'The alert resolves itself after one good cycle; history stays'] },
  Mismatch: { cls: 'warn', glyph: '▲', cause: 'This app and the ticket disagree (see Mismatches above for the rule).', impact: 'Shown, never blocking (open item 9: an open mismatch does not block Gated to Executing).',
    steps: ['Fix whichever side is wrong', 'The alert resolves itself after one good cycle; history stays'] },
  RateLimited: { cls: 'warn', glyph: '▲', cause: 'The system answered 429 Too Many Requests; the poller backs off and retries.', impact: 'Data may be a few minutes older than usual.',
    steps: ['Nothing to do unless it keeps recurring; then lower the polling load', 'Cleared automatically after one good cycle'] },
  ParseError: { cls: 'bad', glyph: '✗', cause: 'The reply was not in the shape the connector expects (an API change or a customised field).', impact: 'Links from that system cannot be read until the connector is fixed.',
    steps: ['Report it with the message shown here', 'The alert resolves itself after one good cycle; history stays'] },
  Stalled: { cls: 'bad', glyph: '▲', cause: 'The watchdog saw no completed sync cycle for three intervals: the poller is stopped or stuck.', impact: 'Nothing from Jira or ServiceNow is current on any screen.',
    steps: ['Check the server log and restart the app if the poller is stuck', 'The alert resolves itself after one good cycle; history stays'] },
  BackupFailed: { cls: 'bad', glyph: '✗', cause: 'A database backup did not complete.', impact: 'Recovery would lose more than the 15 minutes the plan allows.', steps: ['Check disk space and the backup folder permissions', 'Cleared after the next good backup'] },
  ExportFailed: { cls: 'bad', glyph: '✗', cause: 'An export (PDF, XLSX or CSV job) failed.', impact: 'The requested file was not produced.', steps: ['Retry the export; if it fails again, report the message shown here'] },
  DeliveryFailed: { cls: 'bad', glyph: '✗', cause: 'A webhook post did not get through (rejected, timed out or blocked).', impact: 'The channel did not receive the message; the dispatch log records the failure.', steps: ['Check the address on the allowlist below', 'Re-send from the communication drawer'] },
}
const kindInfo = (k: string) => KIND_INFO[k] ?? { cls: 'bad' as const, glyph: '✗', cause: 'A background task failed.', impact: 'See the message.', steps: ['Report the message shown here'] }


const tone = (c: 'bad' | 'warn'): Tone => (c === 'bad' ? 'danger' : 'warning')
const utcText = (iso: string) => dueText({ dueAt: iso, dueOn: null }).utc
function ago(iso: string | null): string {
  if (!iso) return 'never'
  const m = Math.max(0, Math.round((serverNow() - Date.parse(iso)) / 60000))
  return m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${m % 60} min`
}
const minutesSince = (iso: string | null) => (iso ? Math.max(0, (serverNow() - Date.parse(iso)) / 60000) : Infinity)
const hostOf = (u: string) => { try { return new URL(u).host } catch { return u } }

/** What the browser can tell before the server does; the server is still the authority (same rules, WebhookUrlPolicy). */
export function urlProblem(v: string): string | null {
  const t = v.trim()
  if (!t) return null
  if (/^http:\/\//i.test(t)) return 'Only https:// addresses are allowed. A webhook carries release data, and its address is a secret.'
  if (!/^https:\/\//i.test(t)) return 'Start the address with https://'
  let u: URL
  try { u = new URL(t) } catch { return 'That is not a complete address.' }
  if (u.username || u.password) return 'Remove the user name and password from the address.'
  if (u.hash) return 'Remove the #fragment; it is never sent to the server.'
  return null
}

const errText = (e: unknown) => (e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message)

function navigate(path: string) { window.history.pushState(null, '', path); window.dispatchEvent(new PopStateEvent('popstate')) }

const trainPath = (id: string) => `/trains/${encodeURIComponent(id)}`
/** A real link to the train (href for new tab / copy link), opened in place like every other in-app link. */
function TrainLink({ id, title }: { id: string; title: string }) {
  return <a className="os-link" href={trainPath(id)} onClick={e => { if (e.button === 0 && !e.metaKey && !e.ctrlKey && !e.shiftKey) { e.preventDefault(); navigate(trainPath(id)) } }}>{title}</a>
}

function connectorColumns(failingFor: (source: string) => FailingEntry | undefined): Column<ConnectorView>[] {
  const count = (pick: (c: ConnectorView) => number): Column<ConnectorView>['cell'] => c => (c.links.total === 0 ? <span className="os-muted">None</span> : pick(c))
  return [
    { key: 'connector', header: 'Connector', cell: c => <TwoLine primary={SYSTEM_NAMES[c.source] ?? c.source} secondary={hostOf(c.baseUrl)} /> },
    { key: 'last', header: 'Last success (UTC)', cell: c => {
      const stale = c.isEnabled && minutesSince(c.lastSuccessAt) > STALE_MIN
      return c.lastSuccessAt
        ? <span className="sy-stack"><When iso={c.lastSuccessAt} /><span className="sy-note">{ago(c.lastSuccessAt)} ago</span>{stale && <StatusChip tone="warning">Stale</StatusChip>}</span>
        : <span className="os-muted">Never</span>
    } },
    { key: 'links', header: 'Links', align: 'end', cell: c => c.links.total },
    { key: 'sync', header: 'In sync', align: 'end', cell: count(c => c.links.inSync) },
    { key: 'mismatch', header: 'Mismatch', align: 'end', cell: count(c => c.links.mismatch) },
    { key: 'broken', header: 'Broken', align: 'end', cell: count(c => c.links.broken) },
    { key: 'stale', header: 'Stale', align: 'end', cell: count(c => c.links.stale) },
    { key: 'alerts', header: 'Open alerts', align: 'end', cell: c => c.openAlerts },
    { key: 'state', header: 'State', cell: c => {
      const f = failingFor(c.source)
      switch (c.state) {
        case 'Healthy': return <StatusChip tone="success">Healthy</StatusChip>
        case 'Failing': return <TwoLine primary={<StatusChip tone="danger">{f?.kind === 'AuthFailed' ? 'Auth failed' : f?.kind === 'Unreachable' ? 'Unreachable' : 'Failing'}</StatusChip>} secondary={f?.since ? `since ${utcText(f.since)}` : undefined} />
        case 'Degraded': return <StatusChip tone="warning">Degraded</StatusChip>
        case 'NotRun': return <StatusChip tone="neutral">Not run yet</StatusChip>
        default: return <StatusChip tone="neutral">Disabled</StatusChip>
      }
    } },
  ]
}

const mismatchColumns: Column<LinkProblem>[] = [
  { key: 'train', header: 'Release', cell: l => <TrainLink id={l.trainId} title={l.trainTitle ?? l.trainId} /> },
  { key: 'node', header: 'Node', cell: l => l.entityType },
  { key: 'ticket', header: 'Ticket', cell: l => l.externalKey, mono: true },
  { key: 'implies', header: 'This app implies', cell: l => l.expected ?? <span className="os-muted">None</span> },
  { key: 'says', header: 'Ticket says', cell: l => <TwoLine primary={<StatusChip tone={l.syncState === 'Mismatch' ? 'warning' : 'danger'}>{l.syncState === 'Mismatch' ? 'Mismatch' : l.syncState === 'NotFound' ? 'Not found' : 'Auth failed'}</StatusChip>} secondary={l.syncState === 'Mismatch' ? (l.reported ?? 'differs') : undefined} /> },
  { key: 'synced', header: 'Last synced (UTC)', cell: l => <When iso={l.lastSyncedAt} /> },
]

function Detail({ a, canAdmin, busy, confirming, setConfirming, onResolve, onClose, onCopyError, closeRef }: {
  a: AlertItem; canAdmin: boolean; busy: boolean; confirming: boolean; setConfirming: (v: boolean) => void; onResolve: () => void; onClose: () => void; onCopyError: (m: string) => void
  closeRef: React.RefObject<HTMLButtonElement | null>
}) {
  const info = kindInfo(a.kind)
  const connector = a.source === 'Jira' || a.source === 'ServiceNow'
  const resolveRef = useRef<HTMLButtonElement>(null)
  const confirmRef = useFocusWhen<HTMLButtonElement>(confirming)
  const cancel = () => { setConfirming(false); requestAnimationFrame(() => resolveRef.current?.focus()) }
  const copy = async () => {
    try { await navigator.clipboard.writeText(a.fingerprint); announce('Fingerprint copied.') }
    catch { onCopyError('The browser did not allow copying. Select the fingerprint and copy it by hand.') }
  }
  return (
    <Panel title={`${SYSTEM_NAMES[a.source] ?? a.source} ${a.kind}`} subtitle={`${a.occurrenceCount} occurrence${a.occurrenceCount === 1 ? '' : 's'}`}
      meta={a.isResolved ? <StatusChip tone="success">Resolved</StatusChip> : <StatusChip tone={tone(info.cls)}>Open</StatusChip>}>
      <div className="sy-body">
        <PropertyList items={[
          { label: 'Fingerprint', mono: true, value: <span className="sy-fp">{a.fingerprint} <Button onClick={() => void copy()}>Copy</Button></span> },
          { label: 'State', value: a.isResolved ? `Resolved ${utcText(a.resolvedAt!)} · ${a.resolvedByName ?? 'cleared automatically after a clean cycle'}` : 'Open' },
          { label: 'First (UTC)', value: <When iso={a.firstOccurredAt} /> },
          { label: 'Last (UTC)', value: <When iso={a.lastOccurredAt} /> },
          { label: 'Scope', value: a.trainId ? <TrainLink id={a.trainId} title={a.trainTitle ?? a.trainId} /> : 'All releases' },
          { label: 'Message', mono: true, value: a.message },
          { label: 'Likely cause', value: info.cause },
        ]} />
        <div><h3 className="sy-h3">What is affected</h3><p className="sy-p">{info.impact}</p></div>
        <div><h3 className="sy-h3">To clear it</h3><ol className="sy-steps">{info.steps.map(s => <li key={s}>{s}</li>)}</ol></div>
        <div><h3 className="sy-h3">Who was told</h3><p className="sy-p">The banner on every open screen while a whole connector is failing; an in-app notice to RTEs and Release Managers when the alert was first raised; the team webhook after 3 consecutive failures (PROJECT_SCOPE 5.3).</p></div>
        <div className="sy-actions">
          {connector && canAdmin && <Button onClick={() => navigate('/connectors')}>Open Connectors</Button>}
          {!a.isResolved && canAdmin && !confirming && <Button ref={resolveRef} variant="primary" onClick={() => setConfirming(true)}>Resolve now</Button>}
          <Button ref={closeRef} onClick={onClose}>Close (Esc)</Button>
        </div>
        {!a.isResolved && canAdmin && confirming && (
          <div role="group" aria-label="Confirm resolve" className="sy-confirm">
            <p className="sy-p">Mark this alert resolved? The row stays in the history. If the fault is still there, the next failed cycle raises it again.</p>
            <div className="sy-actions">
              <Button ref={confirmRef} variant="primary" disabled={busy} onClick={onResolve}>{busy ? 'Resolving…' : 'Confirm'}</Button>
              <Button disabled={busy} onClick={cancel}>Cancel</Button>
            </div>
          </div>
        )}
        {!a.isResolved && !canAdmin && <p className="sy-note">Only RTEs and Release Managers resolve alerts by hand.</p>}
      </div>
    </Panel>
  )
}

function alertColumns(selected: string | null, onSelect: (id: string) => void, resolved: boolean): Column<AlertItem>[] {
  return [
    { key: 'kind', header: 'Kind', cell: a => {
      const info = kindInfo(a.kind)
      return resolved ? <TwoLine primary={a.kind} secondary={<StatusChip tone="success">Resolved</StatusChip>} /> : <StatusChip tone={tone(info.cls)}>{a.kind}</StatusChip>
    } },
    { key: 'system', header: 'System', cell: a => SYSTEM_NAMES[a.source] ?? a.source },
    { key: 'message', header: 'Message', cell: a => <span className="sy-msg">{a.message}</span> },
    { key: 'first', header: 'First (UTC)', cell: a => <When iso={a.firstOccurredAt} /> },
    { key: 'last', header: 'Last (UTC)', cell: a => <When iso={a.lastOccurredAt} /> },
    { key: 'count', header: 'Count', align: 'end', cell: a => a.occurrenceCount },
    { key: 'scope', header: 'Scope', cell: a => a.trainTitle ?? 'All releases' },
    ...(resolved ? [{ key: 'resolved', header: 'Resolved (UTC)', cell: (a: AlertItem) => <span className="sy-stack"><When iso={a.resolvedAt} /><span className="sy-note">{a.resolvedByName ? `by ${a.resolvedByName}` : 'automatic'}</span></span> }] : []),
    { key: 'details', header: <span className="sr-only">Actions</span>, cell: a => (
      <Button data-rownav data-alert={a.id} aria-current={a.id === selected ? 'true' : undefined} onClick={() => onSelect(a.id)}
        aria-label={`Details of ${a.source} ${a.kind}${resolved ? ', resolved' : ''}`}>Details</Button>) },
  ]
}

export function SyncHealth({ me }: { me: Me }) {
  const canAdmin = me.roles.some(r => r === 'RTE' || r === 'ReleaseManager')
  const [state, setState] = useState<SyncStateDto | null>(null)
  const [alerts, setAlerts] = useState<AlertPage | null>(null)
  const [links, setLinks] = useState<LinkProblem[] | null>(null)
  const [hooks, setHooks] = useState<WebhookRow[] | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [filters, setFilters] = useState({ source: '', kind: '', train: '' })
  const [days, setDays] = useState(7)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [confirming, setConfirming] = useState(false)
  const { run, pending } = useAction()
  const busy = pending !== null
  const [actionError, setActionError] = useState<string | null>(null)
  const seq = useRef(0)
  const closeRef = useRef<HTMLButtonElement | null>(null)

  useEffect(() => { getStream().then(setTrains).catch(() => setTrains([])) }, [])   // only feeds the Release filter; a failure leaves it empty and the rest of the screen says so if the API is down

  const load = useCallback(async () => {
    const mine = ++seq.current
    const q = new URLSearchParams({ state: 'all', days: String(days) })
    if (filters.source) q.set('source', filters.source)
    if (filters.kind) q.set('kind', filters.kind)
    if (filters.train) q.set('train', filters.train)
    try {
      const [s, a, l, h] = await Promise.all([
        get<SyncStateDto>('/api/v1/sync/state'), get<AlertPage>(`/api/v1/sync/alerts?${q}`),
        get<LinkProblem[]>('/api/v1/sync/mismatches'), get<WebhookRow[]>('/api/v1/sync/webhook-allowlist'),
      ])
      if (mine !== seq.current) return   // a newer load superseded this one
      setState(s); setAlerts(a); setLinks(l); setHooks(h); setError(null)
    } catch (e) {
      if (mine === seq.current) setError(errText(e))
    }
  }, [filters, days])

  const loadRef = useRef(load); loadRef.current = load
  useEffect(() => { void load() }, [load])
  useEffect(() => onSyncChanged(() => { void loadRef.current() }), [])   // SyncAlertRaised, a reconnect, or an alert resolved here

  const open = useMemo(() => alerts?.items.filter(a => !a.isResolved) ?? [], [alerts])
  const done = useMemo(() => alerts?.items.filter(a => a.isResolved) ?? [], [alerts])
  const ordered = useMemo(() => [...open, ...done], [open, done])
  const sel = ordered.find(a => a.id === selected) ?? null
  useEffect(() => { setConfirming(false); setActionError(null) }, [selected])

  const select = useCallback((id: string | null) => setSelected(id), [])
  // j / k / Enter only while focus is in the two alert tables (WCAG 2.1.4); the open and resolved rows are one list, in that order.
  const [, , nav] = useRowNav<HTMLDivElement>(ordered.length,
    i => { if (ordered[i]) { setSelected(ordered[i].id); requestAnimationFrame(() => closeRef.current?.focus()) } },
    i => { if (ordered[i]) setSelected(ordered[i].id) })
  /** Close the detail and give focus back to the alert's Details button, so it does not fall to <body> with the Close button. */
  const close = () => {
    const id = selected
    setSelected(null)
    requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-alert="${id}"]`)?.focus())
  }
  const onEsc = (e: React.KeyboardEvent) => {
    const t = e.target as HTMLElement
    if (e.key === 'Escape' && selected !== null && !['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName)) { e.preventDefault(); close() }
  }

  const resolve = () => run('Resolving…', async () => {
    if (!sel) return
    setActionError(null)
    try {
      await post(`/api/v1/sync/alerts/${sel.id}:resolve`, undefined, sel.version)
      setConfirming(false)
      announce('Alert resolved. It stays in the resolved history.')
      requestAnimationFrame(() => closeRef.current?.focus())   // Resolve now and Confirm are gone; focus stays in the detail
      notifySyncChanged()   // this screen, the banner, and every other subscriber refetch
    } catch (e) {
      setActionError(e instanceof ApiError && e.status === 409 ? 'This alert changed since you loaded it. The list has been refreshed; check it and try again.' : errText(e))
      void load()
    }
  })

  const failingFor = (source: string) => state?.failing.find(f => f.source === source && f.scope === 'connector')
  const last = state?.watchdog.lastCycleCompletedAt ?? null
  const filtered = !!(filters.source || filters.kind || filters.train)
  const healthy = state?.connectors.filter(c => c.state === 'Healthy').length ?? 0
  const windowLabel = alerts && (alerts.resolvedWithinDays === 1 ? '24 h' : `${alerts.resolvedWithinDays} days`)

  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Sync health']} title="Sync health"
        description="Read-only. Connectors are polled every 5 minutes, every minute during a deployment window. Times are UTC, with your local time beside them." />
      {error && <ErrorState what="Sync health" message={error} onRetry={() => void load()} />}
      {!state && !error && <LoadingState what="sync health" />}

      {state && (
        <>
          <div className="sy-metrics">
            <MetricCard label="Open alerts" value={state.openAlerts} detail={state.openAlerts > 0 ? 'Errors waiting to clear' : 'None open'} tone={state.openAlerts > 0 ? 'warning' : 'success'} />
            <MetricCard label="Connectors healthy" value={`${healthy} of ${state.connectors.length}`} detail={state.connectorWide ? 'A connector is failing as a whole' : 'None failing as a whole'} tone={state.connectorWide ? 'danger' : 'success'} />
            <MetricCard label="Last sync cycle" value={last ? ago(last) : 'None yet'} unit={last ? 'ago' : undefined} detail={last ? utcText(last) : 'No sync cycle has completed yet'} tone="neutral" />
            <MetricCard label="Watchdog" value={state.watchdog.stalled ? 'Stalled' : last ? 'Heartbeat ok' : 'Waiting'}
              detail={state.watchdog.stalled ? 'Sync stalled: no cycle for 15 min' : 'Raises “sync stalled” after 15 min without a cycle'} tone={state.watchdog.stalled ? 'danger' : last ? 'success' : 'neutral'} />
          </div>

          <Panel label="Connectors" title="Connectors" subtitle="State from the poller; “stale” marks links not refreshed for three intervals" meta={`${state.connectors.length} configured`}>
            {state.connectors.length === 0
              ? <EmptyState title="No connectors are configured yet">{canAdmin ? 'Add Jira and ServiceNow under Connectors.' : 'An RTE or Release Manager adds them under Connectors.'}</EmptyState>
              : <DataTable caption="Connectors" columns={connectorColumns(failingFor)} rows={state.connectors} rowKey={c => c.source} />}
          </Panel>
        </>
      )}

      {links && (
        <Panel label="Mismatches" title="Mismatches" subtitle="This app and the ticket disagree" meta={`${links.length} open`}>
          {links.length === 0
            ? <EmptyState title="Every synced link agrees with its ticket" />
            : <DataTable caption="Mismatched links" columns={mismatchColumns} rows={links} rowKey={l => l.id} />}
        </Panel>
      )}

      {alerts && (
        <form className="sy-filters" aria-label="Alert filters" onSubmit={e => e.preventDefault()}>
          <FilterSelect label="System" value={filters.source} onChange={v => setFilters(f => ({ ...f, source: v }))}
            options={[{ value: '', label: 'All systems' }, ...SOURCES.map(s => ({ value: s, label: SYSTEM_NAMES[s] ?? s }))]} />
          <FilterSelect label="Kind" value={filters.kind} onChange={v => setFilters(f => ({ ...f, kind: v }))}
            options={[{ value: '', label: 'All kinds' }, ...KINDS.map(k => ({ value: k, label: k }))]} />
          <FilterSelect label="Release" value={filters.train} onChange={v => setFilters(f => ({ ...f, train: v }))}
            options={[{ value: '', label: 'All releases' }, ...trains.map(t => ({ value: t.id, label: t.title }))]} />
          {filtered && <Button onClick={() => setFilters({ source: '', kind: '', train: '' })}>Clear filters</Button>}
        </form>
      )}

      <div className="sy-split" onKeyDown={onEsc}>
        <div className="sy-lists">
          {alerts && (
            <div {...nav} className="sy-lists">
              <Panel label="Open alerts" title="Open alerts" subtitle="Errors, one row per fingerprint, repeats counted" meta={`${alerts.openCount} open`}>
                {open.length === 0
                  ? <EmptyState title={`No open alerts${filtered ? ' match these filters' : ''}`} />
                  : <DataTable caption="Open alerts" columns={alertColumns(selected, select, false)} rows={open} rowKey={a => a.id} />}
              </Panel>
              <Panel label="Resolved alerts" title="Resolved history" subtitle="Cleared automatically after one good cycle, or by hand"
                meta={<span className="sy-meta">{alerts.resolvedCount} in the last {windowLabel}
                  <FilterSelect label="History window" value={String(days)} onChange={v => setDays(Number(v))} options={WINDOWS.map(w => ({ value: String(w.days), label: w.label }))} /></span>}
                footer={alerts.items.length >= alerts.limit ? `Showing the newest ${alerts.limit} alerts; narrow the filters to see the rest.` : undefined}>
                {done.length === 0
                  ? <EmptyState title="Nothing was resolved in this window" />
                  : <DataTable caption="Resolved alerts" columns={alertColumns(selected, select, true)} rows={done} rowKey={a => a.id} />}
              </Panel>
            </div>
          )}
        </div>
        <div className="sy-detail" data-testid="alert-detail">   {/* a div, not an aside: a complementary landmark may not sit inside main (axe landmark-complementary-is-top-level) */}
          {sel
            ? <Detail a={sel} canAdmin={canAdmin} busy={busy} confirming={confirming} setConfirming={setConfirming} onResolve={() => void resolve()} onClose={close} onCopyError={setActionError} closeRef={closeRef} />
            : <Panel title="Alert detail"><EmptyState title="Select an alert">Select an alert to see its cause, what it affects and how to clear it. In the alert tables, j / k move, Enter opens the detail, Esc closes it.</EmptyState></Panel>}
          {actionError && <p className="sy-problem" role="alert">▲ {actionError}</p>}
        </div>
      </div>

      <WebhookAllowlist rows={hooks} canAdmin={canAdmin} onChanged={() => void load()} />
    </div>
  )
}

function WebhookAllowlist({ rows, canAdmin, onChanged }: { rows: WebhookRow[] | null; canAdmin: boolean; onChanged: () => void }) {
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [kind, setKind] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<WebhookRow | null>(null)
  const nameRef = useRef<HTMLInputElement>(null)
  const problem = urlProblem(url)
  const ready = name.trim() !== '' && url.trim() !== '' && !problem

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!ready) return
    setBusy(true); setError(null)
    try {
      await post('/api/v1/sync/webhook-allowlist', { name: name.trim(), url: url.trim(), kind: kind || null })
      setName(''); setUrl(''); setKind('')
      onChanged()
    } catch (err) { setError(errText(err)) } finally { setBusy(false) }
  }
  const remove = async (w: WebhookRow) => {
    setBusy(true); setError(null)
    try { await delIfMatch(`/api/v1/sync/webhook-allowlist/${w.id}`, w.version); announce(`${w.name} removed from the allowlist.`); setRemoving(null); onChanged(); requestAnimationFrame(() => nameRef.current?.focus()) }
    catch (err) { setError(errText(err)); setRemoving(null); onChanged() } finally { setBusy(false) }
  }

  const columns: Column<WebhookRow>[] = [
    { key: 'name', header: 'Name', cell: w => w.name },
    { key: 'kind', header: 'Kind', cell: w => w.kind },
    { key: 'address', header: 'Address', cell: w => <TwoLine primary={<span className="mono">{w.displayUrl}</span>} secondary="Path hidden: it is a secret" /> },
    { key: 'used', header: 'Used by', cell: w => w.usedByTeams + w.usedByDispatches === 0 ? <span className="os-muted">Not used</span>
      : `${w.usedByTeams} team${w.usedByTeams === 1 ? '' : 's'} · ${w.usedByDispatches} dispatch${w.usedByDispatches === 1 ? '' : 'es'}` },
    ...(canAdmin ? [{ key: 'actions', header: <span className="sr-only">Actions</span>, cell: (w: WebhookRow) => <Button variant="destructive" disabled={busy} aria-label={`Remove ${w.name}`} onClick={() => setRemoving(w)}>Remove</Button> }] : []),
  ]

  return (
    <>
    <Panel label="Webhook allowlist" title="Webhook allowlist" subtitle="Channels the app may post to (HTTPS only)" meta={rows ? `${rows.length} channel${rows.length === 1 ? '' : 's'}` : undefined}>
      <div className="sy-body">
        {rows && rows.length === 0 && <EmptyState title="No channels are allowlisted">No message can be posted to a webhook until one is added.</EmptyState>}
        {rows && rows.length > 0 && <DataTable caption="Webhook allowlist" columns={columns} rows={rows} rowKey={w => w.id} />}
        {canAdmin ? (
          <form className="sy-add" onSubmit={add} aria-label="Add a webhook">
            <Field label="Name"><TextInput ref={nameRef} type="text" value={name} onChange={e => setName(e.target.value)} placeholder="#release-ops" maxLength={80} /></Field>
            <Field label="Address"><TextInput type="text" inputMode="url" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://…" autoComplete="off" spellCheck={false} aria-invalid={problem ? true : undefined} aria-describedby="wh-hint" /></Field>
            <Field label="Kind"><SelectInput value={kind} onChange={e => setKind(e.target.value)} options={[{ value: '', label: 'Detect from address' }, { value: 'Teams', label: 'Teams' }, { value: 'Slack', label: 'Slack' }, { value: 'Generic', label: 'Generic' }]} /></Field>
            <Button type="submit" variant="primary" disabled={!ready || busy}>{busy ? 'Adding…' : 'Add'}</Button>
          </form>
        ) : <ReadOnlyNotice>Only RTEs and Release Managers change the allowlist.</ReadOnlyNotice>}
        {canAdmin && (
          <p id="wh-hint" role="status" className={problem ? 'sy-problem' : 'sy-note'}>
            {problem ? `▲ ${problem}` : !ready ? 'Enter a name and an https:// address to add a channel. Private, loopback and internal addresses are refused unless the deployment allows them.' : ''}
          </p>
        )}
        {error && <p className="sy-problem" role="alert">▲ {error}</p>}
      </div>
    </Panel>
      <ConfirmDialog open={removing !== null} title={removing ? `Remove ${removing.name}?` : 'Remove channel?'} confirmLabel="Remove channel" pendingLabel="Removing…" destructive pending={busy}
        onConfirm={() => { if (removing) void remove(removing) }} onCancel={() => setRemoving(null)}>
        Nothing can be posted to this channel once it is removed. Teams and dispatches that use it will fail delivery until another channel is chosen.
      </ConfirmDialog>
    </>
  )
}
