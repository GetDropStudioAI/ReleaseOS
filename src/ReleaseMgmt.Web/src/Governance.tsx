import { Fragment, useEffect, useState } from 'react'
import {
  addCi, closeCondition, getChangeRecord, getCis, getDecisions, getOwners, putChangeRecord, recordDecision, removeCi,
  type AffectedCi, type ChangeRecord, type DecisionView, type NewConditionBody, type Owner,
} from './api'
import { errMsg } from './format'
import { fmtDayTime, utcToZonedInput, zonedInputToUtc, serverNow } from './time'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import type { ApiError } from './api'

const WORD: Record<string, { g: string; cls: string; word: string }> = {
  Go: { g: '●', cls: 'ok', word: 'Go' }, GoWithConditions: { g: '◐', cls: 'warn', word: 'Go with conditions' }, NoGo: { g: '✗', cls: 'bad', word: 'No-Go' },
}

export interface GoNoGoDraft { decision: string; notes: string; newTarget: string; conditions: { text: string; ownerId: string; expires: string }[] }
export const goNoGoKey = (trainId: string) => `gonogo:${trainId}`
export const emptyGoNoGo = (): GoNoGoDraft => ({ decision: 'Go', notes: '', newTarget: '', conditions: [] })

/** Latest Go/No-Go with its conditions, the history under it, and the inline record form (Release Manager only). Decisions are immutable (D29). */
export function GoNoGo({ trainId, trainVersion, canDecide, refreshKey, onChanged }: { trainId: string; trainVersion: number; canDecide: boolean; refreshKey: number; onChanged: () => void }) {
  const [list, setList] = useState<DecisionView[] | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  const [draft, setDraft] = useDraft<GoNoGoDraft>(goNoGoKey(trainId))
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [history, setHistory] = useState(false)
  useEffect(() => { getDecisions(trainId).then(setList).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { getOwners().then(o => setOwners(o.filter(x => x.kind === 'user'))).catch(() => setOwners([])) }, [])
  if (!list) return err ? <p className="bad" role="alert">✗ {err}</p> : null
  const name = (id: string) => owners.find(o => o.id === id)?.name ?? '—'
  const latest = list[0]
  const now = serverNow()

  const save = async (version = trainVersion) => {
    if (!draft) return
    setErr(null); setConflict(null)
    const conds: NewConditionBody[] = draft.conditions.map(c => ({ text: c.text, ownerUserId: c.ownerId || owners[0]?.id || '', expiresAt: c.expires ? zonedInputToUtc(c.expires) : '' }))
    if (draft.decision === 'GoWithConditions' && conds.some(c => !c.expiresAt)) { setErr('Every condition needs an expiry.'); return }
    try {
      await recordDecision(trainId, { decision: draft.decision, notes: draft.notes || null, newTargetReleaseDate: draft.decision === 'NoGo' && draft.newTarget ? draft.newTarget : null, conditions: draft.decision === 'GoWithConditions' ? conds : [] }, version)
      setDraft(undefined); onChanged()
    } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const close = async (id: string, version: number) => {
    setErr(null)
    try { await closeCondition(id, version); onChanged() } catch (e) { setErr(errMsg(e)); onChanged() }
  }
  const setCond = (i: number, patch: Partial<GoNoGoDraft['conditions'][number]>) => draft && setDraft({ ...draft, conditions: draft.conditions.map((c, j) => j === i ? { ...c, ...patch } : c) })

  const row = (v: DecisionView) => {
    const w = WORD[v.decision.decision]
    return (
      <div key={v.decision.id}>
        <p><span className={w.cls}>{w.g} {w.word}</span> · <span className="mono">{fmtDayTime(v.decision.decidedAt)}</span> · {name(v.decision.decidedByUserId)}
          {v.decision.notes && <span className="muted"> — {v.decision.notes}</span>}
          {v.decision.newTargetReleaseDate && <span className="muted"> · new target {v.decision.newTargetReleaseDate}</span>}</p>
        {v.conditions.length > 0 && (
          <table className="grid">
            <thead><tr><th>State</th><th>Condition</th><th>Owner</th><th>Expires</th><th><span className="sr-only">Actions</span></th></tr></thead>
            <tbody>
              {v.conditions.map(c => {
                const lapsed = !c.closedAt && new Date(c.expiresAt).getTime() <= now
                return (
                  <tr key={c.id}>
                    <td className={c.closedAt ? 'ok nowrap' : lapsed ? 'bad nowrap' : 'nowrap'}>{c.closedAt ? '✓ closed' : lapsed ? '✗ expired' : '○ open'}</td>
                    <td>{c.text}</td><td>{name(c.ownerUserId)}</td><td className="mono nowrap">{fmtDayTime(c.expiresAt)}</td>
                    <td>{!c.closedAt && <button type="button" className="text" onClick={() => close(c.id, c.version)}>Close</button>}</td>
                  </tr>)
              })}
            </tbody>
          </table>)}
      </div>)
  }

  return (
    <section aria-label="Go/No-Go">
      <div className="section-head"><h2 className="cap dark">Go/No-Go</h2>
        <span className="muted">{list.length > 1 && <button type="button" className="text" onClick={() => setHistory(!history)}>{history ? 'Hide history' : `History (${list.length})`}</button>}
          {canDecide && <> <button type="button" className="text" onClick={() => setDraft(draft ? undefined : emptyGoNoGo())}>{draft ? 'Cancel' : 'Record Go/No-Go'}</button></>}</span></div>
      {draft && (
        <div className="inline-form stack">
          <span>{(['Go', 'GoWithConditions', 'NoGo'] as const).map(d => <span key={d}><button type="button" className={draft.decision === d ? 'text strong' : 'text'} aria-pressed={draft.decision === d}
            onClick={() => setDraft({ ...draft, decision: d, conditions: d === 'GoWithConditions' && draft.conditions.length === 0 ? [{ text: '', ownerId: '', expires: '' }] : draft.conditions })}>{draft.decision === d ? '● ' : '○ '}{WORD[d].word}</button>{' '}</span>)}</span>
          <label>Notes <input className="line wide" value={draft.notes} onChange={e => setDraft({ ...draft, notes: e.target.value })} /></label>
          {draft.decision === 'NoGo' && <label>New target date <input className="line" type="date" value={draft.newTarget} onChange={e => setDraft({ ...draft, newTarget: e.target.value })} /></label>}
          {draft.decision === 'GoWithConditions' && (
            <>
              {draft.conditions.map((c, i) => (
                <span key={i} className="inline-form">
                  <label>Condition <input className="line wide" value={c.text} onChange={e => setCond(i, { text: e.target.value })} /></label>
                  <label>Owner <select className="line" value={c.ownerId || owners[0]?.id || ''} onChange={e => setCond(i, { ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>
                  <label>Expires <input className="line" type="datetime-local" value={c.expires} onChange={e => setCond(i, { expires: e.target.value })} /></label>
                  <button type="button" className="text" onClick={() => setDraft({ ...draft, conditions: draft.conditions.filter((_, j) => j !== i) })}>Remove</button>
                </span>))}
              <span><button type="button" className="text" onClick={() => setDraft({ ...draft, conditions: [...draft.conditions, { text: '', ownerId: '', expires: utcToZonedInput(new Date(now + 24 * 3600e3).toISOString()) }] })}>Add condition</button></span>
            </>)}
          <span><button type="button" className="text" onClick={() => save()}>Record decision</button> <span className="muted">Decisions cannot be edited afterwards.</span></span>
          {conflict && <Conflict error={conflict} what="train"><button type="button" className="text" onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Record anyway</button></Conflict>}
        </div>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {!latest ? <p className="muted">No Go/No-Go recorded yet. Executing needs a Go.</p> : row(latest)}
      {history && list.slice(1).map(row)}
    </section>
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
  useEffect(() => { getChangeRecord(trainId).then(setCr).catch(e => setErr(errMsg(e))); getCis(trainId).then(setCis).catch(() => setCis([])) }, [trainId, refreshKey])
  if (!cr) return err ? <p className="bad" role="alert">✗ {err}</p> : null
  const edit = () => setDraft(Object.fromEntries([...FIELDS.map(f => [f.key, cr[f.key] ?? '']), ['cabDate', cr.cabDate ?? '']]))
  const save = async (version = cr.version) => {
    if (!draft) return
    setErr(null); setConflict(null)
    try {
      await putChangeRecord(trainId, { justification: draft.justification, implementationPlan: draft.implementationPlan, riskImpactAnalysis: draft.riskImpactAnalysis, backoutPlan: draft.backoutPlan, testPlan: draft.testPlan, communicationPlan: draft.communicationPlan, cabDate: draft.cabDate || null }, version)
      setDraft(undefined); onChanged()
    } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const addOne = async () => {
    setErr(null)
    try { await addCi(trainId, ci.name, ci.ext || null); setCi({ name: '', ext: '' }); onChanged() } catch (e) { setErr(errMsg(e)) }
  }
  const remove = async (id: string) => { setErr(null); try { await removeCi(trainId, id); onChanged() } catch (e) { setErr(errMsg(e)) } }
  const empty = FIELDS.every(f => !cr[f.key]) && !cr.cabDate

  return (
    <section aria-label="Change record">
      <div className="section-head"><h2 className="cap dark">Change record</h2>
        <span className="muted">{cr.cabDate && <>CAB <span className="mono">{cr.cabDate}</span> · </>}{canEdit && <button type="button" className="text" onClick={() => draft ? setDraft(undefined) : edit()}>{draft ? 'Cancel' : 'Edit'}</button>}</span></div>
      {draft ? (
        <div className="inline-form stack">
          {FIELDS.map(f => <label key={f.key}>{f.label}<textarea className="line block" rows={2} value={draft[f.key] ?? ''} onChange={e => setDraft({ ...draft, [f.key]: e.target.value })} /></label>)}
          <label>CAB date <input className="line" type="date" value={draft.cabDate ?? ''} onChange={e => setDraft({ ...draft, cabDate: e.target.value })} /></label>
          <span><button type="button" className="text" onClick={() => save()}>Save change record</button></span>
          {conflict && <Conflict error={conflict} what="change record"><button type="button" className="text" onClick={() => save((conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}<button type="button" className="text" onClick={() => { setDraft(undefined); setConflict(null) }}>Use theirs</button></Conflict>}
        </div>
      ) : empty ? <p className="muted">Nothing recorded yet.</p> : (
        <dl className="facts">{FIELDS.filter(f => cr[f.key]).map(f => <Fragment key={f.key}><dt>{f.label}</dt><dd>{cr[f.key]}</dd></Fragment>)}</dl>
      )}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      <h3 className="cap">Affected configuration items</h3>
      {cis.length === 0 ? <p className="muted">None listed.</p> : (
        <table className="grid"><thead><tr><th>Item</th><th>External id</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{cis.map(c => <tr key={c.id}><td>{c.ciName}</td><td className="mono muted">{c.ciExternalId ?? '—'}</td><td>{canEdit && <button type="button" className="text" onClick={() => remove(c.id)}>Remove</button>}</td></tr>)}</tbody></table>)}
      {canEdit && (
        <p className="inline-form">
          <label>Item <input className="line" value={ci.name} onChange={e => setCi({ ...ci, name: e.target.value })} /></label>
          <label>External id <input className="line" value={ci.ext} onChange={e => setCi({ ...ci, ext: e.target.value })} /></label>
          <button type="button" className="text" disabled={!ci.name.trim()} onClick={addOne}>Add item</button>
        </p>)}
    </section>
  )
}
