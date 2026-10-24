import { Fragment, useEffect, useId, useRef, useState, type Ref } from 'react'
import {
  addCi, closeCondition, getChangeRecord, getCis, getDecisions, getOwners, putChangeRecord, recordDecision, removeCi,
  type AffectedCi, type ChangeRecord, type DecisionView, type NewConditionBody, type Owner,
} from './api'
import { errMsg } from './format'
import { utcToZonedInput, zonedInputToUtc, serverNow } from './time'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import type { ApiError, Condition } from './api'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { announce } from './announce'
import { useFocusWhen } from './focus'
import { Button, DataTable, ErrorState, Notice, Panel, StatusChip, type Column, type Tone, InputZoneNote, Time } from './ui'

const WORD: Record<string, { g: string; cls: string; word: string; tone: Tone }> = {
  Go: { g: '●', cls: 'ok', word: 'Go', tone: 'success' }, GoWithConditions: { g: '◐', cls: 'warn', word: 'Go with conditions', tone: 'warning' }, NoGo: { g: '✗', cls: 'bad', word: 'No-Go', tone: 'danger' },
}

export interface GoNoGoDraft { decision: string; notes: string; newTarget: string; conditions: { text: string; ownerId: string; expires: string }[] }
export const goNoGoKey = (trainId: string) => `gonogo:${trainId}`
export const emptyGoNoGo = (): GoNoGoDraft => ({ decision: 'Go', notes: '', newTarget: '', conditions: [] })

/** A one-of choice as underlined text (docs/UI.md), exposed as a named radio group: arrow keys move, only the chosen one is in the tab order. */
export function RadioChoice<T extends string>({ label, options, value, onChange, groupRef }: { label: string; options: { key: T; word: string }[]; value: T; onChange: (v: T) => void; groupRef?: Ref<HTMLSpanElement> }) {
  const refs = useRef<(HTMLButtonElement | null)[]>([])
  const move = (by: number) => { const n = (options.findIndex(o => o.key === value) + by + options.length) % options.length; onChange(options[n].key); refs.current[n]?.focus() }
  return (
    <span role="radiogroup" aria-label={label} ref={groupRef} onKeyDown={e => {
      if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); move(1) } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); move(-1) }
    }}>
      {options.map((o, i) => <span key={o.key}><button ref={el => { refs.current[i] = el }} type="button" role="radio" aria-checked={value === o.key} tabIndex={value === o.key ? 0 : -1}
        className={value === o.key ? 'text strong' : 'text'} onClick={() => onChange(o.key)}><span aria-hidden="true">{value === o.key ? '● ' : '○ '}</span>{o.word}</button>{' '}</span>)}
    </span>)
}
/** After opening an inline form, move focus to its chosen radio (the form renders on the next frame). */
const focusChecked = (group: { current: HTMLElement | null }) => requestAnimationFrame(() => group.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus())

/** Latest Go/No-Go with its conditions, the history under it, and the inline record form (Release Manager only). Decisions are immutable (D29). */
export function GoNoGo({ trainId, trainVersion, canDecide, refreshKey, onChanged }: { trainId: string; trainVersion: number; canDecide: boolean; refreshKey: number; onChanged: () => void }) {
  const [list, setList] = useState<DecisionView[] | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  const [draft, setDraft] = useDraft<GoNoGoDraft>(goNoGoKey(trainId))
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [history, setHistory] = useState(false)
  const [confirmFor, setConfirmFor] = useState<GoNoGoDraft | null>(null)   // the draft the user is confirming; any edit makes a new draft and drops the confirm
  const { run, pending } = useAction()
  const formId = useId(), historyId = useId()
  const toggle = useRef<HTMLButtonElement>(null)
  const group = useRef<HTMLSpanElement>(null)
  const recordBtn = useRef<HTMLButtonElement>(null)
  const confirming = !!draft && confirmFor === draft
  const confirmBtn = useFocusWhen<HTMLButtonElement>(confirming)
  useEffect(() => { getDecisions(trainId).then(setList).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { getOwners().then(o => setOwners(o.filter(x => x.kind === 'user'))).catch(() => setOwners([])) }, [])
  if (!list) return err ? <ErrorState what="The Go/No-Go decisions" message={err} /> : null
  const name = (id: string) => owners.find(o => o.id === id)?.name ?? '—'
  const latest = list[0]
  const now = serverNow()

  const conds = (d: GoNoGoDraft): NewConditionBody[] => d.conditions.map(c => ({ text: c.text, ownerUserId: c.ownerId || owners[0]?.id || '', expiresAt: c.expires ? zonedInputToUtc(c.expires) : '' }))
  const invalid = (d: GoNoGoDraft) => d.decision === 'GoWithConditions' && conds(d).some(c => !c.expiresAt) ? 'Every condition needs an expiry.' : null
  /** Record decision → validate, then a summary line with Confirm (decisions are immutable). */
  const review = () => {
    if (!draft) return
    setErr(null); setConflict(null)
    const bad = invalid(draft)
    if (bad) setErr(bad); else setConfirmFor(draft)
  }
  const cancelConfirm = () => { setConfirmFor(null); requestAnimationFrame(() => recordBtn.current?.focus()) }
  const save = (version = trainVersion) => run('Recording…', async () => {
    if (!draft) return
    setErr(null); setConflict(null)
    const bad = invalid(draft)
    if (bad) { setErr(bad); setConfirmFor(null); return }
    try {
      await recordDecision(trainId, { decision: draft.decision, notes: draft.notes || null, newTargetReleaseDate: draft.decision === 'NoGo' && draft.newTarget ? draft.newTarget : null, conditions: draft.decision === 'GoWithConditions' ? conds(draft) : [] }, version)
      announce(`${WORD[draft.decision].word} recorded.`)
      setDraft(undefined); setConfirmFor(null); onChanged()
      requestAnimationFrame(() => toggle.current?.focus())
    } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); setConfirmFor(null); onChanged() }
  })
  const close = async (id: string, version: number) => {
    setErr(null)
    try { await closeCondition(id, version); announce('Condition closed.'); onChanged() } catch (e) { setErr(errMsg(e)); onChanged() }
  }
  const open = () => { setConflict(null); setDraft(emptyGoNoGo()); focusChecked(group) }
  const summary = (d: GoNoGoDraft) => d.decision === 'GoWithConditions' ? ` with ${d.conditions.length === 1 ? '1 condition' : `${d.conditions.length} conditions`}`
    : d.decision === 'NoGo' && d.newTarget ? ` with a new target of ${d.newTarget}` : ''
  const setCond = (i: number, patch: Partial<GoNoGoDraft['conditions'][number]>) => draft && setDraft({ ...draft, conditions: draft.conditions.map((c, j) => j === i ? { ...c, ...patch } : c) })

  const condColumns: Column<Condition>[] = [
    { key: 'state', header: 'State', cell: c => { const lapsed = !c.closedAt && new Date(c.expiresAt).getTime() <= now; return <StatusChip tone={c.closedAt ? 'success' : lapsed ? 'danger' : 'neutral'}>{c.closedAt ? 'Closed' : lapsed ? 'Expired' : 'Open'}</StatusChip> } },
    { key: 'text', header: 'Condition', cell: c => c.text },
    { key: 'owner', header: 'Owner', cell: c => name(c.ownerUserId) },
    { key: 'expires', header: 'Expires', cell: c => <span className="mono"><Time at={c.expiresAt} date /></span> },
    { key: 'actions', header: <span className="sr-only">Actions</span>, cell: c => !c.closedAt && <ConfirmInline label="Close" triggerLabel={`Close condition: ${c.text}`} question="Close this condition?" confirmLabel="Close condition" pendingLabel="Closing…" destructive={false} onConfirm={() => close(c.id, c.version)} /> },
  ]
  const row = (v: DecisionView) => {
    const w = WORD[v.decision.decision]
    return (
      <div key={v.decision.id} className="rd-decision">
        <p><StatusChip tone={w.tone}>{w.word}</StatusChip> <span className="mono"><Time at={v.decision.decidedAt} date /></span> · {name(v.decision.decidedByUserId)}
          {v.decision.notes && <span className="rd-na"> — {v.decision.notes}</span>}
          {v.decision.newTargetReleaseDate && <span className="rd-na"> · new target {v.decision.newTargetReleaseDate}</span>}</p>
        {v.conditions.length > 0 && <DataTable caption="Go conditions" columns={condColumns} rows={v.conditions} rowKey={c => c.id} />}
      </div>)
  }

  return (
    <Panel label="Go/No-Go" title="Go/No-Go" subtitle="Decisions are recorded once and cannot be edited afterwards."
      meta={<>
        {list.length > 1 && <Button aria-expanded={history} aria-controls={history ? historyId : undefined} onClick={() => setHistory(!history)}>{history ? 'Hide history' : `History (${list.length})`}</Button>}
        {canDecide && <Button ref={toggle} variant={draft ? 'secondary' : 'primary'} aria-expanded={!!draft} aria-controls={draft ? formId : undefined} disabled={!!pending} onClick={() => draft ? setDraft(undefined) : open()}>{draft ? 'Cancel' : 'Record Go/No-Go'}</Button>}
      </>}>
      {draft && (
        <div className="rd-fields stack" id={formId} role="group" aria-label="Record Go/No-Go">
          <RadioChoice label="Decision" groupRef={group} value={draft.decision as 'Go' | 'GoWithConditions' | 'NoGo'} options={(['Go', 'GoWithConditions', 'NoGo'] as const).map(k => ({ key: k, word: WORD[k].word }))}
            onChange={d => setDraft({ ...draft, decision: d, conditions: d === 'GoWithConditions' && draft.conditions.length === 0 ? [{ text: '', ownerId: '', expires: '' }] : draft.conditions })} />
          <label>Notes <input value={draft.notes} onChange={e => setDraft({ ...draft, notes: e.target.value })} /></label>
          {draft.decision === 'NoGo' && <label>New target date <input type="date" value={draft.newTarget} onChange={e => setDraft({ ...draft, newTarget: e.target.value })} /></label>}
          {draft.decision === 'GoWithConditions' && (
            <>
              {draft.conditions.map((c, i) => (
                <span key={i} className="rd-fields">
                  <label>Condition <input value={c.text} onChange={e => setCond(i, { text: e.target.value })} /></label>
                  <label>Owner <select value={c.ownerId || owners[0]?.id || ''} onChange={e => setCond(i, { ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>
                  <label>Expires <input type="datetime-local" value={c.expires} aria-describedby={`zn-gov-${i}`} onChange={e => setCond(i, { expires: e.target.value })} /></label><InputZoneNote id={`zn-gov-${i}`} value={c.expires} />
                  <Button onClick={() => setDraft({ ...draft, conditions: draft.conditions.filter((_, j) => j !== i) })}>Remove</Button>
                </span>))}
              <span><Button onClick={() => setDraft({ ...draft, conditions: [...draft.conditions, { text: '', ownerId: '', expires: utcToZonedInput(new Date(now + 24 * 3600e3).toISOString()) }] })}>Add condition</Button></span>
            </>)}
          {confirming
            ? <span className="confirm-inline rd-fields" role="group" aria-label="Confirm decision">
                <span>Record <StatusChip tone={WORD[draft.decision].tone}>{WORD[draft.decision].word}</StatusChip>{summary(draft)}? Decisions cannot be edited afterwards.</span>
                <Button ref={confirmBtn} variant="primary" disabled={!!pending} onClick={() => save()}>{pending ?? 'Confirm decision'}</Button>
                <Button disabled={!!pending} onClick={cancelConfirm}>Cancel</Button>
              </span>
            : <span><Button ref={recordBtn} variant="primary" onClick={review}>Record decision</Button> <span className="rd-na">Decisions cannot be edited afterwards.</span></span>}
        </div>)}
      {conflict && <Conflict error={conflict} what="train">{draft && <button type="button" className="text" disabled={!!pending} onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>{pending ?? 'Record anyway'}</button>}</Conflict>}
      {err && <div role="alert"><Notice tone="danger" title="That did not work">{err}</Notice></div>}
      {!latest ? <p className="rd-na">No Go/No-Go recorded yet. Executing needs a Go.</p> : row(latest)}
      {history && <div id={historyId}>{list.slice(1).map(row)}</div>}
    </Panel>
  )
}

const FIELDS: { key: keyof Omit<ChangeRecord, 'releaseTrainId' | 'version' | 'cabDate'>; label: string }[] = [
  { key: 'justification', label: 'Justification' }, { key: 'implementationPlan', label: 'Implementation plan' }, { key: 'riskImpactAnalysis', label: 'Risk and impact' },
  { key: 'backoutPlan', label: 'Back-out plan' }, { key: 'testPlan', label: 'Test plan' }, { key: 'communicationPlan', label: 'Communication plan' },
]
type CrDraft = Record<string, string>

/** The audit field set and the affected configuration items (REOS-33). Edited inline; a draft survives a closed tab. */
export function ChangeRecordPanel({ trainId, canEdit, refreshKey, onChanged }: { trainId: string; canEdit: boolean; refreshKey: number; onChanged: () => void }) {
  const [cr, setCr] = useState<ChangeRecord | null>(null)
  const [cis, setCis] = useState<AffectedCi[]>([])
  const [draft, setDraft] = useDraft<CrDraft>(`changeRecord:${trainId}`)
  const [ci, setCi] = useState({ name: '', ext: '' })
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const { run, pending } = useAction()
  const formId = useId()
  const editBtn = useRef<HTMLButtonElement>(null)
  const firstField = useRef<HTMLTextAreaElement>(null)
  const itemInput = useRef<HTMLInputElement>(null)
  useEffect(() => { getChangeRecord(trainId).then(setCr).catch(e => setErr(errMsg(e))); getCis(trainId).then(setCis).catch(() => setCis([])) }, [trainId, refreshKey])
  if (!cr) return err ? <ErrorState what="The change record" message={err} /> : null
  const edit = () => { setDraft(Object.fromEntries([...FIELDS.map(f => [f.key, cr[f.key] ?? '']), ['cabDate', cr.cabDate ?? '']])); requestAnimationFrame(() => firstField.current?.focus()) }
  const save = (version = cr.version) => run('Saving…', async () => {
    if (!draft) return
    setErr(null); setConflict(null)
    try {
      await putChangeRecord(trainId, { justification: draft.justification, implementationPlan: draft.implementationPlan, riskImpactAnalysis: draft.riskImpactAnalysis, backoutPlan: draft.backoutPlan, testPlan: draft.testPlan, communicationPlan: draft.communicationPlan, cabDate: draft.cabDate || null }, version)
      announce('Change record saved.')
      setDraft(undefined); onChanged()
      requestAnimationFrame(() => editBtn.current?.focus())
    } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  })
  const addOne = () => run('Adding…', async () => {
    setErr(null)
    try { await addCi(trainId, ci.name, ci.ext || null); announce(`${ci.name} added.`); setCi({ name: '', ext: '' }); onChanged(); itemInput.current?.focus() } catch (e) { setErr(errMsg(e)) }
  })
  const remove = async (c: AffectedCi) => {
    setErr(null)
    try { await removeCi(trainId, c.id); announce(`${c.ciName} removed.`); onChanged(); requestAnimationFrame(() => itemInput.current?.focus()) } catch (e) { setErr(errMsg(e)) }
  }
  const empty = FIELDS.every(f => !cr[f.key]) && !cr.cabDate

  const ciColumns: Column<AffectedCi>[] = [
    { key: 'item', header: 'Item', cell: c => c.ciName },
    { key: 'ext', header: 'External id', cell: c => <span className="mono">{c.ciExternalId ?? '—'}</span> },
    { key: 'actions', header: <span className="sr-only">Actions</span>, cell: c => canEdit && <ConfirmInline label="Remove" triggerLabel={`Remove ${c.ciName}`} question={`Remove ${c.ciName}?`} confirmLabel="Remove item" pendingLabel="Removing…" onConfirm={() => remove(c)} /> },
  ]
  return (
    <Panel label="Change record" title="Change record" subtitle={cr.cabDate ? <>CAB <span className="mono">{cr.cabDate}</span></> : undefined}
      meta={canEdit && <Button ref={editBtn} aria-expanded={!!draft} aria-controls={draft ? formId : undefined} disabled={!!pending && !!draft} onClick={() => draft ? setDraft(undefined) : edit()}>{draft ? 'Cancel' : 'Edit'}</Button>}>
      {draft ? (
        <div className="rd-fields stack" id={formId}>
          {FIELDS.map((f, i) => <label key={f.key}>{f.label}<textarea ref={i === 0 ? firstField : undefined} rows={2} value={draft[f.key] ?? ''} onChange={e => setDraft({ ...draft, [f.key]: e.target.value })} /></label>)}
          <label>CAB date <input type="date" value={draft.cabDate ?? ''} onChange={e => setDraft({ ...draft, cabDate: e.target.value })} /></label>
          <span><Button variant="primary" disabled={!!pending} onClick={() => save()}>{pending === 'Saving…' ? pending : 'Save change record'}</Button></span>
          {conflict && <Conflict error={conflict} what="change record"><button type="button" className="text" disabled={!!pending} onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}<button type="button" className="text" onClick={() => { setDraft(undefined); setConflict(null) }}>Use theirs</button></Conflict>}
        </div>
      ) : empty ? <p className="rd-na">Nothing recorded yet.</p> : (
        <dl className="rd-facts">{FIELDS.filter(f => cr[f.key]).map(f => <Fragment key={f.key}><dt>{f.label}</dt><dd>{cr[f.key]}</dd></Fragment>)}</dl>
      )}
      {err && <div role="alert"><Notice tone="danger" title="That did not work">{err}</Notice></div>}
      <h3 className="rd-sub">Affected configuration items</h3>
      {cis.length === 0 ? <p className="rd-na">None listed.</p> : <DataTable caption="Affected configuration items" columns={ciColumns} rows={cis} rowKey={c => c.id} />}
      {canEdit && (
        <div className="rd-fields">
          <label>Item <input ref={itemInput} value={ci.name} onChange={e => setCi({ ...ci, name: e.target.value })} /></label>
          <label>External id <input value={ci.ext} onChange={e => setCi({ ...ci, ext: e.target.value })} /></label>
          <Button disabled={!ci.name.trim() || !!pending} onClick={addOne}>{pending === 'Adding…' ? pending : 'Add item'}</Button>
        </div>)}
    </Panel>
  )
}
