import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react'
import {
  addTask, gateAction, getGate, getOwners, getProducts, getWindow, putWindow, taskAction,
  type GateDetail, type GateRow, type Owner, type ProductsResponse, type WindowRow,
} from './api'
import {
  addGate, addProduct, patchGate, patchProduct, removeGate, removeProduct, GATE_CLASSES, REQUIRED_BEFORE,
  type GateDefinition, type GateInput, type ProductRowV,
} from './api'
import { day, errMsg, plural, tMinus } from './format'
import { Button, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, Notice, Panel, PropertyList, StatusChip, chipTone, type Column, type Tone, InputZoneNote, Time } from './ui'
import { InspectorFrame } from './InspectorFrame'
import { stampOf, stampText } from './deploy/deployLogic'
import { windowStamp } from './release/model'
import { browserZone } from './shell/clock'
import { utcToZonedInput, zonedInputToUtc } from './time'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import type { ApiError, WindowCollision } from './api'
import { collisionText } from './Advisories'
import { StepInspector } from './Runbook'
import { Evidence } from './Evidence'
import { WaiversPanel } from './Freezes'
import { bulkKey, type BulkDraft } from './Bulk'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { announce } from './announce'

export type Selection = { kind: 'gate'; id: string } | { kind: 'task'; gateId: string; id: string } | { kind: 'product'; id: string } | { kind: 'step'; id: string } | null

const STATUS: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'certified' }, Waived: { g: '●', cls: 'warn', word: 'waived' }, Failed: { g: '✗', cls: 'bad', word: 'failed' },
  InProgress: { g: '◐', cls: 'accent', word: 'in progress' }, Pending: { g: '○', cls: 'muted', word: 'pending' },
}

// ---- Deployment window (GET/PUT /trains/{id}/window). Shown as UTC with the person's own time beside it (D44); edited in the display zone, which the fields say. -----------------------------
const localInput = utcToZonedInput
const toUtc = zonedInputToUtc

/** D44: the window in UTC, labelled, with the person's own time beside it. */
function WindowStamp({ w }: { w: WindowRow }) {
  const s = windowStamp(w.startsAt, w.endsAt, browserZone())
  return <><span className="mono">{s.utc}</span>{s.local && <span className="rd-na"> ({s.local} your time)</span>}</>
}

export function WindowLine({ trainId, canEdit, refreshKey, onChanged, collisions }: { trainId: string; canEdit: boolean; refreshKey: number; onChanged: () => void; collisions?: WindowCollision[] }) {
  const [win, setWin] = useState<WindowRow | null | undefined>(undefined)
  const [draft, setDraft] = useDraft<{ start: string; end: string }>(`window:${trainId}`)   // survives a closed tab (session engine)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [bad, setBad] = useState<'start' | 'end' | null>(null)
  const { run, pending } = useAction()
  const startRef = useRef<HTMLInputElement>(null), editBtn = useRef<HTMLButtonElement>(null), errId = useId()
  const editing = draft !== undefined
  const start = draft?.start ?? '', end = draft?.end ?? ''
  useEffect(() => { getWindow(trainId).then(setWin).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  if (win === undefined) return null
  const back = () => requestAnimationFrame(() => editBtn.current?.focus())   // the inline form unmounts: give focus back to Edit window
  const open = () => { setErr(null); setConflict(null); setBad(null); setDraft({ start: win ? localInput(win.startsAt) : '', end: win ? localInput(win.endsAt) : '' }); requestAnimationFrame(() => startRef.current?.focus()) }
  const close = () => { setDraft(undefined); setConflict(null); setErr(null); setBad(null); back() }
  const save = (version = win?.version) => run('Saving…', async () => {
    setErr(null); setConflict(null); setBad(null)
    if (!start || !end) { setBad(!start ? 'start' : 'end'); setErr(!start ? 'Starts: enter a start date and time.' : 'Ends: enter an end date and time.'); return }
    try { await putWindow(trainId, toUtc(start), toUtc(end), version); setDraft(undefined); back(); announce('Window saved.'); onChanged() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }   // a 409 keeps the draft; the user chooses below
  })
  const invalid = (f: 'start' | 'end') => bad === f ? { 'aria-invalid': true, 'aria-describedby': errId } : {}
  if (!editing) return (
    <span>Window {win ? <WindowStamp w={win} /> : <span className="rd-na">not set</span>}
      {canEdit && <> <button ref={editBtn} type="button" className="text" onClick={open}>{win ? 'Edit window' : 'Set window'}</button></>}
      {win && collisions?.map(c => <span key={c.otherTrainId} className="warn"> · <span aria-hidden="true">▲ </span>{collisionText(c)}</span>)}
      {err && <span className="bad" role="alert"> ✗ {err}</span>}</span>
  )
  return (
    <span className="inline-form">
      <label>Starts <input ref={startRef} className="line" type="datetime-local" value={start} aria-describedby={"zn-pl-1"} onChange={e => setDraft({ start: e.target.value, end })} {...invalid('start')} /></label><InputZoneNote id="zn-pl-1" value={start} />{' '}
      <label>Ends <input className="line" type="datetime-local" value={end} aria-describedby={"zn-pl-2"} onChange={e => setDraft({ start, end: e.target.value })} {...invalid('end')} /></label><InputZoneNote id="zn-pl-2" value={end} />{' '}
      <button type="button" className="text" disabled={!!pending} onClick={() => save()}>{pending ?? 'Save window'}</button>{' '}
      <button type="button" className="text" disabled={!!pending} onClick={close}>Cancel</button>
      {err && <span id={errId} className="bad" role="alert"> ✗ {err}</span>}
      {conflict && (
        <Conflict error={conflict} what="window">
          <button type="button" className="text" disabled={!!pending} onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}
          <button type="button" className="text" onClick={close}>Use theirs</button>
        </Conflict>)}
    </span>
  )
}

// ---- Bundled products ---------------------------------------------------------------------------------------------------------------
const HEALTH_TONE: Record<string, Tone> = { Ready: 'success', 'On track': 'success', 'At risk': 'warning' }

// REOS-81: products are added inline under the heading, edited inline in their row, and removed with an inline confirm (no modal).
// The plan is locked once the train is Executing (the API refuses with the reason; the buttons are not offered then).
type ProductDraft = { name: string; versionTag: string; projectCode: string }
const planLocked = (status: string | undefined) => status === 'Executing' || status === 'Complete' || status === 'Aborted'
const LOCK_REASON: Record<string, string> = {
  Executing: 'The train is Executing: products and gates are locked once deployment has started.',
  Complete: 'The train is Complete: its products and gates can no longer be changed.',
  Aborted: 'The train is Aborted: its products and gates can no longer be changed.',
}

export function Products({ trainId, refreshKey, selection, onSelect, canPlan = false, trainStatus, onChanged, manifestVersion }: { trainId: string; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void; canPlan?: boolean; trainStatus?: string; onChanged?: () => void; manifestVersion?: number }) {
  const [data, setData] = useState<ProductsResponse | null>(null)
  const [tick, setTick] = useState(0)
  const [loadErr, setLoadErr] = useState<string | null>(null)
  const [adding, setAdding] = useDraft<ProductDraft>(`addProduct:${trainId}`)   // survives a closed tab (session engine)
  const [editing, setEditing] = useDraft<ProductDraft & { id: string }>(`editProduct:${trainId}`)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [sort, setSort] = useState<'name' | 'version' | 'blockers'>('name')
  useEffect(() => { getProducts(trainId).then(d => { setData(d); setLoadErr(null) }).catch(e => setLoadErr(errMsg(e))) }, [trainId, refreshKey, tick])
  if (!data) return loadErr ? <ErrorState what="The service manifest" message={loadErr} onRetry={() => setTick(n => n + 1)} /> : <LoadingState what="the service manifest" />
  const pct = data.tasksTotal === 0 ? 0 : Math.round(100 * data.tasksDone / data.tasksTotal)
  const locked = trainStatus !== undefined && planLocked(trainStatus)
  const editable = canPlan && !locked
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
  const all = data.products as ProductRowV[]
  // 20 or more services: sortable (name, version, most blockers first) and the table scrolls inside its panel with the headings kept.
  const rows = [...all].sort((x, y) => sort === 'version' ? x.versionTag.localeCompare(y.versionTag, undefined, { numeric: true }) : sort === 'blockers' ? y.openBlockers.length - x.openBlockers.length || x.name.localeCompare(y.name) : x.name.localeCompare(y.name))
  const save = (p: ProductRowV, version = p.version) => {
    if (!editing) return
    void run(() => patchProduct(p.id, { name: editing.name, versionTag: editing.versionTag, projectCode: editing.projectCode }, version), () => setEditing(undefined))
  }
  const remove = (p: ProductRowV) => run(() => removeProduct(p.id, p.version), () => { setConfirming(null); if (selection?.kind === 'product' && selection.id === p.id) onSelect(null) })
  const editRow = rows.find(p => p.id === editing?.id)
  const columns: Column<ProductRowV>[] = [
    { key: 'name', header: 'Service', cell: p => editable && editing?.id === p.id
      ? <input className="rd-input" aria-label="Product name" value={editing.name} onChange={e => setEditing({ ...editing, name: e.target.value })} />
      : <button type="button" className="os-link os-link-button" aria-current={selection?.kind === 'product' && selection.id === p.id ? 'true' : undefined} onClick={() => onSelect({ kind: 'product', id: p.id })}>{p.name}</button> },
    { key: 'version', header: 'Version', cell: p => editable && editing?.id === p.id
      ? <input className="rd-input" aria-label="Version tag" value={editing.versionTag} onChange={e => setEditing({ ...editing, versionTag: e.target.value })} /> : <span className="mono">{p.versionTag}</span> },
    { key: 'project', header: 'Project', cell: p => editable && editing?.id === p.id
      ? <input className="rd-input" aria-label="Project code" value={editing.projectCode} onChange={e => setEditing({ ...editing, projectCode: e.target.value })} /> : <span className="mono">{p.projectCode || '—'}</span> },
    { key: 'team', header: 'Team', cell: () => <span className="rd-na">Not recorded</span> },
    { key: 'checks', header: 'Checks', cell: p => <span className="mono">{p.tasksDone} / {p.tasksTotal}</span> },
    { key: 'blockers', header: 'Blockers', cell: p => p.openBlockers.length === 0 ? <span className="rd-na">—</span> : <span>{p.openBlockers.map((sev, i) => <StatusChip key={i} tone={sev === 'Critical' || sev === 'High' ? 'danger' : 'warning'}>{sev}</StatusChip>)}</span> },
    { key: 'health', header: 'State', cell: p => <StatusChip tone={HEALTH_TONE[p.health] ?? 'neutral'}>{p.health}</StatusChip> },
    ...(editable ? [{ key: 'actions', header: <span className="sr-only">Actions</span>, cell: (p: ProductRowV) => editing?.id === p.id
      ? <span className="rd-row-actions"><Button onClick={() => setEditing(undefined)}>Cancel</Button><Button variant="primary" onClick={() => save(p)}>Save</Button></span>
      : confirming === p.id
        ? <span role="group" aria-label={`Confirm removing ${p.name}`} className="rd-row-actions"><span className="rd-error">Remove?</span><Button onClick={() => setConfirming(null)}>Keep</Button><Button variant="destructive" onClick={() => remove(p)}>Confirm remove</Button></span>
        : <span className="rd-row-actions"><Button aria-label={`Edit ${p.name}`} onClick={() => { setErr(null); setConflict(null); setEditing({ id: p.id, name: p.name, versionTag: p.versionTag, projectCode: p.projectCode }) }}>Edit</Button>
            <Button variant="destructive" aria-label={`Remove ${p.name}`} onClick={() => { setErr(null); setConflict(null); setConfirming(p.id) }}>Remove</Button></span> }] : []),
  ]
  return (
    <Panel label="Bundled products" title="Service manifest" subtitle={`${plural(all.length, 'service')} bundled in this release`}
      meta={<>
        {manifestVersion !== undefined && <span className="mono">Manifest v{manifestVersion}</span>}
        <StatusChip tone={locked ? 'warning' : 'success'}>{locked ? 'Locked' : 'Open'}</StatusChip>
        <span><span className="mono">{data.tasksDone} / {data.tasksTotal}</span> checks done · <span className="mono">{pct}%</span></span>
        {rows.length > 1 && <FilterSelect label="Sort services by" value={sort} onChange={v => setSort(v as typeof sort)} options={[{ value: 'name', label: 'Name' }, { value: 'version', label: 'Version' }, { value: 'blockers', label: 'Most blockers' }]} />}
        {editable && <Button variant="primary" aria-expanded={adding !== undefined} onClick={() => { setErr(null); setAdding(adding ? undefined : { name: '', versionTag: '', projectCode: '' }) }}>Add product</Button>}
      </>}>
      {editable && adding && (
        <div className="inline-form rd-fields" role="group" aria-label="New product">
          <label>Product <input value={adding.name} onChange={e => setAdding({ ...adding, name: e.target.value })} placeholder="Payments API" /></label>
          <label>Version <input value={adding.versionTag} onChange={e => setAdding({ ...adding, versionTag: e.target.value })} placeholder="4.2.0" /></label>
          <label>Project <input value={adding.projectCode} onChange={e => setAdding({ ...adding, projectCode: e.target.value })} placeholder="optional" /></label>
          <Button onClick={() => setAdding(undefined)}>Cancel</Button>
          <Button variant="primary" onClick={add}>Add product</Button>
        </div>)}
      {locked && <Notice tone="info" title="The manifest is locked">{LOCK_REASON[trainStatus!]}</Notice>}
      {err && <div role="alert"><Notice tone="danger" title="That did not work">{err}</Notice></div>}
      {conflict && (
        <Conflict error={conflict} what="product">
          {editRow && editing && <><button type="button" className="text" onClick={() => save(editRow, (conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}</>}
          <button type="button" className="text" onClick={() => { setEditing(undefined); setConfirming(null); setConflict(null) }}>Use theirs</button>
        </Conflict>)}
      <div className="rd-scroll"><DataTable caption="Service manifest" columns={columns} rows={rows} rowKey={p => p.id} selectedKey={selection?.kind === 'product' ? selection.id : null} empty="No products bundled yet." /></div>
    </Panel>
  )
}

// ---- Gate timeline: x is in business days (T−n), so a gap of one business day is one step wherever weekends fall ------------------------
/** A milestone marker on the timeline (Q-0843): state is the word and colour class from Milestones.milestoneState. */
export interface TimelineMilestone { id: string; name: string; dueOn: string; tMinus: number; state: { cls: string; word: string } }
export function Timeline({ gates, todayT, targetDate, selectedId, onSelect, trainId, trainStatus, canPlan = false, onChanged, milestones = [] }: { gates: GateRow[]; todayT: number; targetDate: string; selectedId: string | null; onSelect: (id: string) => void; trainId?: string; trainStatus?: string; canPlan?: boolean; onChanged?: () => void; milestones?: TimelineMilestone[] }) {
  const W = 730, L = 50, R = 680
  // The axis spans the first gate (or milestone) to the target (or to today, or a milestone, after it); "now" is pinned to the left edge when it is earlier than the first gate.
  const max = Math.max(1, ...gates.map(g => g.tMinus), ...milestones.map(m => m.tMinus))
  const min = Math.min(0, todayT, ...milestones.map(m => m.tMinus))
  const x = (t: number) => L + ((max - Math.min(t, max)) / (max - min)) * (R - L)
  const nowX = x(todayT)
  const nowPinned = todayT > max
  const first = (name: string | null) => name ? name.split(' ')[0] : ''
  const stateText = (g: GateRow) => g.status === 'Certified' ? `✓ ${g.certifiedOn ? day(g.certifiedOn) : ''}${g.certifiedBy ? ' · ' + first(g.certifiedBy) : ''}`
    : `${(STATUS[g.status] ?? STATUS.Pending).word}${g.ownerName ? ' · ' + first(g.ownerName) : ''}`
  // Milestones (Q-0843) sit in their own lane under the gates: a ◆ on a hairline from the axis, then "name · date" and the state word, alternating two rows.
  const H = milestones.length > 0 ? 156 : 104
  const targetX = x(0)
  return (
    <Panel label="Gate timeline" title={`Gates${milestones.length > 0 ? ' and milestones' : ''} · business days to target`} subtitle="Click a gate to open its checklist."
      meta={canPlan && trainId && !planLocked(trainStatus) ? <AddGateButton trainId={trainId} trainStatus={trainStatus} /> : undefined}>
      {canPlan && trainId && !planLocked(trainStatus) && <AddGate trainId={trainId} trainStatus={trainStatus} gates={gates} onAdded={id => { onChanged?.(); onSelect(id) }} />}
      <svg className="timeline" width="100%" viewBox={`0 0 ${W} ${H}`} role="group"
           aria-label={`Gate timeline to ${day(targetDate)}: ` + gates.map(g => `${g.name} ${STATUS[g.status]?.word ?? g.status}`).join(', ')
             + (milestones.length > 0 ? '; milestones: ' + milestones.map(m => `${m.name} ${day(m.dueOn)} ${m.state.word.replace('✓ ', '')}`).join(', ') : '')}>
        <line className="tl-axis" x1={L} x2={R} y1="52" y2="52" />
        <line className="tl-now" x1={nowX} x2={nowX} y1="16" y2="62" strokeDasharray="3 3" />
        <text className="tl-now-label" x={nowX + 4} y="11">now{nowPinned ? ` · ${tMinus(todayT)}` : ''}</text>
        {gates.map((g, i) => {
          const gx = x(g.tMinus), st = STATUS[g.status] ?? STATUS.Pending
          const anchor = gx < L + 20 ? 'start' : gx > R - 20 ? 'end' : 'middle'
          const nameY = i % 2 === 0 ? 30 : 16
          return (
            <g key={g.id} className={`tl-gate ${selectedId === g.id ? 'sel' : ''}`} tabIndex={0} role="button" aria-current={selectedId === g.id ? 'true' : undefined} aria-label={`${g.name}, ${st.word}, due ${day(g.dueOn)}`}
               onClick={() => onSelect(g.id)} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(g.id) } }}>
              <rect className="tl-hit" x={anchor === 'start' ? gx - 12 : anchor === 'end' ? gx - 108 : gx - 60} y={4} width={120} height={96} />
              <text className="tl-name" x={gx} y={nameY} textAnchor={anchor}>{g.name}</text>
              <circle className={`tl-dot ${st.cls}`} cx={gx} cy="52" r="6" />
              <text className="tl-day mono" x={gx} y="74" textAnchor={anchor}>{day(g.dueOn)} · {tMinus(g.tMinus)}</text>
              <text className={`tl-state ${st.cls}`} x={gx} y="90" textAnchor={anchor}>{stateText(g)}</text>
            </g>
          )
        })}
        {milestones.map((m, i) => {
          const mx = x(m.tMinus)
          const anchor = mx < L + 20 ? 'start' : mx > R - 20 ? 'end' : 'middle'
          const labelY = i % 2 === 0 ? 132 : 148
          return (
            <g key={m.id} className="tl-ms" role="img" aria-label={`Milestone ${m.name}, ${day(m.dueOn)}, ${m.state.word.replace('✓ ', '')}`} data-testid="timeline-milestone">
              <title>{`${m.name} · ${day(m.dueOn)} · ${m.state.word}`}</title>
              <line className="tl-ms-tick" x1={mx} x2={mx} y1="58" y2={labelY - 26} strokeDasharray="1 2" />
              <text className={`tl-ms-glyph ${m.state.cls}`} x={mx} y={labelY - 14} textAnchor="middle" aria-hidden="true">◆</text>
              <text className="tl-ms-name" x={mx} y={labelY} textAnchor={anchor} aria-hidden="true">{m.name} · <tspan className="tl-day mono">{day(m.dueOn)}</tspan> <tspan className={m.state.cls}>{m.state.word}</tspan></text>
            </g>
          )
        })}
        <g><path className="tl-target" d={`M${targetX} 45 L${targetX + 7} 52 L${targetX} 59 L${targetX - 7} 52 Z`} />
          <text className="tl-name" x={targetX + 8} y="30" textAnchor="end">Target</text>
          <text className="tl-day mono" x={targetX + 8} y="74" textAnchor="end">{day(targetDate)} · T0</text></g>
      </svg>
    </Panel>
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
  const [bad, setBad] = useState<'text' | 'owner' | null>(null)
  const { run, pending } = useAction()
  const taskRef = useRef<HTMLInputElement>(null), addBtn = useRef<HTMLButtonElement>(null), errId = useId()
  useEffect(() => { getGate(gateId).then(setGate).catch(e => setErr(errMsg(e))) }, [gateId, refreshKey])
  useEffect(() => { if (adding && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [adding, owners.length])
  if (!gate) return err ? <p className="bad" role="alert">✗ {err}</p> : null
  const done = gate.tasks.filter(t => t.done).length
  const toggle = (t: GateDetail['tasks'][number]) => run(t.id, async () => {
    setErr(null); setConflict(null)
    try { await taskAction(t.id, t.done ? 'reopen' : 'complete', t.version); announce(t.done ? `Reopened: ${t.description}.` : `Done: ${t.description}.`); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  })
  // The form stays open after Add, with focus back in Task, so a run of tasks is type, Enter, type, Enter (UX review 10).
  const add = (e: FormEvent) => { e.preventDefault(); void run('add', async () => {
    const owner = owners.find(o => o.id === ownerId), desc = text.trim()
    if (!desc) { setBad('text'); setErr('Task: describe what has to happen.'); taskRef.current?.focus(); return }
    if (!owner) { setBad('owner'); setErr('Owner: choose who owns this task.'); return }
    setErr(null); setBad(null)
    try { await addTask(gate.id, desc, owner); setDraft({ text: '', ownerId: owner.id }); announce(`Task added: ${desc}.`); onChanged(); taskRef.current?.focus() } catch (e) { setErr(errMsg(e)) }
  }) }
  const toggleForm = () => {
    setErr(null); setBad(null)
    if (adding) { setDraft(undefined); return }
    setDraft({ text: '', ownerId: '' }); requestAnimationFrame(() => taskRef.current?.focus())
  }
  const cancel = () => { setDraft(undefined); setErr(null); setBad(null); requestAnimationFrame(() => addBtn.current?.focus()) }
  const invalid = (f: 'text' | 'owner') => bad === f ? { 'aria-invalid': true, 'aria-describedby': errId } : {}
  return (
    <Panel label="Checklist" title={`${gate.name} · checklist`}
      meta={<><span><span className="mono">{done} / {gate.tasks.length}</span> done</span><Button onClick={openBulk}>Paste tasks</Button><Button ref={addBtn} aria-expanded={adding} onClick={toggleForm}>Add task</Button></>}>
      {adding && (
        <form onSubmit={add}><p className="inline-form">
          <label>Task <input ref={taskRef} className="line wide" value={text} onChange={e => setDraft({ text: e.target.value, ownerId })} placeholder="What has to happen" {...invalid('text')} /></label>{' '}
          <label>Owner <select className="line" value={ownerId} onChange={e => setDraft({ text, ownerId: e.target.value })} {...invalid('owner')}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>{' '}
          <button type="submit" className="text" disabled={!!pending}>{pending === 'add' ? 'Adding…' : 'Add'}</button> <button type="button" className="text" onClick={cancel}>Cancel</button>
        </p></form>)}
      {err && <p id={errId} className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="task" />}
      {gate.tasks.length === 0 ? <p className="muted">No tasks on this gate.</p> : (
        <table className="grid" aria-label={`${gate.name} checklist`}>
          <thead><tr><th>State</th><th>Task</th><th>Owner</th><th>Product</th><th>Done</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {gate.tasks.map(t => {
              const sel = selection?.kind === 'task' && selection.id === t.id
              return (
                <tr key={t.id} className={sel ? 'selected' : undefined}>
                  <td className={t.done ? 'ok nowrap' : 'nowrap'}>{t.done ? '✓ done' : '○ open'}</td>
                  <td><button type="button" className="text plainlink" aria-current={sel ? 'true' : undefined} onClick={() => onSelect({ kind: 'task', gateId: gate.id, id: t.id })}>{t.description}</button></td>
                  <td>{t.owner ?? <span className="muted">—</span>}</td>
                  <td>{t.product ?? <span className="muted">—</span>}</td>
                  <td className="mono muted nowrap">{t.completedAt ? <Time at={t.completedAt} date /> : '—'}</td>
                  <td><button type="button" className="text" disabled={!!pending} onClick={() => void toggle(t)}>{pending === t.id ? (t.done ? 'Reopening…' : 'Marking done…') : t.done ? 'Reopen' : 'Mark done'}</button></td>
                </tr>
              )
            })}
          </tbody>
        </table>)}
    </Panel>
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
  useEffect(() => { if (selection?.kind === 'product' && trainId) getProducts(trainId).then(setProducts).catch(e => { setProducts(null); setErr(errMsg(e)) }) }, [selection, trainId, refreshKey])
  const { run, pending } = useAction()

  if (!selection) return <EmptyState title="Nothing selected">Select a train, gate or task to see its detail here.</EmptyState>
  const focusKey = `${selection.kind}:${selection.id}`
  // One action at a time: the trigger is disabled and says what is happening, so a double click cannot send a stale version (UX review 3, 8).
  const act = (label: string, fn: () => Promise<unknown>, said?: string) => run(label, async () => {
    setErr(null); setConflict(null)
    try { await fn(); if (said) announce(said); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  })
  const busy = (label: string, idle: string) => pending === label ? label : idle
  const frame = (title: string, subtitle: string, body: ReactNode, loading = false) => <InspectorFrame focusKey={focusKey} title={title} subtitle={subtitle} loading={loading} onClose={onClose}>{body}</InspectorFrame>
  const waiting = (kind: string) => frame(kind, kind, err ? <div role="alert"><ErrorState what={`The ${kind.toLowerCase()}`} message={err} /></div> : <LoadingState what={`the ${kind.toLowerCase()}`} />, true)

  if (selection.kind === 'step') return trainId ? <StepInspector stepId={selection.id} trainId={trainId} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} onClose={onClose} /> : waiting('Step')
  if (selection.kind === 'product') {
    const p = products?.products.find(x => x.id === selection.id)
    if (!p) return waiting('Product')
    return frame(p.name, 'Product', <>
      <PropertyList items={[
        { label: 'Version', value: p.versionTag, mono: true }, { label: 'Project', value: p.projectCode, mono: true },
        { label: 'Tasks', value: `${p.tasksDone} of ${p.tasksTotal} done` }, { label: 'Health', value: <StatusChip tone={HEALTH_TONE[p.health] ?? 'neutral'}>{p.health}</StatusChip> },
        { label: 'Blockers', value: p.openBlockers.length === 0 ? 'None open' : <span className="ins-blockers">{p.openBlockers.map((b, i) => <StatusChip key={i} tone={b === 'Critical' || b === 'High' ? 'danger' : 'warning'}>{b}</StatusChip>)}</span> },
      ]} /></>)
  }
  if (!gate) return waiting(selection.kind === 'task' ? 'Task' : 'Gate')
  const st = STATUS[gate.status] ?? STATUS.Pending

  if (selection.kind === 'task') {
    const t = gate.tasks.find(x => x.id === selection.id)
    if (!t) return frame('Task', 'Task', <EmptyState title="This task no longer exists" />)
    return frame(t.description, 'Task', <>
      <PropertyList items={[
        { label: 'State', value: <StatusChip tone={t.done ? 'success' : 'neutral'}>{t.done ? 'Done' : 'Open'}</StatusChip> }, { label: 'Owner', value: t.owner ?? '—' }, { label: 'Product', value: t.product ?? '—' },
        { label: 'Gate', value: gate.name },
        ...(t.done ? [{ label: 'Completed', value: `${t.completedBy ?? ''} ${t.completedAt ? stampText(stampOf(t.completedAt, browserZone())) : ''}`.trim() }] : []),
      ]} />
      <div className="ins-actions"><Button disabled={!!pending} onClick={() => void act(t.done ? 'Reopening…' : 'Marking done…', () => taskAction(t.id, t.done ? 'reopen' : 'complete', t.version), t.done ? 'Task reopened.' : 'Task marked done.')}>{pending ?? (t.done ? 'Reopen task' : 'Mark done')}</Button></div>
      {err && <Notice tone="danger" title="That did not work" live>{err}</Notice>}
      {conflict && <Conflict error={conflict} what="task" />}</>)
  }

  const canStart = gate.status === 'Pending', canFail = gate.status === 'InProgress', canReopen = gate.status === 'Failed' || gate.status === 'Certified'
  const certifiable = gate.status === 'InProgress' || gate.status === 'Failed'
  return frame(gate.name, 'Gate', <>
    <PropertyList items={[
      { label: 'Status', value: <StatusChip tone={chipTone(st.word)}>{st.word[0].toUpperCase() + st.word.slice(1)}</StatusChip> }, { label: 'Class', value: gate.class },
      { label: 'Due', value: <>{day(gate.dueOn)} · <span className="mono">{tMinus(gate.tMinus)}</span></> }, { label: 'Owner', value: gate.ownerName ?? '—' },
      { label: 'Tasks', value: `${gate.tasks.filter(t => t.done).length} of ${gate.tasks.length} done` },
      ...('requiredBeforeStatus' in gate ? [{ label: 'Required before', value: (gate as GateDefinition).requiredBeforeStatus }] : []),
    ]} />
    <div className="ins-actions">
      {canStart && <Button variant="primary" disabled={!!pending} onClick={() => void act('Starting…', () => gateAction(gate.id, 'start', gate.version), 'Gate started.')}>{busy('Starting…', 'Start gate')}</Button>}
      {certifiable && <Button variant="primary" disabled={!gate.certify.eligible || !!pending} onClick={() => void act('Certifying…', () => gateAction(gate.id, 'certify', gate.version), 'Gate certified.')}>{busy('Certifying…', 'Certify gate')}</Button>}
      {canFail && <ConfirmInline label="Fail gate" question="Fail this gate?" confirmLabel="Fail gate" pendingLabel="Failing…" disabled={!!pending}
        onConfirm={() => act('Failing…', () => gateAction(gate.id, 'fail', gate.version), 'Gate failed.')} />}
      {canReopen && (gate.status === 'Certified'
        ? <ConfirmInline label="Decertify (reopen)" question="Remove this gate's certification and reopen it?" confirmLabel="Decertify" pendingLabel="Decertifying…" disabled={!!pending}
            onConfirm={() => act('Decertifying…', () => gateAction(gate.id, 'reopen', gate.version), 'Gate decertified and reopened.')} />
        : <Button disabled={!!pending} onClick={() => void act('Reopening…', () => gateAction(gate.id, 'reopen', gate.version), 'Gate reopened.')}>{busy('Reopening…', 'Reopen gate')}</Button>)}
    </div>
    {certifiable && (gate.certify.eligible
      ? <Notice tone="success" title="You can certify this gate now" />
      : <Notice tone="warning" title="Certify unavailable"><ul className="plain" aria-label="Why Certify is unavailable">{gate.certify.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul></Notice>)}
    {gate.status === 'Pending' && <p className="os-muted">Start the gate first, then work its checklist.</p>}
    {canPlan && 'offsetDays' in gate && <GateDefinitionActions key={gate.id} gate={gate as GateDefinition} onRemoved={() => { onClose(); onChanged() }} onChanged={onChanged} />}
    {trainId && <Evidence trainId={trainId} entityType="Gate" entityId={gate.id} refreshKey={refreshKey} />}
    <WaiversPanel gateId={gate.id} gateStatus={gate.status} gateVersion={gate.version} refreshKey={refreshKey} onChanged={onChanged} />
    {err && <Notice tone="danger" title="That did not work" live>{err}</Notice>}
    {conflict && <Conflict error={conflict} what="gate" />}</>)
}
