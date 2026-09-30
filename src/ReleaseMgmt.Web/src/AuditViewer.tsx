import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, get, getStream, type Me, type StreamRow, type UserRow } from './api'
import { primaryOf, useRowNav } from './rowNav'
import { fmtDay, fmtHMS, zoneAbbr, zonedInputToUtc } from './time'

/**
 * Audit viewer (REOS-38, Q-038a..c): filter line, dense newest-first event table, before/after JSON in the right-hand detail column
 * (same split layout as Admin), CSV export of exactly the applied filter. Read-only: nothing on this screen writes.
 * Keyboard, while focus is in the event table: j / k move the selection (and focus), Enter moves focus into the detail, Esc closes it.
 */
interface AuditItem {
  id: number; occurredAt: string; actorUserId: string | null; actorName: string | null; releaseTrainId: string | null; trainTitle: string | null
  entityType: string; entityId: string; action: string; beforeJson: string | null; afterJson: string | null
}
interface AuditPage { items: AuditItem[]; nextCursor: string | null; limit: number }
interface Filters { train: string; entity: string; actor: string; action: string; from: string; to: string }
const EMPTY: Filters = { train: '', entity: '', actor: '', action: '', from: '', to: '' }
const PAGE = 100

/** Dates are picked as calendar days in the display zone (D24); the server gets UTC instants, so a day means the same thing on screen and in the export. */
function nextDay(d: string) { const [y, m, da] = d.split('-').map(Number); return new Date(Date.UTC(y, m - 1, da + 1)).toISOString().slice(0, 10) }
export function auditQuery(f: Filters): URLSearchParams {
  const q = new URLSearchParams()
  if (f.train) q.set('train', f.train)
  if (f.entity) q.set('entity', f.entity)
  if (f.actor) q.set('actor', f.actor)
  if (f.action.trim()) q.set('action', f.action.trim())
  if (f.from) q.set('from', zonedInputToUtc(`${f.from}T00:00`))
  if (f.to) q.set('to', new Date(Date.parse(zonedInputToUtc(`${nextDay(f.to)}T00:00`)) - 1000).toISOString().replace(/\.\d{3}Z$/, 'Z'))
  return q
}

function parse(json: string | null): unknown { if (json === null) return undefined; try { return JSON.parse(json) } catch { return json } }
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const pretty = (v: unknown) => JSON.stringify(v, null, 2)

/** Top-level keys that differ between the two sides; the diff is by key (a nested change marks its parent key). */
export function changedKeys(before: unknown, after: unknown): Set<string> {
  const out = new Set<string>()
  if (isObj(before) && isObj(after)) for (const k of new Set([...Object.keys(before), ...Object.keys(after)])) { if (pretty(before[k]) !== pretty(after[k])) out.add(k) }
  return out
}

function JsonSide({ label, glyph, cls, value, changed, other }: { label: string; glyph: string; cls: string; value: unknown; changed: Set<string>; other: unknown }) {
  if (value === undefined) return <section><h3 className="cap">{label}</h3><p className="muted">○ none recorded</p></section>
  if (!isObj(value)) return <section><h3 className="cap">{label}</h3><pre className="audit-json">{typeof value === 'string' ? value : pretty(value)}</pre></section>
  return (
    <section aria-label={label}>
      <h3 className="cap">{label}</h3>
      <pre className="audit-json">
        {Object.keys(value).map(k => {
          const isChanged = changed.has(k)
          const body = `"${k}": ${pretty(value[k])}`.split('\n').join('\n    ')
          // "kind" tells a reader what the colour means without the colour: + added, − removed, ~ changed.
          const mark = !isChanged ? ' ' : isObj(other) && !(k in other) ? glyph : '~'
          return <div key={k} className={isChanged ? cls : undefined}>{mark} {body}</div>
        })}
        {Object.keys(value).length === 0 && <span className="muted">{'{ }'}</span>}
      </pre>
    </section>
  )
}

function Detail({ item, onClose, closeRef }: { item: AuditItem; onClose: () => void; closeRef: React.RefObject<HTMLButtonElement | null> }) {
  const before = parse(item.beforeJson), after = parse(item.afterJson)
  const changed = useMemo(() => changedKeys(before, after), [item]) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <>
      <h2>{item.action}</h2>
      <dl className="facts">
        <dt>When</dt><dd className="mono">{fmtDay(item.occurredAt)} {fmtHMS(item.occurredAt)} {zoneAbbr(item.occurredAt)} <span className="muted">({item.occurredAt})</span></dd>
        <dt>Actor</dt><dd>{item.actorName ?? (item.actorUserId ? item.actorUserId : 'System')}</dd>
        <dt>Train</dt><dd>{item.trainTitle ?? <span className="muted">none</span>}</dd>
        <dt>Entity</dt><dd>{item.entityType} <span className="mono muted">{item.entityId}</span></dd>
        <dt>Event</dt><dd className="mono">#{item.id}</dd>
      </dl>
      {isObj(before) && isObj(after) && <p className="muted">{changed.size === 0 ? 'No field differs.' : `${changed.size} changed: ${[...changed].join(', ')}`}</p>}
      {changed.size > 0 && <p className="muted small">Marks: + added, − removed, ~ changed.</p>}
      <JsonSide label="Before" glyph="−" cls="bad" value={before} changed={changed} other={after} />
      <JsonSide label="After" glyph="+" cls="ok" value={after} changed={changed} other={before} />
      <p><button type="button" className="text" ref={closeRef} onClick={onClose}>Close (Esc)</button></p>
    </>
  )
}

export function AuditViewer({ me }: { me: Me }) {
  const allowed = me.roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')
  const [draft, setDraft] = useState<Filters>(EMPTY)
  const [applied, setApplied] = useState<Filters>(EMPTY)
  const [rows, setRows] = useState<AuditItem[] | null>(null)
  const [next, setNext] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [selected, setSelected] = useState<number | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [users, setUsers] = useState<UserRow[]>([])
  const [types, setTypes] = useState<string[]>([])
  const seq = useRef(0)
  const closeRef = useRef<HTMLButtonElement | null>(null)

  useEffect(() => {
    if (!allowed) return
    getStream().then(setTrains).catch(() => setTrains([]))
    get<UserRow[]>('/api/v1/users').then(setUsers).catch(() => setUsers([]))
    get<string[]>('/api/v1/audit/entity-types').then(setTypes).catch(() => setTypes([]))
  }, [allowed])

  const load = useCallback(async (f: Filters, cursor: string | null) => {
    const mine = ++seq.current
    setLoading(true); setError(null)
    try {
      const q = auditQuery(f); q.set('limit', String(PAGE)); if (cursor) q.set('cursor', cursor)
      const page = await get<AuditPage>(`/api/v1/audit?${q}`)
      if (mine !== seq.current) return   // a newer Apply superseded this request
      setRows(prev => cursor && prev ? [...prev, ...page.items] : page.items)
      setNext(page.nextCursor)
    } catch (e) {
      if (mine !== seq.current) return
      setError(e instanceof ApiError && e.status === 400 ? (e.body?.message ?? 'Those filters are not valid') : (e as Error).message)
    } finally { if (mine === seq.current) setLoading(false) }
  }, [])

  useEffect(() => { if (allowed) load(applied, null) }, [allowed, applied, load])

  const apply = (e?: React.FormEvent) => { e?.preventDefault(); setSelected(null); setApplied({ ...draft }) }
  const clear = () => { setDraft(EMPTY); setSelected(null); setApplied(EMPTY) }
  const set = (k: keyof Filters) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setDraft(d => ({ ...d, [k]: e.target.value }))

  const sel = rows?.find(r => r.id === selected) ?? null
  // j / k / Enter only while focus is in the table (WCAG 2.1.4); moving focuses the row's time button, Enter moves focus into the detail.
  const [, , nav] = useRowNav(rows?.length ?? 0,
    i => { if (rows?.[i]) { setSelected(rows[i].id); requestAnimationFrame(() => closeRef.current?.focus()) } },
    i => { if (rows?.[i]) setSelected(rows[i].id) })
  /** Close the detail and give focus back to the event's row, so it does not fall to <body> with the Close button. */
  const close = () => {
    const id = selected
    setSelected(null)
    requestAnimationFrame(() => primaryOf(document.getElementById(`audit-${id}`))?.focus())
  }
  const onEsc = (e: React.KeyboardEvent) => {
    const t = e.target as HTMLElement
    if (e.key === 'Escape' && selected !== null && !['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName)) { e.preventDefault(); close() }
  }

  if (!allowed) return <><h1>Audit</h1><p className="muted" role="status">The audit log is available to Release Train Engineers, Release Managers and Governance Officers.</p></>

  const csvQuery = auditQuery(applied).toString()
  const anyFilter = Object.values(applied).some(Boolean)
  return (
    <>
      <h1>Audit</h1>
      <form className="inline-form audit-filters" onSubmit={apply} aria-label="Audit filters">
        <label>Train <select className="line" value={draft.train} onChange={set('train')}>
          <option value="">All trains</option>{trains.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label>
        <label>Entity <select className="line" value={draft.entity} onChange={set('entity')}>
          <option value="">All entities</option>{types.map(t => <option key={t}>{t}</option>)}</select></label>
        <label>Actor <select className="line" value={draft.actor} onChange={set('actor')}>
          <option value="">Anyone</option>{users.map(u => <option key={u.id} value={u.id}>{u.displayName}</option>)}</select></label>
        <label>Action <input className="line" type="text" value={draft.action} onChange={set('action')} placeholder="contains…" /></label>
        <label>From <input className="line" type="date" value={draft.from} onChange={set('from')} /></label>
        <label>To <input className="line" type="date" value={draft.to} onChange={set('to')} /></label>
        <button type="submit" className="text">Apply</button>
        <button type="button" className="text" onClick={clear}>Clear</button>
        <a className="textbtn" href={`/api/v1/audit.csv${csvQuery ? `?${csvQuery}` : ''}`} download>Export CSV</a>
      </form>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      <p className="muted" role="status">
        {error ? ''
          : loading && !rows ? 'Loading…'
          : rows ? `${rows.length} event${rows.length === 1 ? '' : 's'}${next ? ' loaded, more available' : ''}${anyFilter ? ' matching the filters' : ''} · newest first · times in ${zoneAbbr()}` : ''}
      </p>
      <div className="split" onKeyDown={onEsc}>
        <div>
          <table className="grid audit-table" aria-label="Audit events, newest first" {...nav}>
            <thead><tr><th>Time</th><th>Actor</th><th>Train</th><th>Entity</th><th>Action</th></tr></thead>
            <tbody>
              {rows?.map(r => (
                <tr key={r.id} id={`audit-${r.id}`} className={r.id === selected ? 'selected' : undefined} onClick={() => setSelected(r.id)}>
                  <td className="mono"><button type="button" className="plainlink text" data-rownav aria-current={r.id === selected ? 'true' : undefined}
                    onClick={e => { e.stopPropagation(); setSelected(r.id) }} aria-label={`${fmtDay(r.occurredAt)} ${fmtHMS(r.occurredAt)}, event ${r.id}, ${r.action}`}>{fmtDay(r.occurredAt)} {fmtHMS(r.occurredAt)}</button></td>
                  <td>{r.actorName ?? (r.actorUserId ? <span className="muted">unknown user</span> : <span className="muted">○ System</span>)}</td>
                  <td>{r.trainTitle ?? <span className="muted">none</span>}</td>
                  <td>{r.entityType} <span className="mono muted" title={r.entityId}>{r.entityId.length > 10 ? `${r.entityId.slice(0, 8)}…` : r.entityId}</span></td>
                  <td>{r.action}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows?.length === 0 && !error && <p className="muted">{anyFilter ? 'No events match these filters.' : 'No events recorded yet.'}</p>}
          {next && <p><button type="button" className="text" disabled={loading} onClick={() => load(applied, next)}>{loading ? 'Loading…' : 'Load more'}</button></p>}
        </div>
        <aside className="detail" aria-label="Event detail">
          {sel ? <Detail item={sel} onClose={close} closeRef={closeRef} /> : <p className="muted">Select an event to see what changed. In the table, j / k move, Enter opens the detail, Esc closes it.</p>}
        </aside>
      </div>
    </>
  )
}
