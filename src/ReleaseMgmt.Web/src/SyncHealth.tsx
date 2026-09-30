import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, delIfMatch, get, getStream, post, type Me, type StreamRow } from './api'
import { notifySyncChanged, onSyncChanged, SYSTEM_NAMES, type ConnectorView, type FailingEntry, type SyncStateDto } from './SyncBanner'
import { fmtDay, fmtHM, fmtHMS, serverNow, zoneAbbr } from './time'

/**
 * Screen 5, Sync health (REOS-42, mockups/SyncHealth.html): connector table, mismatches, open alerts with counts, resolved history, the alert detail
 * in the right-hand column (same split layout as Admin and Audit), and the webhook allowlist. It refetches when a SyncAlertRaised push arrives (App
 * forwards it through notifySyncChanged), so a new alert appears without a reload. Keyboard: j / k move the selection, Enter moves focus into the detail,
 * Esc closes it. Status is words and glyphs; actions are text buttons; the destructive confirms are inline. See Q-042a..f.
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

/** Today in the display zone: time only; another day: weekday and time. */
function short(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso), now = new Date(serverNow())
  return fmtDay(d) === fmtDay(now) ? fmtHM(d) : `${fmtDay(d)} ${fmtHM(d)}`
}
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

function ConnectorRow({ c, failing }: { c: ConnectorView; failing: FailingEntry | undefined }) {
  const l = c.links
  const dash = <span className="muted">—</span>
  const n = (v: number, cls?: string) => <td className={`n${v > 0 && cls ? ` ${cls}` : ''}`}>{l.total === 0 ? dash : v}</td>
  const lastOk = c.lastSuccessAt
  const stale = minutesSince(lastOk) > STALE_MIN && c.isEnabled
  return (
    <tr>
      <td><strong>{SYSTEM_NAMES[c.source] ?? c.source}</strong> <span className="muted">· {hostOf(c.baseUrl)}</span></td>
      <td className={`mono${stale ? ' bad' : ''}`}>{lastOk ? <>{fmtHMS(lastOk)}{minutesSince(lastOk) > 5 && <span className={stale ? '' : 'muted'}> ({ago(lastOk)})</span>}</> : <span className="muted">never</span>}</td>
      <td className="n">{l.total}</td>
      {n(l.inSync)}{n(l.mismatch, 'warn')}{n(l.broken, 'bad')}{n(l.stale, 'bad')}
      <td className="n">{c.openAlerts}</td>
      <td>
        {c.state === 'Healthy' && <span className="ok">● Healthy</span>}
        {c.state === 'Failing' && <strong className="bad">✗ {failing?.kind === 'AuthFailed' ? 'Auth failed' : failing?.kind === 'Unreachable' ? 'Unreachable' : 'Failing'}{failing?.since ? ` since ${short(failing.since)}` : ''}</strong>}
        {c.state === 'Degraded' && <span className="warn">▲ Degraded</span>}
        {c.state === 'NotRun' && <span className="muted">○ Not run yet</span>}
        {c.state === 'Disabled' && <span className="muted">○ Disabled</span>}
      </td>
    </tr>
  )
}

function navigate(path: string) { window.history.pushState(null, '', path); window.dispatchEvent(new PopStateEvent('popstate')) }

function Detail({ a, canAdmin, busy, confirming, setConfirming, onResolve, onClose, closeRef }: {
  a: AlertItem; canAdmin: boolean; busy: boolean; confirming: boolean; setConfirming: (v: boolean) => void; onResolve: () => void; onClose: () => void; closeRef: React.RefObject<HTMLButtonElement | null>
}) {
  const info = kindInfo(a.kind)
  const connector = a.source === 'Jira' || a.source === 'ServiceNow'
  return (
    <>
      <h2>{SYSTEM_NAMES[a.source] ?? a.source} {a.kind}</h2>
      <p className="muted">Fingerprint <span className="mono" title={a.fingerprint}>{a.fingerprint.slice(0, 12)}…</span> · {a.occurrenceCount} occurrence{a.occurrenceCount === 1 ? '' : 's'}</p>
      <dl className="facts">
        <dt>State</dt><dd>{a.isResolved ? <span className="ok">✓ Resolved {short(a.resolvedAt)} · {a.resolvedByName ?? 'cleared automatically after a clean cycle'}</span> : <span className={info.cls}>{info.glyph} Open</span>}</dd>
        <dt>First</dt><dd className="mono">{fmtDay(a.firstOccurredAt)} {fmtHMS(a.firstOccurredAt)} {zoneAbbr(a.firstOccurredAt)}</dd>
        <dt>Last</dt><dd className="mono">{fmtDay(a.lastOccurredAt)} {fmtHMS(a.lastOccurredAt)} {zoneAbbr(a.lastOccurredAt)}</dd>
        <dt>Scope</dt><dd>{a.trainTitle ? <span className="mono">{a.trainTitle}</span> : 'all trains'}</dd>
        <dt>Message</dt><dd className="mono sync-note">{a.message}</dd>
        <dt>Likely cause</dt><dd>{info.cause}</dd>
      </dl>
      <h3 className="cap">What is affected</h3>
      <p>{info.impact}</p>
      <h3 className="cap">To clear it</h3>
      <ol className="sync-steps">{info.steps.map(s => <li key={s}>{s}</li>)}</ol>
      <h3 className="cap">Who was told</h3>
      <p className="sync-note">The banner on every open screen while a whole connector is failing; an in-app notice to RTEs and Release Managers when the alert was first raised; the team webhook after 3 consecutive failures (PROJECT_SCOPE 5.3).</p>
      <div className="actions-col">
        {connector && canAdmin && <button type="button" className="text" onClick={() => navigate('/connectors')}>Open Connectors</button>}
        {!a.isResolved && canAdmin && !confirming && <button type="button" className="text" onClick={() => setConfirming(true)}>Resolve now</button>}
        <button type="button" className="text" ref={closeRef} onClick={onClose}>Close (Esc)</button>
      </div>
      {!a.isResolved && canAdmin && confirming && (
        <p role="group" aria-label="Confirm resolve">
          Mark this alert resolved? The row stays in the history. If the fault is still there, the next failed cycle raises it again.{' '}
          <button type="button" className="text" disabled={busy} onClick={onResolve}>{busy ? 'Resolving…' : 'Confirm'}</button>{' '}
          <button type="button" className="text" onClick={() => setConfirming(false)}>Cancel</button>
        </p>
      )}
      {!a.isResolved && !canAdmin && <p className="muted sync-note">Only RTEs and Release Managers resolve alerts by hand.</p>}
    </>
  )
}

function AlertTable({ label, rows, selected, onSelect, resolved }: { label: string; rows: AlertItem[]; selected: string | null; onSelect: (id: string) => void; resolved: boolean }) {
  return (
    <div className="scroll-x">
      <table className="grid audit-table sync-alerts" aria-label={label}>
        <thead><tr><th>Kind</th><th>System</th><th>Message</th><th>First</th><th>Last</th><th className="n">Count</th><th>Scope</th><th>{resolved ? 'Resolved' : <span className="sr-only">Actions</span>}</th></tr></thead>
        <tbody>
          {rows.map(a => {
            const info = kindInfo(a.kind)
            return (
              <tr key={a.id} id={`alert-${a.id}`} className={a.id === selected ? 'selected' : undefined} aria-selected={a.id === selected} onClick={() => onSelect(a.id)}>
                <td className={resolved ? 'ok' : info.cls} style={{ fontWeight: 600 }}>{resolved ? '✓' : info.glyph} {a.kind}</td>
                <td>{SYSTEM_NAMES[a.source] ?? a.source}</td>
                <td className="mono wrap" style={{ fontSize: 12 }}>{a.message}</td>
                <td className="mono muted">{short(a.firstOccurredAt)}</td>
                <td className="mono muted">{short(a.lastOccurredAt)}</td>
                <td className="n">{a.occurrenceCount}</td>
                <td>{a.trainTitle ? <span className="mono">{a.trainTitle}</span> : 'all trains'}</td>
                <td>{resolved
                  ? <span className="mono muted">{short(a.resolvedAt)} <span className="muted">{a.resolvedByName ? `· ${a.resolvedByName}` : '· auto'}</span></span>
                  : <button type="button" className="text" onClick={e => { e.stopPropagation(); onSelect(a.id) }} aria-label={`Details of ${a.source} ${a.kind}`}>Details</button>}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
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
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const seq = useRef(0)
  const closeRef = useRef<HTMLButtonElement | null>(null)

  useEffect(() => { getStream().then(setTrains).catch(() => setTrains([])) }, [])   // only feeds the Train filter; a failure leaves it empty and the rest of the screen says so if the API is down

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
  const move = useCallback((delta: number) => {
    if (ordered.length === 0) return
    const i = ordered.findIndex(a => a.id === selected)
    const j = i < 0 ? (delta > 0 ? 0 : ordered.length - 1) : Math.min(ordered.length - 1, Math.max(0, i + delta))
    setSelected(ordered[j].id)
    document.getElementById(`alert-${ordered[j].id}`)?.scrollIntoView?.({ block: 'nearest' })
  }, [ordered, selected])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null
      if (t && (['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName) || t.isContentEditable)) return
      if (e.ctrlKey || e.metaKey || e.altKey) return
      if (e.key === 'j') { e.preventDefault(); move(1) }
      else if (e.key === 'k') { e.preventDefault(); move(-1) }
      else if (e.key === 'Escape' && selected !== null) setSelected(null)
      else if (e.key === 'Enter' && sel && t?.tagName !== 'BUTTON' && t?.tagName !== 'A') { e.preventDefault(); closeRef.current?.focus() }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [move, selected, sel])

  const resolve = async () => {
    if (!sel) return
    setBusy(true); setActionError(null)
    try {
      await post(`/api/v1/sync/alerts/${sel.id}:resolve`, undefined, sel.version)
      setConfirming(false)
      notifySyncChanged()   // this screen, the banner, and every other subscriber refetch
    } catch (e) {
      setActionError(e instanceof ApiError && e.status === 409 ? 'This alert changed since you loaded it. The list has been refreshed; check it and try again.' : errText(e))
      void load()
    } finally { setBusy(false) }
  }

  const failingFor = (source: string) => state?.failing.find(f => f.source === source && f.scope === 'connector')
  const last = state?.watchdog.lastCycleCompletedAt ?? null

  return (
    <>
      <div className="sync-head">
        <div>
          <h1>Sync health</h1>
          <div className="muted">Read-only · every 5 min, every 1 min during a window · times in {zoneAbbr()}</div>
        </div>
        {state && (
          <div className="sync-side">
            <div>{last ? <>Last cycle <span className="mono">{fmtHMS(last)}</span> · {ago(last)} ago</> : 'No sync cycle has completed yet'}</div>
            <div className={state.watchdog.stalled ? 'bad' : last ? 'ok' : 'muted'}>
              Watchdog {state.watchdog.stalled ? '✗ sync stalled' : last ? '● heartbeat ok' : '○ waiting for the first cycle'} <span className="muted">· raises “sync stalled” after 15 min without a cycle</span>
            </div>
          </div>
        )}
      </div>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      {!state && !error && <p className="muted" role="status">Loading…</p>}

      {state && (
        <section aria-label="Connectors">
          <h2 className="cap dark">Connectors</h2>
          {state.connectors.length === 0
            ? <p className="muted">○ No connectors are configured yet.{canAdmin ? ' Add Jira and ServiceNow under Connectors.' : ' An RTE or Release Manager adds them under Connectors.'}</p>
            : <div className="scroll-x"><table className="grid" aria-label="Connectors">
              <thead><tr><th>Connector</th><th>Last success</th><th className="n">Links</th><th className="n">In sync</th><th className="n">Mismatch</th><th className="n">Broken</th><th className="n">Stale</th><th className="n">Open alerts</th><th>State</th></tr></thead>
              <tbody>{state.connectors.map(c => <ConnectorRow key={c.source} c={c} failing={failingFor(c.source)} />)}</tbody>
            </table></div>}
        </section>
      )}

      {links && (
        <section aria-label="Mismatches">
          <div className="section-head"><h2 className="cap dark">Mismatches · this app and the ticket disagree</h2><span className="muted sync-note">{links.length} open</span></div>
          {links.length === 0 ? <p className="ok">✓ Every synced link agrees with its ticket.</p>
            : <div className="scroll-x"><table className="grid" aria-label="Mismatched links">
              <thead><tr><th>Train</th><th>Node</th><th>Ticket</th><th>This app implies</th><th>Ticket says</th><th>Last synced</th></tr></thead>
              <tbody>{links.map(l => (
                <tr key={l.id}>
                  <td className="mono">{l.trainTitle ?? l.trainId}</td><td>{l.entityType}</td><td className="mono">{l.externalKey}</td>
                  <td>{l.expected ?? <span className="muted">—</span>}</td>
                  <td className={l.syncState === 'Mismatch' ? 'warn' : 'bad'}>{l.syncState === 'Mismatch' ? `◆ ${l.reported ?? 'differs'}` : l.syncState === 'NotFound' ? '✗ not found' : '✗ auth failed'}</td>
                  <td className="mono muted">{short(l.lastSyncedAt)}</td>
                </tr>))}</tbody>
            </table></div>}
        </section>
      )}

      <form className="inline-form audit-filters" aria-label="Alert filters" onSubmit={e => e.preventDefault()}>
        <label>System <select className="line" value={filters.source} onChange={e => setFilters(f => ({ ...f, source: e.target.value }))}>
          <option value="">All systems</option>{SOURCES.map(s => <option key={s} value={s}>{SYSTEM_NAMES[s] ?? s}</option>)}</select></label>
        <label>Kind <select className="line" value={filters.kind} onChange={e => setFilters(f => ({ ...f, kind: e.target.value }))}>
          <option value="">All kinds</option>{KINDS.map(k => <option key={k}>{k}</option>)}</select></label>
        <label>Train <select className="line" value={filters.train} onChange={e => setFilters(f => ({ ...f, train: e.target.value }))}>
          <option value="">All trains</option>{trains.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label>
        {(filters.source || filters.kind || filters.train) && <button type="button" className="text" onClick={() => setFilters({ source: '', kind: '', train: '' })}>Clear</button>}
      </form>

      <div className="split">
        <div>
          {alerts && (
            <>
              <section aria-label="Open alerts">
                <div className="section-head"><h2 className="cap dark">Open alerts · errors, one row per fingerprint, repeats counted</h2><span className="muted sync-note">{alerts.openCount} open</span></div>
                {open.length === 0 ? <p className="ok" role="status">✓ No open alerts{filters.source || filters.kind || filters.train ? ' match these filters' : ''}.</p>
                  : <AlertTable label="Open alerts" rows={open} selected={selected} onSelect={select} resolved={false} />}
              </section>
              <section aria-label="Resolved alerts">
                <div className="section-head">
                  <h2 className="cap dark">Resolved history · cleared automatically after one good cycle, or by hand</h2>
                  <span className="muted sync-note">{alerts.resolvedCount} in the last {alerts.resolvedWithinDays === 1 ? '24 h' : `${alerts.resolvedWithinDays} days`}
                    <span className="sync-window" role="group" aria-label="History window">{WINDOWS.map(w => <button key={w.days} type="button" aria-pressed={days === w.days} onClick={() => setDays(w.days)}>{w.label}</button>)}</span></span>
                </div>
                {done.length === 0 ? <p className="muted">○ Nothing was resolved in this window.</p>
                  : <AlertTable label="Resolved alerts" rows={done} selected={selected} onSelect={select} resolved />}
                {alerts.items.length >= alerts.limit && <p className="warn sync-note">▲ Showing the newest {alerts.limit} alerts; narrow the filters to see the rest.</p>}
              </section>
            </>
          )}

          <section aria-label="Webhook allowlist">
            <WebhookAllowlist rows={hooks} canAdmin={canAdmin} onChanged={() => void load()} />
          </section>
        </div>
        <aside className="detail" aria-label="Alert detail">
          {sel
            ? <Detail a={sel} canAdmin={canAdmin} busy={busy} confirming={confirming} setConfirming={setConfirming} onResolve={resolve} onClose={() => setSelected(null)} closeRef={closeRef} />
            : <p className="muted">Select an alert to see its cause, what it affects and how to clear it. j / k move, Enter opens the detail, Esc closes it.</p>}
          {actionError && <p className="bad" role="alert">✗ {actionError}</p>}
        </aside>
      </div>
    </>
  )
}

function WebhookAllowlist({ rows, canAdmin, onChanged }: { rows: WebhookRow[] | null; canAdmin: boolean; onChanged: () => void }) {
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [kind, setKind] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
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
    try { await delIfMatch(`/api/v1/sync/webhook-allowlist/${w.id}`, w.version); setRemoving(null); onChanged() }
    catch (err) { setError(errText(err)); onChanged() } finally { setBusy(false) }
  }

  return (
    <>
      <div className="section-head"><h2 className="cap dark">Webhook allowlist · channels the app may post to (HTTPS only)</h2><span className="muted sync-note">{rows ? `${rows.length} channel${rows.length === 1 ? '' : 's'}` : ''}</span></div>
      {rows && rows.length === 0 && <p className="muted">○ No channels are allowlisted, so no message can be posted to a webhook.</p>}
      {rows && rows.length > 0 && (
        <div className="scroll-x"><table className="grid" aria-label="Webhook allowlist">
          <thead><tr><th>Name</th><th>Kind</th><th>Address</th><th>Used by</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{rows.map(w => (
            <tr key={w.id}>
              <td>{w.name}</td><td>{w.kind}</td>
              <td><span className="mono">{w.displayUrl}</span> <span className="muted sync-note">path hidden: it is a secret</span></td>
              <td>{w.usedByTeams + w.usedByDispatches === 0 ? <span className="muted">not used</span> : `${w.usedByTeams} team${w.usedByTeams === 1 ? '' : 's'} · ${w.usedByDispatches} dispatch${w.usedByDispatches === 1 ? '' : 'es'}`}</td>
              <td>{canAdmin && (removing === w.id
                ? <span>Remove {w.name}? <button type="button" className="text destructive" disabled={busy} onClick={() => void remove(w)}>Confirm</button>{' '}<button type="button" className="text" onClick={() => setRemoving(null)}>Cancel</button></span>
                : <button type="button" className="text" onClick={() => { setRemoving(w.id); setError(null) }} aria-label={`Remove ${w.name}`}>Remove</button>)}</td>
            </tr>))}</tbody>
        </table></div>
      )}
      {canAdmin ? (
        <form className="inline-form sync-add" onSubmit={add} aria-label="Add a webhook">
          <label>Name <input className="line" type="text" value={name} onChange={e => setName(e.target.value)} placeholder="#release-ops" maxLength={80} /></label>
          <label>Address <input className="line url" type="text" inputMode="url" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://…" autoComplete="off" spellCheck={false} aria-invalid={problem ? true : undefined} aria-describedby="wh-hint" /></label>
          <label>Kind <select className="line" value={kind} onChange={e => setKind(e.target.value)}><option value="">Detect from address</option><option>Teams</option><option>Slack</option><option>Generic</option></select></label>
          <button type="submit" className="text" disabled={!ready || busy}>{busy ? 'Adding…' : 'Add'}</button>
        </form>
      ) : <p className="muted sync-note">Only RTEs and Release Managers change the allowlist.</p>}
      {canAdmin && (
        <p id="wh-hint" role="status" className={`sync-note ${problem ? 'bad' : 'muted'}`}>
          {problem ? `✗ ${problem}` : !ready ? 'Enter a name and an https:// address to add a channel. Private, loopback and internal addresses are refused unless the deployment allows them.' : ''}
        </p>
      )}
      {error && <p className="bad" role="alert">✗ {error}</p>}
    </>
  )
}
