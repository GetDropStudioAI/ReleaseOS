import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { Copy, Download, ShieldAlert, ShieldCheck, X } from 'lucide-react'
import { ApiError, get, getStream, type Me, type StreamRow, type UserRow } from './api'
import { announce } from './announce'
import { Button, ConfirmDialog, EmptyState, ErrorState, FilterSelect, LoadingState, NoPermissionState, Notice, PageHeading, Panel, PropertyList, SearchField, StatusChip, TwoLine } from './ui'
import { localClock, browserZone } from './shell/clock'
import {
  EMPTY_FILTERS, LARGE_EXPORT, PAGE_SIZE, SERVER_EXPORT_MAX, anyFilter, auditPath, auditQuery, diffLines, eventFromSearch, eventId, eventRecord, eventTitle, outcomeOf,
  parseJson, type AuditItem, type Filters,
} from './auditLogic'
import './audit.css'

/**
 * Audit history (REOS-101, Figma 6:6333; the table and filters are REOS-38's): integrity banner, filter bar, event table and a closable details panel.
 * Filters apply as they change and combine; the table pages by cursor (Previous / Next) with the matching total; the open event is in the URL
 * (`/audit?event=123`) so it can be shared; in the table Up and Down move the selection, Enter opens the details, Esc closes them. Read-only: nothing here writes.
 */
interface AuditPageDto { items: AuditItem[]; nextCursor: string | null; limit: number; total: number | null }
interface Integrity { appendOnly: boolean; missingTriggers: string[]; hashChained: boolean; retainedEvents: number; oldestAt: string | null; newestAt: string | null; checkedAt: string }

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const pad = (n: number) => String(n).padStart(2, '0')
const utcDay = (iso: string) => { const d = new Date(iso); return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}` }
const utcTime = (iso: string) => { const d = new Date(iso); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}` }
const local = (iso: string) => localClock(Date.parse(iso), browserZone())

function Stamp({ iso }: { iso: string }) {
  const l = local(iso)
  return <>{utcDay(iso)}, {utcTime(iso)} UTC{l && <span className="os-muted"> ({l})</span>}</>
}

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value)
  useEffect(() => { const t = setTimeout(() => setV(value), ms); return () => clearTimeout(t) }, [value, ms])
  return v
}

/** The banner states only what the app can check. There is no hash chain yet (REOS-114), so it never says "chain verified": it reports the append-only triggers it found. */
function IntegrityBanner({ integrity, error, onRetry }: { integrity: Integrity | null; error: string | null; onRetry: () => void }) {
  if (error) return <ErrorState what="The audit integrity check" message={error} onRetry={onRetry} />
  if (!integrity) return <LoadingState what="the integrity check" />
  const checked = <Stamp iso={integrity.checkedAt} />
  return (
    <section className={`au-integrity ${integrity.appendOnly ? 'intact' : 'broken'}`} aria-label="Audit integrity">
      <div className="au-integrity-main">
        {integrity.appendOnly ? <ShieldCheck aria-hidden="true" size={22} strokeWidth={1.6} /> : <ShieldAlert aria-hidden="true" size={22} strokeWidth={1.6} />}
        <div>
          <p className="au-integrity-title">{integrity.appendOnly ? 'Append-only protection verified' : 'Audit log protection could not be verified'}</p>
          <p className="au-integrity-sub">
            {integrity.appendOnly
              ? 'Edits and deletes of events are refused by the database. Not hash-chained or signed yet (REOS-114).'
              : `Missing database rule${integrity.missingTriggers.length === 1 ? '' : 's'}: ${integrity.missingTriggers.join(', ')}. Events could be altered or deleted. Tell an administrator.`}
          </p>
        </div>
      </div>
      <dl className="au-integrity-facts">
        <div><dt>Retained events</dt><dd className="mono">{integrity.retainedEvents.toLocaleString('en-US')}</dd></div>
        <div><dt>Retention</dt><dd>Kept indefinitely</dd></div>
        <div><dt>Archive</dt><dd>None configured</dd></div>
        <div><dt>Last checked</dt><dd className="mono">{checked}</dd></div>
      </dl>
    </section>
  )
}

function DiffBlock({ item }: { item: AuditItem }) {
  const lines = useMemo(() => diffLines(parseJson(item.beforeJson), parseJson(item.afterJson)), [item])
  if (lines.length === 0) return <p className="os-muted">No before or after values were recorded for this event.</p>
  const word = { removed: 'Removed', added: 'Added', same: 'Unchanged' } as const
  const sign = { removed: '−', added: '+', same: ' ' } as const
  return (
    <div className="au-diff" role="group" aria-label="Change">
      <p className="au-diff-legend">− removed · + added · unchanged lines have no mark</p>
      <pre>{lines.map((l, i) => <div key={i} className={`au-line ${l.kind}`}><span className="sr-only">{word[l.kind]}: </span><span aria-hidden="true" className="au-sign">{sign[l.kind]}</span>{l.text}</div>)}</pre>
    </div>
  )
}

function Details({ item, role, onClose, onOpenTrain, closeRef }: { item: AuditItem; role: string | undefined; onClose: () => void; onOpenTrain?: (id: string) => void; closeRef: React.RefObject<HTMLButtonElement | null> }) {
  const [copied, setCopied] = useState<'ok' | 'fail' | null>(null)
  useEffect(() => setCopied(null), [item.id])
  const copy = async () => {
    const text = JSON.stringify(eventRecord(item), null, 2)
    try {
      await navigator.clipboard.writeText(text)
      setCopied('ok'); announce(`Event record ${eventId(item.id)} copied`)
    } catch { setCopied('fail'); announce('The event record could not be copied') }
  }
  const outcome = outcomeOf(item.action)
  return (
    <Panel title="Event details" meta={<button type="button" ref={closeRef} className="au-icon-button" aria-label="Close event details (Esc)" onClick={onClose}><X aria-hidden="true" size={16} strokeWidth={1.6} /></button>}>
      <div className="au-details">
        <StatusChip>{outcome}</StatusChip>
        <h3 className="au-details-title">{eventTitle(item.entityType, item.action)}</h3>
        <p className="os-muted au-details-id"><span className="mono">{eventId(item.id)}</span> · <Stamp iso={item.occurredAt} /></p>
        <PropertyList items={[
          { label: 'Release', value: item.trainTitle && item.releaseTrainId ? (onOpenTrain ? <button type="button" className="os-link os-link-button" onClick={() => onOpenTrain(item.releaseTrainId!)}>{item.trainTitle}</button> : item.trainTitle) : 'None' },
          { label: 'Resource', value: <>{item.entityType} <span className="mono">{item.entityId}</span></> },
          { label: 'Actor', value: item.actorName ?? (item.actorUserId ? 'Unknown user' : 'System') },
          ...(role ? [{ label: 'Role', value: role }] : []),
        ]} />
        <h4 className="au-h4">Change</h4>
        <DiffBlock item={item} />
        <p className="au-signature"><ShieldAlert aria-hidden="true" size={16} strokeWidth={1.6} /> <span>Not signed. The log is append-only; signatures and a hash chain arrive with REOS-114.</span></p>
        <div className="au-copy">
          <Button icon={Copy} onClick={copy}>Copy event record</Button>
          {copied === 'ok' && <span className="au-copied" role="status">✓ Copied as JSON</span>}
          {copied === 'fail' && <span className="au-copy-fail" role="alert">✗ The browser refused clipboard access. Select the change above and copy it by hand.</span>}
        </div>
      </div>
    </Panel>
  )
}

export function AuditViewer({ me, onOpenTrain }: { me: Me; onOpenTrain?: (trainId: string) => void }) {
  const allowed = me.roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')
  const [filters, setFilters] = useState<Filters>(EMPTY_FILTERS)
  const search = useDebounced(filters.search, 300)
  const applied = useMemo<Filters>(() => ({ ...filters, search }), [filters, search])
  const [rows, setRows] = useState<AuditItem[] | null>(null)
  const [total, setTotal] = useState<number | null>(null)
  const [next, setNext] = useState<string | null>(null)
  const [cursors, setCursors] = useState<(string | null)[]>([null])   // the cursor that starts each page shown so far; Previous pops one
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [active, setActive] = useState<number | null>(null)            // the highlighted row (arrow keys)
  const [openId, setOpenId] = useState<number | null>(() => eventFromSearch(window.location.search))
  const [openItem, setOpenItem] = useState<AuditItem | null>(null)
  const [openError, setOpenError] = useState<string | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [users, setUsers] = useState<UserRow[]>([])
  const [types, setTypes] = useState<string[]>([])
  const [integrity, setIntegrity] = useState<Integrity | null>(null)
  const [integrityError, setIntegrityError] = useState<string | null>(null)
  const [retention, setRetention] = useState(false)
  const [confirmExport, setConfirmExport] = useState(false)
  const [focusPanel, setFocusPanel] = useState(false)             // Enter opened the details: focus moves into them once they are drawn
  const seq = useRef(0)
  const closeRef = useRef<HTMLButtonElement | null>(null)
  const tableRef = useRef<HTMLTableElement | null>(null)
  const pageIndex = cursors.length - 1

  useEffect(() => {
    if (!allowed) return
    getStream().then(setTrains).catch(() => setTrains([]))
    get<UserRow[]>('/api/v1/users').then(setUsers).catch(() => setUsers([]))
    get<string[]>('/api/v1/audit/entity-types').then(setTypes).catch(() => setTypes([]))
  }, [allowed])
  const loadIntegrity = useCallback(() => { setIntegrityError(null); get<Integrity>('/api/v1/audit/integrity').then(setIntegrity).catch((e: Error) => setIntegrityError(e.message)) }, [])
  useEffect(() => { if (allowed) loadIntegrity() }, [allowed, loadIntegrity])

  const load = useCallback(async (f: Filters, cursor: string | null, withTotal: boolean) => {
    const mine = ++seq.current
    setLoading(true); setError(null)
    try {
      const q = auditQuery(f); q.set('limit', String(PAGE_SIZE)); if (cursor) q.set('cursor', cursor); if (withTotal) q.set('total', 'true')
      const page = await get<AuditPageDto>(`/api/v1/audit?${q}`)
      if (mine !== seq.current) return   // a newer filter change superseded this request
      setRows(page.items); setNext(page.nextCursor); if (page.total !== null) setTotal(page.total)
      setActive(a => (page.items.some(r => r.id === a) ? a : page.items[0]?.id ?? null))
    } catch (e) {
      if (mine !== seq.current) return
      setError(e instanceof ApiError && e.status === 400 ? (e.body?.message ?? 'Those filters are not valid') : (e as Error).message)
    } finally { if (mine === seq.current) setLoading(false) }
  }, [])

  // a filter change starts again at page one and asks for the new total
  useEffect(() => { if (allowed) { setCursors([null]); load(applied, null, true) } }, [allowed, applied, load])
  const goNext = () => { if (next) { setCursors(c => [...c, next]); load(applied, next, false) } }
  const goPrev = () => { if (pageIndex > 0) { const c = cursors.slice(0, -1); setCursors(c); load(applied, c[c.length - 1], false) } }

  // the open event: from the page when it is there, otherwise by id (a shared link)
  useEffect(() => {
    setOpenError(null)
    if (openId === null) { setOpenItem(null); return }
    const onPage = rows?.find(r => r.id === openId)
    if (onPage) { setOpenItem(onPage); return }
    let cancelled = false
    get<AuditItem>(`/api/v1/audit/${openId}`).then(i => { if (!cancelled) setOpenItem(i) })
      .catch(e => { if (!cancelled) { setOpenItem(null); setOpenError(e instanceof ApiError && e.status === 404 ? `Event ${eventId(openId)} does not exist in this workspace.` : (e as Error).message) } })
    return () => { cancelled = true }
  }, [openId, rows])
  useEffect(() => { if (focusPanel && openItem && closeRef.current) { closeRef.current.focus(); setFocusPanel(false) } }, [focusPanel, openItem])
  const setOpen = useCallback((id: number | null) => {
    setOpenId(id)
    window.history.replaceState(null, '', auditPath(id))
  }, [])
  useEffect(() => {
    const onPop = () => setOpenId(eventFromSearch(window.location.search))
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const focusRow = (id: number | null) => requestAnimationFrame(() => tableRef.current?.querySelector<HTMLElement>(`[data-event="${id}"]`)?.focus())
  const onRowKey = (e: KeyboardEvent<HTMLTableRowElement>, i: number) => {
    if (!rows) return
    const move = (to: number) => { e.preventDefault(); const r = rows[Math.min(rows.length - 1, Math.max(0, to))]; if (r) { setActive(r.id); focusRow(r.id) } }
    if (e.key === 'ArrowDown') move(i + 1)
    else if (e.key === 'ArrowUp') move(i - 1)
    else if (e.key === 'Home') move(0)
    else if (e.key === 'End') move(rows.length - 1)
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setActive(rows[i].id); setOpen(rows[i].id); setFocusPanel(true) }
  }
  const close = () => { const id = openId; setOpen(null); focusRow(id) }
  const onEsc = (e: KeyboardEvent) => {
    const t = e.target as HTMLElement
    if (e.key === 'Escape' && openId !== null && !['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName) && !(t.closest('dialog'))) { e.preventDefault(); close() }
  }

  // the export takes the filter as typed, not the debounced one the table is showing, so it never lags a keystroke behind the screen
  const csvQuery = auditQuery(filters).toString()
  const csvHref = `/api/v1/audit.csv${csvQuery ? `?${csvQuery}` : ''}`
  const download = () => { const a = document.createElement('a'); a.href = csvHref; a.download = ''; document.body.appendChild(a); a.click(); a.remove(); setConfirmExport(false) }
  const exportLog = () => { if ((total ?? 0) > LARGE_EXPORT) setConfirmExport(true); else download() }
  const roleOf = (id: string | null) => users.find(u => u.id === id)?.role

  if (!allowed) return <div className="os-page"><PageHeading trail={['Workspace', 'Audit history']} title="Audit history" /><NoPermissionState what="read the audit log" who="Release Train Engineers, Release Managers and Governance Officers" /></div>

  const filtered = anyFilter(applied)
  const from = rows && rows.length > 0 ? pageIndex * PAGE_SIZE + 1 : 0
  const to = rows ? pageIndex * PAGE_SIZE + rows.length : 0
  const count = total === null ? '' : `${total.toLocaleString('en-US')} event${total === 1 ? '' : 's'}${filtered ? ' match the filters' : ''}`
  const set = (k: keyof Filters) => (v: string) => setFilters(f => ({ ...f, [k]: v }))
  const date = (k: 'from' | 'to', label: string) => (
    <label className="au-date"><span>{label}</span><input type="date" value={filters[k]} onChange={e => set(k)(e.target.value)} /></label>
  )

  return (
    <div className="os-page" onKeyDown={onEsc}>
      <PageHeading trail={['Workspace', 'Audit history']} title="Audit history" description="Every release action and decision, attributed and append-only."
        actions={<><Button onClick={() => setRetention(r => !r)}>Retention policy</Button><Button variant="primary" icon={Download} onClick={exportLog}>Export audit log</Button></>} />
      {retention && <Notice tone="info" title="Retention">Audit events are kept indefinitely: nothing deletes them, and the database refuses edits. Generated exports (evidence packs, CSV and XLSX files) are kept for 365 days.</Notice>}
      <IntegrityBanner integrity={integrity} error={integrityError} onRetry={loadIntegrity} />

      <form className="au-filters" aria-label="Audit filters" onSubmit={e => e.preventDefault()}>
        <SearchField label="Search event names" placeholder="Search event names…" value={filters.search} onChange={set('search')} />
        {date('from', 'From (UTC)')}{date('to', 'To (UTC)')}
        <FilterSelect label="Release" value={filters.train} onChange={set('train')} options={[{ value: '', label: 'All releases' }, ...trains.map(t => ({ value: t.id, label: t.title }))]} />
        <FilterSelect label="Actor" value={filters.actor} onChange={set('actor')} options={[{ value: '', label: 'All actors' }, ...users.map(u => ({ value: u.id, label: u.displayName }))]} />
        <FilterSelect label="Event type" value={filters.entity} onChange={set('entity')} options={[{ value: '', label: 'All event types' }, ...types.map(t => ({ value: t, label: t }))]} />
        {filtered && <Button onClick={() => setFilters(EMPTY_FILTERS)}>Clear filters</Button>}
        <p className="au-count" role="status">{error ? '' : count}</p>
      </form>

      {error && <ErrorState what="The audit log" message={error} onRetry={() => load(applied, cursors[pageIndex], pageIndex === 0)} />}
      <div className={`au-split${openId !== null ? ' open' : ''}`}>
        <section className="os-panel" aria-labelledby="au-title">
          <header className="os-panel-head"><h2 id="au-title" className="os-panel-title">Event history</h2><div className="os-panel-meta">Newest first</div></header>
          {rows === null && !error ? <LoadingState what="events" /> : rows && (
            <>
              <div className="os-table-wrap">
                <table className="os-table selectable au-table" ref={tableRef}>
                  <caption className="sr-only">Audit events, newest first. Up and Down move the selection, Enter opens the details, Escape closes them.</caption>
                  <thead><tr><th scope="col">Time (UTC)</th><th scope="col">Event</th><th scope="col">Resource</th><th scope="col">Actor</th><th scope="col">Outcome</th></tr></thead>
                  <tbody>
                    {rows.length === 0 && <tr><td colSpan={5} className="os-table-empty">{filtered ? 'No events match these filters.' : 'No events recorded yet.'}</td></tr>}
                    {rows.map(r => {
                      const on = r.id === active
                      return (
                        <tr key={r.id} data-event={r.id} tabIndex={on || (active === null && r === rows[0]) ? 0 : -1} className={on ? 'selected' : undefined} aria-current={r.id === openId ? 'true' : undefined}
                          onClick={() => { setActive(r.id); setOpen(r.id) }} onKeyDown={e => onRowKey(e, rows.indexOf(r))} onFocus={() => setActive(r.id)}>
                          <td className="mono"><TwoLine primary={utcTime(r.occurredAt)} secondary={<>{utcDay(r.occurredAt)}{local(r.occurredAt) ? ` · ${local(r.occurredAt)}` : ''}</>} /></td>
                          <td><TwoLine primary={eventTitle(r.entityType, r.action)} secondary={<><span className="mono">{eventId(r.id)}</span> · {r.entityType}</>} /></td>
                          <td>{r.trainTitle ?? <span className="os-muted">None</span>}</td>
                          <td><TwoLine primary={r.actorName ?? (r.actorUserId ? 'Unknown user' : 'System')} secondary={roleOf(r.actorUserId) ?? (r.actorUserId ? undefined : 'Service account')} /></td>
                          <td><StatusChip>{outcomeOf(r.action)}</StatusChip></td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
              <footer className="au-pager">
                <span className="os-muted" aria-live="polite">{rows.length === 0 ? '' : `Showing ${from.toLocaleString('en-US')}–${to.toLocaleString('en-US')}${total !== null ? ` of ${total.toLocaleString('en-US')}` : ''} events`}</span>
                <span className="au-pager-buttons">
                  <Button disabled={pageIndex === 0 || loading} onClick={goPrev}>Previous</Button>
                  <Button disabled={!next || loading} onClick={goNext}>Next</Button>
                </span>
              </footer>
            </>
          )}
          {rows?.length === 0 && !error && !filtered && <EmptyState title="No events recorded yet">Events appear here as people change releases, gates and decisions.</EmptyState>}
        </section>

        {openId !== null && (
          <aside aria-label="Event details" className="au-aside">
            {openItem ? <Details item={openItem} role={roleOf(openItem.actorUserId)} onClose={close} onOpenTrain={onOpenTrain} closeRef={closeRef} />
              : openError ? <Panel title="Event details" meta={<button type="button" ref={closeRef} className="au-icon-button" aria-label="Close event details (Esc)" onClick={close}><X aria-hidden="true" size={16} strokeWidth={1.6} /></button>}><ErrorState what="This event" message={openError} /></Panel>
              : <LoadingState what="the event" />}
          </aside>
        )}
      </div>

      <ConfirmDialog open={confirmExport} title="Export a large audit log?" confirmLabel="Export" onConfirm={download} onCancel={() => setConfirmExport(false)}>
        {(total ?? 0).toLocaleString('en-US')} events match the current filters. The file holds at most {SERVER_EXPORT_MAX.toLocaleString('en-US')} of the newest ones and says so on its last line;
        narrow the dates to export the rest. Large files take a while to build.
      </ConfirmDialog>
    </div>
  )
}
