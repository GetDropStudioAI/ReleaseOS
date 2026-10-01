import { useEffect, useState } from 'react'
import {
  addTask, gateAction, getGate, getOwners, getProducts, getWindow, putWindow, taskAction,
  type GateDetail, type GateRow, type Owner, type ProductsResponse, type WindowRow,
} from './api'
import {
  addGate, addProduct, patchGate, patchProduct, removeGate, removeProduct, GATE_CLASSES, REQUIRED_BEFORE,
  type GateDefinition, type GateInput, type ProductRowV,
} from './api'
import { day, errMsg, tMinus } from './format'
import { fmtDay, fmtDayTime, fmtHM, utcToZonedInput, zonedInputToUtc, zoneAbbr } from './time'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import type { ApiError } from './api'
import { StepInspector } from './Runbook'
import { Evidence } from './Evidence'
import { WaiversPanel } from './Freezes'
import { bulkKey, type BulkDraft } from './Bulk'

export type Selection = { kind: 'gate'; id: string } | { kind: 'task'; gateId: string; id: string } | { kind: 'product'; id: string } | { kind: 'step'; id: string } | null

const STATUS: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'certified' }, Waived: { g: '●', cls: 'warn', word: 'waived' }, Failed: { g: '✗', cls: 'bad', word: 'failed' },
  InProgress: { g: '◐', cls: 'accent', word: 'in progress' }, Pending: { g: '○', cls: 'muted', word: 'pending' },
}

// ---- Deployment window (GET/PUT /trains/{id}/window). Shown in the viewer's zone with its abbreviation (Q-008). -----------------------------
const localInput = utcToZonedInput
const toUtc = zonedInputToUtc

export function windowText(w: WindowRow) {
  const same = fmtDay(w.startsAt) === fmtDay(w.endsAt)
  return same ? `${fmtHM(w.startsAt)}–${fmtHM(w.endsAt)} ${zoneAbbr(w.endsAt)}` : `${fmtDayTime(w.startsAt)} – ${fmtDayTime(w.endsAt)} ${zoneAbbr(w.endsAt)}`
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

// REOS-81: products are added inline under the heading, edited inline in their row, and removed with an inline confirm (no modal).
// The plan is locked once the train is Executing (the API refuses with the reason; the buttons are not offered then).
type ProductDraft = { name: string; versionTag: string; projectCode: string }
const planLocked = (status: string | undefined) => status === 'Executing' || status === 'Complete' || status === 'Aborted'
const LOCK_REASON: Record<string, string> = {
  Executing: 'The train is Executing: products and gates are locked once deployment has started.',
  Complete: 'The train is Complete: its products and gates can no longer be changed.',
  Aborted: 'The train is Aborted: its products and gates can no longer be changed.',
}

export function Products({ trainId, refreshKey, selection, onSelect, canPlan = false, trainStatus, onChanged }: { trainId: string; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void; canPlan?: boolean; trainStatus?: string; onChanged?: () => void }) {
  const [data, setData] = useState<ProductsResponse | null>(null)
  const [tick, setTick] = useState(0)
  const [adding, setAdding] = useDraft<ProductDraft>(`addProduct:${trainId}`)   // survives a closed tab (session engine)
  const [editing, setEditing] = useDraft<ProductDraft & { id: string }>(`editProduct:${trainId}`)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  useEffect(() => { getProducts(trainId).then(setData).catch(() => setData(null)) }, [trainId, refreshKey, tick])
  if (!data) return null
  const pct = data.tasksTotal === 0 ? 0 : Math.round(100 * data.tasksDone / data.tasksTotal)
  const editable = canPlan && !planLocked(trainStatus)
  const run = async (fn: () => Promise<unknown>, after: () => void) => {
    setErr(null); setConflict(null)
    try { await fn(); after(); setTick(n => n + 1); onChanged?.() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); setTick(n => n + 1) }   // a 409 or 422 keeps the draft
  }
  const add = () => {
    if (!adding) return
    if (!adding.name.trim() || !adding.versionTag.trim()) { setErr('A product needs a name and a version tag.'); return }
    void run(() => addProduct(trainId, { name: adding.name, versionTag: adding.versionTag, projectCode: adding.projectCode }), () => setAdding(undefined))
  }
  const rows = data.products as ProductRowV[]
  const save = (p: ProductRowV, version = p.version) => {
    if (!editing) return
    void run(() => patchProduct(p.id, { name: editing.name, versionTag: editing.versionTag, projectCode: editing.projectCode }, version), () => setEditing(undefined))
  }
  const remove = (p: ProductRowV) => run(() => removeProduct(p.id, p.version), () => { setConfirming(null); if (selection?.kind === 'product' && selection.id === p.id) onSelect(null) })
  const editRow = rows.find(p => p.id === editing?.id)
  return (
    <section aria-label="Bundled products">
      <div className="section-head"><h2 className="cap dark">Bundled products</h2>
        <span className="muted"><span className="mono label">{data.tasksDone} / {data.tasksTotal}</span> tasks done · <span className="mono label">{pct}%</span>
          {editable && <> · <button type="button" className="text" aria-expanded={adding !== undefined} onClick={() => { setErr(null); setAdding(adding ? undefined : { name: '', versionTag: '', projectCode: '' }) }}>Add product</button></>}</span></div>
      {editable && adding && (
        <p className="inline-form" role="group" aria-label="New product">
          <label>Product <input className="line" value={adding.name} onChange={e => setAdding({ ...adding, name: e.target.value })} placeholder="Payments API" /></label>{' '}
          <label>Version <input className="line mid" value={adding.versionTag} onChange={e => setAdding({ ...adding, versionTag: e.target.value })} placeholder="4.2.0" /></label>{' '}
          <label>Project <input className="line mid" value={adding.projectCode} onChange={e => setAdding({ ...adding, projectCode: e.target.value })} placeholder="optional" /></label>{' '}
          <button type="button" className="text" onClick={() => setAdding(undefined)}>Cancel</button>{' '}
          <button type="button" className="text" onClick={add}>Add product</button>
        </p>)}
      {canPlan && trainStatus && planLocked(trainStatus) && <p className="muted">{LOCK_REASON[trainStatus]}</p>}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && (
        <Conflict error={conflict} what="product">
          {editRow && editing && <><button type="button" className="text" onClick={() => save(editRow, (conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}</>}
          <button type="button" className="text" onClick={() => { setEditing(undefined); setConfirming(null); setConflict(null) }}>Use theirs</button>
        </Conflict>)}
      {rows.length === 0 ? <p className="muted">No products bundled yet.</p> : (
        <table className="grid">
          <thead><tr><th>Product</th><th>Version</th><th>Project</th><th className="n">Tasks</th><th>Blockers</th><th>Health</th>{editable && <th><span className="sr-only">Actions</span></th>}</tr></thead>
          <tbody>
            {rows.map(p => {
              const sel = selection?.kind === 'product' && selection.id === p.id
              if (editable && editing && editing.id === p.id) return (
                <tr key={p.id} aria-selected={sel} className={sel ? 'selected' : undefined} aria-label={`Editing ${p.name}`}>
                  <td><input className="line" aria-label="Product name" value={editing.name} onChange={e => setEditing({ ...editing, name: e.target.value })} /></td>
                  <td><input className="line mid" aria-label="Version tag" value={editing.versionTag} onChange={e => setEditing({ ...editing, versionTag: e.target.value })} /></td>
                  <td><input className="line mid" aria-label="Project code" value={editing.projectCode} onChange={e => setEditing({ ...editing, projectCode: e.target.value })} /></td>
                  <td className="n">{p.tasksDone} / {p.tasksTotal}</td><td /><td />
                  <td className="nowrap"><button type="button" className="text" onClick={() => setEditing(undefined)}>Cancel</button>{' '}<button type="button" className="text" onClick={() => save(p)}>Save</button></td>
                </tr>)
              return (
                <tr key={p.id} aria-selected={sel} className={sel ? 'selected' : undefined}>
                  <td><button type="button" className="text" onClick={() => onSelect({ kind: 'product', id: p.id })}>{p.name}</button></td>
                  <td className="mono">{p.versionTag}</td><td className="mono muted">{p.projectCode || '—'}</td>
                  <td className="n">{p.tasksDone} / {p.tasksTotal}</td>
                  <td>{p.openBlockers.length === 0 ? <span className="muted">—</span> : p.openBlockers.map((s, i) => <span key={i} className={SEV_CLS(s)}>▲ {s} </span>)}</td>
                  <td className={HEALTH_CLS[p.health] ?? 'muted'}>{p.health}</td>
                  {editable && <td className={confirming === p.id ? undefined : 'nowrap'}>{confirming === p.id
                    ? <span role="group" aria-label={`Confirm removing ${p.name}`}><span className="bad">Remove?</span>{' '}<button type="button" className="text" onClick={() => setConfirming(null)}>Keep</button>{' '}<button type="button" className="text destructive" onClick={() => remove(p)}>Confirm remove</button></span>
                    : <><button type="button" className="text" aria-label={`Edit ${p.name}`} onClick={() => { setErr(null); setConflict(null); setEditing({ id: p.id, name: p.name, versionTag: p.versionTag, projectCode: p.projectCode }) }}>Edit</button>{' '}
                        <button type="button" className="text destructive" aria-label={`Remove ${p.name}`} onClick={() => { setErr(null); setConflict(null); setConfirming(p.id) }}>Remove</button></>}</td>}
                </tr>
              )
            })}
          </tbody>
        </table>)}
    </section>
  )
}

// ---- Gate timeline: x is in business days (T−n), so a gap of one business day is one step wherever weekends fall ------------------------
export function Timeline({ gates, todayT, targetDate, selectedId, onSelect, trainId, trainStatus, canPlan = false, onChanged }: { gates: GateRow[]; todayT: number; targetDate: string; selectedId: string | null; onSelect: (id: string) => void; trainId?: string; trainStatus?: string; canPlan?: boolean; onChanged?: () => void }) {
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
      <div className="section-head"><h2 className="cap dark">Gates · business days to target</h2>
        {canPlan && trainId && !planLocked(trainStatus) && <AddGateButton trainId={trainId} trainStatus={trainStatus} />}</div>
      {canPlan && trainId && !planLocked(trainStatus) && <AddGate trainId={trainId} trainStatus={trainStatus} gates={gates} onAdded={id => { onChanged?.(); onSelect(id) }} />}
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

// ---- REOS-81: a gate's definition, added under the timeline and edited in the Inspector -------------------------------------------------------
// Due is either business days before the target (D9) or a date the server turns into that offset (a weekend or holiday is refused with the nearest dates).
// The status is never part of this form: it moves only through Start / Certify / Fail / Reopen / Waive.
type GateDraft = { name: string; gateClass: string; mode: 'offset' | 'date'; offset: string; dueOn: string; requiredBefore: string; ownerId: string; position: string }
const addGateKey = (trainId: string) => `addGate:${trainId}`
const firstOpenStatus = (trainStatus?: string) => trainStatus === 'Gated' ? 'Executing' : 'Gated'
const emptyGate = (trainStatus?: string): GateDraft => ({ name: '', gateClass: 'Standard', mode: 'offset', offset: '', dueOn: '', requiredBefore: firstOpenStatus(trainStatus), ownerId: '', position: '' })
type GateRowSeq = GateRow & { sequenceOrder: number }

function gateInput(d: GateDraft, owners: Owner[], forEdit: boolean): GateInput | string {
  if (!d.name.trim()) return 'A gate needs a name.'
  const owner = owners.find(o => o.id === d.ownerId)
  if (!owner) return 'Choose who owns the gate.'
  const due = d.mode === 'offset'
    ? (/^\d+$/.test(d.offset.trim()) ? { offsetDays: Number(d.offset.trim()) } : null)
    : (d.dueOn ? { dueOn: d.dueOn } : null)
  if (!due) return d.mode === 'offset' ? 'Enter how many business days before the target the gate is due (0 or more).' : 'Choose the due date.'
  const ownerPart = owner.kind === 'team' ? { ownerTeamId: owner.id, ...(forEdit ? {} : { ownerUserId: null }) } : { ownerUserId: owner.id, ...(forEdit ? {} : { ownerTeamId: null }) }
  return { gateName: d.name.trim(), gateClass: d.gateClass, requiredBeforeStatus: d.requiredBefore, ...due, ...ownerPart, ...(!forEdit && d.position ? { sequenceOrder: Number(d.position) } : {}) }
}

function GateFields({ d, set, owners, stack, positions, trainStatus }: { d: GateDraft; set: (d: GateDraft) => void; owners: Owner[]; stack?: boolean; positions?: { value: string; label: string }[]; trainStatus?: string }) {
  const requiredChoices = REQUIRED_BEFORE.filter(r => trainStatus !== 'Gated' || r !== 'Gated' || d.requiredBefore === 'Gated')
  return (
    <div className={stack ? 'inline-form stack' : 'inline-form'}>
      <label>Gate <input className={stack ? 'line block' : 'line'} value={d.name} onChange={e => set({ ...d, name: e.target.value })} placeholder="QA Sign-off" /></label>
      <label>Class <select className="line" value={d.gateClass} onChange={e => set({ ...d, gateClass: e.target.value })}>{GATE_CLASSES.map(c => <option key={c}>{c}</option>)}</select></label>
      <span>
        <span className="tabs" role="group" aria-label="Due by">
          <button type="button" className="choice" aria-pressed={d.mode === 'offset'} onClick={() => set({ ...d, mode: 'offset' })}>Days before target</button>
          <button type="button" className="choice" aria-pressed={d.mode === 'date'} onClick={() => set({ ...d, mode: 'date' })}>Date</button>
        </span>{' '}
        {d.mode === 'offset'
          ? <label><span className="sr-only">Business days before the target</span>T−<input className="line narrow mono" inputMode="numeric" value={d.offset} onChange={e => set({ ...d, offset: e.target.value })} placeholder="3" /></label>
          : <label><span className="sr-only">Due on</span><input className="line" type="date" value={d.dueOn} onChange={e => set({ ...d, dueOn: e.target.value })} /></label>}
      </span>
      <label>Required before <select className="line" value={d.requiredBefore} onChange={e => set({ ...d, requiredBefore: e.target.value })}>{requiredChoices.map(r => <option key={r}>{r}</option>)}</select></label>
      <label>Owner <select className="line" value={d.ownerId} onChange={e => set({ ...d, ownerId: e.target.value })}>
        <option value="">Choose…</option>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>
      {positions && positions.length > 1 && (
        <label>Position <select className="line" value={d.position} onChange={e => set({ ...d, position: e.target.value })}>{positions.map(p => <option key={p.value} value={p.value}>{p.label}</option>)}</select></label>)}
    </div>
  )
}

function AddGateButton({ trainId, trainStatus }: { trainId: string; trainStatus?: string }) {
  const [draft, setDraft] = useDraft<GateDraft>(addGateKey(trainId))
  return <button type="button" className="text" aria-expanded={draft !== undefined} onClick={() => setDraft(draft ? undefined : emptyGate(trainStatus))}>Add gate</button>
}

function AddGate({ trainId, trainStatus, gates, onAdded }: { trainId: string; trainStatus?: string; gates: GateRow[]; onAdded: (id: string) => void }) {
  const [draft, setDraft] = useDraft<GateDraft>(addGateKey(trainId))
  const [owners, setOwners] = useState<Owner[]>([])
  const [err, setErr] = useState<string | null>(null)
  const open = draft !== undefined
  useEffect(() => { if (open && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [open, owners.length])
  if (!draft) return null
  // A new gate can go before any gate after the last certified or waived one (the API refuses earlier positions, trg_Gate_CertifyInOrder).
  const seqGates = gates as GateRowSeq[]
  const lastClosed = seqGates.reduce((i, g, k) => g.status === 'Certified' || g.status === 'Waived' ? k : i, -1)
  const positions = [{ value: '', label: gates.length ? `After ${gates[gates.length - 1].name}` : 'First' },
    ...seqGates.slice(lastClosed + 1).filter(g => typeof g.sequenceOrder === 'number').map(g => ({ value: String(g.sequenceOrder), label: `Before ${g.name}` }))]
  const d = draft
  const save = async () => {
    const input = gateInput(d, owners, false)
    if (typeof input === 'string') { setErr(input); return }
    setErr(null)
    try { const g = await addGate(trainId, input); setDraft(undefined); onAdded(g.id) } catch (e) { setErr(errMsg(e)) }
  }
  return (
    <div role="group" aria-label="New gate">
      <GateFields d={d} set={setDraft} owners={owners} positions={positions} trainStatus={trainStatus} />
      <p className="actions-col"><button type="button" className="text" onClick={() => { setErr(null); setDraft(undefined) }}>Cancel</button><button type="button" className="text" onClick={save}>Add gate</button></p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
    </div>
  )
}

/** Inspector part for a gate's definition: "Edit gate" (not once Certified or Waived) and "Remove gate" (Pending only), each inline. Keyed by gate id, so messages reset with the selection. */
function GateDefinitionActions({ gate, onRemoved, onChanged }: { gate: GateDefinition; onRemoved: () => void; onChanged: () => void }) {
  const [draft, setDraft] = useDraft<GateDraft>(`editGate:${gate.id}`)
  const [owners, setOwners] = useState<Owner[]>([])
  const [confirming, setConfirming] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  useEffect(() => { if (draft && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [draft, owners.length])
  if (planLocked(gate.trainStatus)) return <p className="muted">{LOCK_REASON[gate.trainStatus]}</p>
  const closed = gate.status === 'Certified' || gate.status === 'Waived'
  const start = () => { setErr(null); setConflict(null); setConfirming(false)
    setDraft({ name: gate.name, gateClass: gate.class, mode: 'offset', offset: String(gate.offsetDays), dueOn: gate.dueOn, requiredBefore: gate.requiredBeforeStatus, ownerId: gate.ownerUserId ?? gate.ownerTeamId ?? '', position: '' }) }
  const save = async (version = gate.version) => {
    if (!draft) return
    const input = gateInput(draft, owners, true)
    if (typeof input === 'string') { setErr(input); return }
    setErr(null); setConflict(null)
    try { await patchGate(gate.id, input, version); setDraft(undefined); onChanged() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }   // the draft stays
  }
  const remove = async () => {
    setErr(null); setConflict(null)
    try { await removeGate(gate.id, gate.version); setConfirming(false); onRemoved() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); setConfirming(false); onChanged() }
  }
  return (<>
    {draft && <div role="group" aria-label="Edit gate"><GateFields d={draft} set={setDraft} owners={owners} stack trainStatus={gate.trainStatus} /></div>}
    <p className="actions-col">
      {draft
        ? <><button type="button" className="text" onClick={() => setDraft(undefined)}>Cancel</button><button type="button" className="text" onClick={() => save()}>Save gate</button></>
        : <>{!closed && <button type="button" className="text" onClick={start}>Edit gate</button>}
            {gate.status === 'Pending' && !confirming && <button type="button" className="text destructive" onClick={() => { setErr(null); setConfirming(true) }}>Remove gate</button>}</>}
    </p>
    {closed && !draft && <p className="muted">{gate.status === 'Certified' ? 'Reopen the gate to change its definition.' : 'A waived gate is final; add a new gate instead.'}</p>}
    {!closed && gate.status !== 'Pending' && !draft && <p className="muted">Only a pending gate can be removed.</p>}
    {confirming && (
      <div role="group" aria-label="Confirm removal">
        <p className="bad">Remove {gate.name}{gate.tasks.length > 0 ? ` and its ${gate.tasks.length === 1 ? 'task' : `${gate.tasks.length} tasks`}` : ''}? This cannot be undone.</p>
        <p className="actions-col"><button type="button" className="text" onClick={() => setConfirming(false)}>Keep gate</button><button type="button" className="text destructive" onClick={remove}>Confirm remove</button></p>
      </div>)}
    {err && <p className="bad" role="alert">✗ {err}</p>}
    {conflict && (
      <Conflict error={conflict} what="gate">
        {draft && <><button type="button" className="text" onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}</>}
        <button type="button" className="text" onClick={() => { setDraft(undefined); setConflict(null) }}>Use theirs</button>
      </Conflict>)}
  </>)
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
          <thead><tr><th>State</th><th>Task</th><th>Owner</th><th>Product</th><th>Done</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {gate.tasks.map(t => {
              const sel = selection?.kind === 'task' && selection.id === t.id
              return (
                <tr key={t.id} aria-selected={sel} className={sel ? 'selected' : undefined}>
                  <td className={t.done ? 'ok nowrap' : 'nowrap'}>{t.done ? '✓ done' : '○ open'}</td>
                  <td><button type="button" className="text plainlink" onClick={() => onSelect({ kind: 'task', gateId: gate.id, id: t.id })}>{t.description}</button></td>
                  <td>{t.owner ?? <span className="muted">—</span>}</td>
                  <td>{t.product ?? <span className="muted">—</span>}</td>
                  <td className="mono muted nowrap">{t.completedAt ? fmtDayTime(t.completedAt) : '—'}</td>
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
        <dt>Gate</dt><dd>{gate.name}</dd>{t.done && <><dt>Completed</dt><dd>{t.completedBy ?? ''} {t.completedAt ? fmtDayTime(t.completedAt) : ''}</dd></>}</dl>
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
      <dt>Tasks</dt><dd>{gate.tasks.filter(t => t.done).length} of {gate.tasks.length} done</dd>
      {'requiredBeforeStatus' in gate && <><dt>Required before</dt><dd>{(gate as GateDefinition).requiredBeforeStatus}</dd></>}</dl>
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
    {canPlan && 'offsetDays' in gate && <GateDefinitionActions key={gate.id} gate={gate as GateDefinition} onRemoved={() => { onClose(); onChanged() }} onChanged={onChanged} />}
    {trainId && <Evidence trainId={trainId} entityType="Gate" entityId={gate.id} refreshKey={refreshKey} />}
    <WaiversPanel gateId={gate.id} gateStatus={gate.status} gateVersion={gate.version} refreshKey={refreshKey} onChanged={onChanged} />
    {err && <p className="bad" role="alert">✗ {err}</p>}
    {conflict && <Conflict error={conflict} what="gate" />}</>)
}
