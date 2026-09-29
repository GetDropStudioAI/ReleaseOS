import { useEffect, useState } from 'react'
import {
  addTask, gateAction, getGate, getOwners, getProducts, getWindow, putWindow, taskAction,
  type GateDetail, type GateRow, type Owner, type ProductsResponse, type WindowRow,
} from './api'
import { day, errMsg, tMinus } from './format'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import type { ApiError } from './api'
import { StepInspector } from './Runbook'
import { bulkKey, type BulkDraft } from './Bulk'

export type Selection = { kind: 'gate'; id: string } | { kind: 'task'; gateId: string; id: string } | { kind: 'product'; id: string } | { kind: 'step'; id: string } | null

const STATUS: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'certified' }, Waived: { g: '●', cls: 'warn', word: 'waived' }, Failed: { g: '✗', cls: 'bad', word: 'failed' },
  InProgress: { g: '◐', cls: 'accent', word: 'in progress' }, Pending: { g: '○', cls: 'muted', word: 'pending' },
}

// ---- Deployment window (GET/PUT /trains/{id}/window). Shown in the viewer's zone with its abbreviation (Q-008). -----------------------------
const hm = (d: Date) => d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })
const zone = (d: Date) => new Intl.DateTimeFormat('en-GB', { timeZoneName: 'short' }).formatToParts(d).find(p => p.type === 'timeZoneName')?.value ?? ''
const localInput = (iso: string) => { const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}` }
const toUtc = (local: string) => new Date(local).toISOString().replace(/\.\d{3}Z$/, 'Z')

export function windowText(w: WindowRow) {
  const a = new Date(w.startsAt), b = new Date(w.endsAt)
  const same = a.toDateString() === b.toDateString()
  return same ? `${hm(a)}–${hm(b)} ${zone(b)}` : `${day(w.startsAt.slice(0, 10))} ${hm(a)} – ${day(w.endsAt.slice(0, 10))} ${hm(b)} ${zone(b)}`
}

export function WindowLine({ trainId, canEdit, refreshKey, onChanged }: { trainId: string; canEdit: boolean; refreshKey: number; onChanged: () => void }) {
  const [win, setWin] = useState<WindowRow | null | undefined>(undefined)
  const [draft, setDraft] = useDraft<{ start: string; end: string }>(`window:${trainId}`)   // survives a closed tab (session engine)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const editing = draft !== undefined
  const start = draft?.start ?? '', end = draft?.end ?? ''
  useEffect(() => { getWindow(trainId).then(setWin).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  if (win === undefined) return null
  const open = () => { setErr(null); setConflict(null); setDraft({ start: win ? localInput(win.startsAt) : '', end: win ? localInput(win.endsAt) : '' }) }
  const save = async (version = win?.version) => {
    setErr(null); setConflict(null)
    if (!start || !end) { setErr('Enter both a start and an end.'); return }
    try { await putWindow(trainId, toUtc(start), toUtc(end), version); setDraft(undefined); onChanged() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }   // a 409 keeps the draft; the user chooses below
  }
  if (!editing) return (
    <span>Window {win ? <span className="mono">{windowText(win)}</span> : <span className="muted">not set</span>}
      {canEdit && <> <button type="button" className="text" onClick={open}>{win ? 'Edit window' : 'Set window'}</button></>}
      {err && <span className="bad" role="alert"> ✗ {err}</span>}</span>
  )
  return (
    <span className="inline-form">
      <label>Starts <input className="line" type="datetime-local" value={start} onChange={e => setDraft({ start: e.target.value, end })} /></label>{' '}
      <label>Ends <input className="line" type="datetime-local" value={end} onChange={e => setDraft({ start, end: e.target.value })} /></label>{' '}
      <button type="button" className="text" onClick={() => save()}>Save window</button>{' '}
      <button type="button" className="text" onClick={() => setDraft(undefined)}>Cancel</button>
      {err && <span className="bad" role="alert"> ✗ {err}</span>}
      {conflict && (
        <Conflict error={conflict} what="window">
          <button type="button" className="text" onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}
          <button type="button" className="text" onClick={() => { setDraft(undefined); setConflict(null) }}>Use theirs</button>
        </Conflict>)}
    </span>
  )
}

// ---- Bundled products ---------------------------------------------------------------------------------------------------------------
const HEALTH_CLS: Record<string, string> = { Ready: 'ok', 'On track': 'ok', 'At risk': 'warn' }
const SEV_CLS = (s: string) => s === 'Critical' || s === 'High' ? 'bad' : 'warn'

export function Products({ trainId, refreshKey, selection, onSelect }: { trainId: string; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void }) {
  const [data, setData] = useState<ProductsResponse | null>(null)
  useEffect(() => { getProducts(trainId).then(setData).catch(() => setData(null)) }, [trainId, refreshKey])
  if (!data) return null
  const pct = data.tasksTotal === 0 ? 0 : Math.round(100 * data.tasksDone / data.tasksTotal)
  return (
    <section aria-label="Bundled products">
      <div className="section-head"><h2 className="cap dark">Bundled products</h2>
        <span className="muted"><span className="mono label">{data.tasksDone} / {data.tasksTotal}</span> tasks done · <span className="mono label">{pct}%</span></span></div>
      {data.products.length === 0 ? <p className="muted">No products bundled yet.</p> : (
        <table className="grid">
          <thead><tr><th>Product</th><th>Version</th><th>Project</th><th className="n">Tasks</th><th>Blockers</th><th>Health</th></tr></thead>
          <tbody>
            {data.products.map(p => (
              <tr key={p.id} aria-selected={selection?.kind === 'product' && selection.id === p.id} className={selection?.kind === 'product' && selection.id === p.id ? 'selected' : undefined}>
                <td><button type="button" className="text" onClick={() => onSelect({ kind: 'product', id: p.id })}>{p.name}</button></td>
                <td className="mono">{p.versionTag}</td><td className="mono muted">{p.projectCode}</td>
                <td className="n">{p.tasksDone} / {p.tasksTotal}</td>
                <td>{p.openBlockers.length === 0 ? <span className="muted">—</span> : p.openBlockers.map((s, i) => <span key={i} className={SEV_CLS(s)}>▲ {s} </span>)}</td>
                <td className={HEALTH_CLS[p.health] ?? 'muted'}>{p.health}</td>
              </tr>
            ))}
          </tbody>
        </table>)}
    </section>
  )
}

// ---- Gate timeline: x is in business days (T−n), so a gap of one business day is one step wherever weekends fall ------------------------
export function Timeline({ gates, todayT, targetDate, selectedId, onSelect }: { gates: GateRow[]; todayT: number; targetDate: string; selectedId: string | null; onSelect: (id: string) => void }) {
  const W = 730, L = 50, R = 680
  // The axis spans the first gate to the target (or to today if the target has passed); "now" is pinned to the left edge when it is earlier than the first gate.
  const max = Math.max(1, ...gates.map(g => g.tMinus))
  const min = Math.min(0, todayT)
  const x = (t: number) => L + ((max - Math.min(t, max)) / (max - min)) * (R - L)
  const nowX = x(todayT)
  const nowPinned = todayT > max
  const first = (name: string | null) => name ? name.split(' ')[0] : ''
  const stateText = (g: GateRow) => g.status === 'Certified' ? `✓ ${g.certifiedOn ? day(g.certifiedOn) : ''}${g.certifiedBy ? ' · ' + first(g.certifiedBy) : ''}`
    : `${(STATUS[g.status] ?? STATUS.Pending).word}${g.ownerName ? ' · ' + first(g.ownerName) : ''}`
  return (
    <section aria-label="Gate timeline">
      <h2 className="cap dark">Gates · business days to target</h2>
      <svg className="timeline" width="100%" viewBox={`0 0 ${W} 104`} role="group"
           aria-label={`Gate timeline to ${day(targetDate)}: ` + gates.map(g => `${g.name} ${STATUS[g.status]?.word ?? g.status}`).join(', ')}>
        <line className="tl-axis" x1={L} x2={R} y1="52" y2="52" />
        <line className="tl-now" x1={nowX} x2={nowX} y1="16" y2="62" strokeDasharray="3 3" />
        <text className="tl-now-label" x={nowX + 4} y="11">now{nowPinned ? ` · ${tMinus(todayT)}` : ''}</text>
        {gates.map((g, i) => {
          const gx = x(g.tMinus), st = STATUS[g.status] ?? STATUS.Pending
          const anchor = gx < L + 20 ? 'start' : gx > R - 20 ? 'end' : 'middle'
          const nameY = i % 2 === 0 ? 30 : 16
          return (
            <g key={g.id} className={`tl-gate ${selectedId === g.id ? 'sel' : ''}`} tabIndex={0} role="button" aria-label={`${g.name}, ${st.word}, due ${day(g.dueOn)}`}
               onClick={() => onSelect(g.id)} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(g.id) } }}>
              <rect className="tl-hit" x={anchor === 'start' ? gx - 12 : anchor === 'end' ? gx - 108 : gx - 60} y={4} width={120} height={96} />
              <text className="tl-name" x={gx} y={nameY} textAnchor={anchor}>{g.name}</text>
              <circle className={`tl-dot ${st.cls}`} cx={gx} cy="52" r="6" />
              <text className="tl-day mono" x={gx} y="74" textAnchor={anchor}>{day(g.dueOn)} · {tMinus(g.tMinus)}</text>
              <text className={`tl-state ${st.cls}`} x={gx} y="90" textAnchor={anchor}>{stateText(g)}</text>
            </g>
          )
        })}
        <g><path className="tl-target" d={`M${R} 45 L${R + 7} 52 L${R} 59 L${R - 7} 52 Z`} />
          <text className="tl-name" x={R + 8} y="30" textAnchor="end">Target</text>
          <text className="tl-day mono" x={R + 8} y="74" textAnchor="end">{day(targetDate)} · T0</text></g>
      </svg>
    </section>
  )
}

// ---- Checklist of the selected gate ----------------------------------------------------------------------------------------------------------
export function Checklist({ gateId, trainId, refreshKey, selection, onSelect, onChanged }: { gateId: string; trainId: string; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void; onChanged: () => void }) {
  const [gate, setGate] = useState<GateDetail | null>(null)
  const [draft, setDraft] = useDraft<{ text: string; ownerId: string }>(`addTask:${gateId}`)   // survives a closed tab (session engine)
  const adding = draft !== undefined
  const text = draft?.text ?? ''
  const [owners, setOwners] = useState<Owner[]>([])
  const ownerId = draft?.ownerId || owners[0]?.id || ''
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [bulk, setBulk] = useDraft<BulkDraft>(bulkKey(trainId))
  const openBulk = () => setBulk({ text: bulk?.text ?? '', gateId: bulk?.gateId || gateId, open: true })
  useEffect(() => { getGate(gateId).then(setGate).catch(e => setErr(errMsg(e))) }, [gateId, refreshKey])
  useEffect(() => { if (adding && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [adding, owners.length])
  if (!gate) return err ? <p className="bad" role="alert">✗ {err}</p> : null
  const done = gate.tasks.filter(t => t.done).length
  const toggle = async (t: GateDetail['tasks'][number]) => {
    setErr(null); setConflict(null)
    try { await taskAction(t.id, t.done ? 'reopen' : 'complete', t.version); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const add = async () => {
    const owner = owners.find(o => o.id === ownerId)
    if (!text.trim() || !owner) { setErr('A task needs a description and an owner.'); return }
    setErr(null)
    try { await addTask(gate.id, text.trim(), owner); setDraft(undefined); onChanged() } catch (e) { setErr(errMsg(e)) }
  }
  return (
    <section aria-label="Checklist">
      <div className="section-head"><h2 className="cap accent">{gate.name} · checklist</h2>
        <span className="muted"><span className="mono label">{done} / {gate.tasks.length}</span> done · <button type="button" className="text" onClick={openBulk}>Paste tasks</button> · <button type="button" className="text" onClick={() => setDraft(adding ? undefined : { text: '', ownerId: '' })}>Add task</button></span></div>
      {adding && (
        <p className="inline-form">
          <label>Task <input className="line wide" value={text} onChange={e => setDraft({ text: e.target.value, ownerId })} placeholder="What has to happen" /></label>{' '}
          <label>Owner <select className="line" value={ownerId} onChange={e => setDraft({ text, ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>{' '}
          <button type="button" className="text" onClick={add}>Add</button> <button type="button" className="text" onClick={() => setDraft(undefined)}>Cancel</button>
        </p>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="task" />}
      {gate.tasks.length === 0 ? <p className="muted">No tasks on this gate.</p> : (
        <table className="grid">
          <thead><tr><th>State</th><th>Task</th><th>Owner</th><th>Product</th><th>Done</th><th /></tr></thead>
          <tbody>
            {gate.tasks.map(t => {
              const sel = selection?.kind === 'task' && selection.id === t.id
              return (
                <tr key={t.id} aria-selected={sel} className={sel ? 'selected' : undefined}>
                  <td className={t.done ? 'ok nowrap' : 'nowrap'}>{t.done ? '✓ done' : '○ open'}</td>
                  <td><button type="button" className="text plainlink" onClick={() => onSelect({ kind: 'task', gateId: gate.id, id: t.id })}>{t.description}</button></td>
                  <td>{t.owner ?? <span className="muted">—</span>}</td>
                  <td>{t.product ?? <span className="muted">—</span>}</td>
                  <td className="mono muted nowrap">{t.completedAt ? new Date(t.completedAt).toLocaleString('en-GB', { weekday: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—'}</td>
                  <td><button type="button" className="text" onClick={() => toggle(t)}>{t.done ? 'Reopen' : 'Mark done'}</button></td>
                </tr>
              )
            })}
          </tbody>
        </table>)}
    </section>
  )
}

// ---- Inspector (right pane): gate, task or product, with the reason Certify is unavailable -------------------------------------------------
export function Inspector({ selection, trainId, canPlan, refreshKey, onClose, onChanged }: { selection: Selection; trainId: string | null; canPlan: boolean; refreshKey: number; onClose: () => void; onChanged: () => void }) {
  const gateId = selection?.kind === 'gate' ? selection.id : selection?.kind === 'task' ? selection.gateId : null
  const [gate, setGate] = useState<GateDetail | null>(null)
  const [products, setProducts] = useState<ProductsResponse | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  // Messages belong to what is selected, so they reset only when the selection changes. Refetching after a 409 must not wipe the conflict notice.
  useEffect(() => { setErr(null); setConflict(null) }, [selection?.kind, gateId, selection && selection.kind !== 'gate' ? selection.id : null])   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (gateId) getGate(gateId).then(setGate).catch(e => setErr(errMsg(e))); else setGate(null) }, [gateId, refreshKey])
  useEffect(() => { if (selection?.kind === 'product' && trainId) getProducts(trainId).then(setProducts).catch(() => setProducts(null)) }, [selection, trainId, refreshKey])

  if (!selection) return <p className="muted">Select a train, gate or task to see its detail here.</p>
  const act = async (fn: () => Promise<unknown>) => { setErr(null); setConflict(null); try { await fn(); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() } }
  const head = (kind: string) => <div className="section-head"><span className="cap">Inspector · {kind}</span><button type="button" className="text quiet" aria-label="Close inspector" onClick={onClose}>Close</button></div>

  if (selection.kind === 'step') return <>{head('step')}{trainId ? <StepInspector stepId={selection.id} trainId={trainId} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} /> : null}</>
  if (selection.kind === 'product') {
    const p = products?.products.find(x => x.id === selection.id)
    return (<>{head('product')}{!p ? <p className="muted">Loading…</p> : (<>
      <h2>{p.name}</h2>
      <dl className="facts"><dt>Version</dt><dd className="mono">{p.versionTag}</dd><dt>Project</dt><dd className="mono">{p.projectCode}</dd>
        <dt>Tasks</dt><dd>{p.tasksDone} of {p.tasksTotal} done</dd><dt>Health</dt><dd className={HEALTH_CLS[p.health] ?? 'muted'}>{p.health}</dd>
        <dt>Blockers</dt><dd>{p.openBlockers.length === 0 ? 'None open' : p.openBlockers.map((s, i) => <span key={i} className={SEV_CLS(s)}>▲ {s} </span>)}</dd></dl></>)}</>)
  }
  if (!gate) return <>{head(selection.kind)}{err ? <p className="bad" role="alert">✗ {err}</p> : <p className="muted">Loading…</p>}</>
  const st = STATUS[gate.status] ?? STATUS.Pending

  if (selection.kind === 'task') {
    const t = gate.tasks.find(x => x.id === selection.id)
    if (!t) return <>{head('task')}<p className="muted">This task no longer exists.</p></>
    return (<>{head('task')}
      <h2>{t.description}</h2>
      <dl className="facts"><dt>State</dt><dd className={t.done ? 'ok' : undefined}>{t.done ? '✓ done' : '○ open'}</dd><dt>Owner</dt><dd>{t.owner ?? '—'}</dd><dt>Product</dt><dd>{t.product ?? '—'}</dd>
        <dt>Gate</dt><dd>{gate.name}</dd>{t.done && <><dt>Completed</dt><dd>{t.completedBy ?? ''} {t.completedAt ? new Date(t.completedAt).toLocaleString('en-GB') : ''}</dd></>}</dl>
      <p><button type="button" className="text" onClick={() => act(() => taskAction(t.id, t.done ? 'reopen' : 'complete', t.version))}>{t.done ? 'Reopen task' : 'Mark done'}</button></p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="task" />}</>)
  }

  const canStart = gate.status === 'Pending', canFail = gate.status === 'InProgress', canReopen = gate.status === 'Failed' || gate.status === 'Certified'
  const certifiable = gate.status === 'InProgress' || gate.status === 'Failed'
  return (<>{head('gate')}
    <h2>{gate.name}</h2>
    <dl className="facts"><dt>Status</dt><dd className={st.cls}>{st.g} {st.word}</dd><dt>Class</dt><dd>{gate.class}</dd>
      <dt>Due</dt><dd>{day(gate.dueOn)} · <span className="mono">{tMinus(gate.tMinus)}</span></dd><dt>Owner</dt><dd>{gate.ownerName ?? '—'}</dd>
      <dt>Tasks</dt><dd>{gate.tasks.filter(t => t.done).length} of {gate.tasks.length} done</dd></dl>
    <p className="actions-col">
      {canStart && <button type="button" className="text" onClick={() => act(() => gateAction(gate.id, 'start', gate.version))}>Start gate</button>}
      {certifiable && <button type="button" className="text" disabled={!gate.certify.eligible} onClick={() => act(() => gateAction(gate.id, 'certify', gate.version))}>Certify gate</button>}
      {canFail && <button type="button" className="text destructive" onClick={() => act(() => gateAction(gate.id, 'fail', gate.version))}>Fail gate</button>}
      {canReopen && <button type="button" className="text" onClick={() => act(() => gateAction(gate.id, 'reopen', gate.version))}>{gate.status === 'Certified' ? 'Decertify (reopen)' : 'Reopen gate'}</button>}
    </p>
    {certifiable && (gate.certify.eligible
      ? <p className="ok" role="status">✓ You can certify this gate now.</p>
      : <div role="status" aria-label="Why Certify is unavailable"><p className="cap">Certify unavailable</p>
          <ul className="plain">{gate.certify.reasons.map((r, i) => <li key={i} className="bad">✗ {r}</li>)}</ul></div>)}
    {gate.status === 'Pending' && <p className="muted">Start the gate first, then work its checklist.</p>}
    {err && <p className="bad" role="alert">✗ {err}</p>}
    {conflict && <Conflict error={conflict} what="gate" />}</>)
}
