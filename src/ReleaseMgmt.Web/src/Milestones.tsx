import { useCallback, useEffect, useState } from 'react'
import { getMe, getOwners, type ApiError, type Me, type Owner } from './api'
import { day, errMsg, tMinus } from './format'
import { displayZone, fmtDayTime, serverNow } from './time'
import { useDraft } from './session'
import { Conflict, isConflict } from './Conflict'
import { addMilestone, editMilestone, getMilestones, milestoneAction, removeMilestone, type Milestone, type MilestoneInput } from './milestonesApi'

/**
 * Train milestones (decision 2026-10-01, Q-0840..Q-0846): named key dates on a train ("Code complete", "UAT sign-off"). Informational: never certified and never
 * a guard on a gate or train move. Status is glyph + word + colour: ◆ marks a milestone (on the gate timeline too), "✓ done", "due", "overdue".
 * Add and edit inline (drafts survive a closed tab), remove with an inline confirm, mark done / not done. No modals, no checkboxes, no pills.
 */

/** Today in the display zone, by the server's clock. */
const todayInZone = () => new Intl.DateTimeFormat('en-CA', { timeZone: displayZone(), year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(serverNow()))

export type MilestoneState = { cls: string; word: string }
export function milestoneState(m: Pick<Milestone, 'done' | 'dueOn'>, today = todayInZone()): MilestoneState {
  if (m.done) return { cls: 'ok', word: '✓ done' }
  return m.dueOn < today ? { cls: 'bad', word: 'overdue' } : { cls: 'muted', word: 'due' }
}

/** The milestones of a train, refetched whenever the workspace's refreshKey moves (SignalR TrainChanged, or a local change). */
export function useMilestones(trainId: string, refreshKey: number) {
  const [rows, setRows] = useState<Milestone[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    getMilestones(trainId).then(r => { if (live) { setRows(r); setError(null) } }).catch(e => { if (live) setError(errMsg(e)) })
    return () => { live = false }
  }, [trainId, refreshKey])
  return { rows, error }
}

interface Form { name: string; dueOn: string; owner: string; note: string }   // owner = '' (nobody) or "user:<id>" / "team:<id>"
const emptyForm = (): Form => ({ name: '', dueOn: '', owner: '', note: '' })
const toInput = (f: Form): MilestoneInput => ({
  name: f.name.trim(), dueOn: f.dueOn, note: f.note,
  ownerUserId: f.owner.startsWith('user:') ? f.owner.slice(5) : null, ownerTeamId: f.owner.startsWith('team:') ? f.owner.slice(5) : null,
})
const fromRow = (m: Milestone): Form => ({ name: m.name, dueOn: m.dueOn, note: m.note ?? '', owner: m.ownerUserId ? `user:${m.ownerUserId}` : m.ownerTeamId ? `team:${m.ownerTeamId}` : '' })

function Fields({ form, owners, onChange, idPrefix }: { form: Form; owners: Owner[]; onChange: (f: Form) => void; idPrefix: string }) {
  return (<>
    <label>Milestone <input className="line wide" value={form.name} maxLength={200} onChange={e => onChange({ ...form, name: e.target.value })} placeholder="Code complete" aria-describedby={`${idPrefix}-help`} /></label>{' '}
    <label>Date <input className="line" type="date" value={form.dueOn} onChange={e => onChange({ ...form, dueOn: e.target.value })} /></label>{' '}
    <label>Owner <select className="line" value={form.owner} onChange={e => onChange({ ...form, owner: e.target.value })}>
      <option value="">Nobody</option>
      {owners.map(o => <option key={`${o.kind}:${o.id}`} value={`${o.kind}:${o.id}`}>{o.kind === 'team' ? `${o.name} (team)` : o.name}</option>)}
    </select></label>{' '}
    <label>Note <input className="line" value={form.note} maxLength={2000} onChange={e => onChange({ ...form, note: e.target.value })} placeholder="Optional" /></label>
    <span id={`${idPrefix}-help`} className="sr-only">A named key date on this train. It never blocks a gate or the train.</span>
  </>)
}

export function MilestonesSection({ trainId, rows, error, canPlan, onChanged }: { trainId: string; rows: Milestone[] | null; error: string | null; canPlan: boolean; onChanged: () => void }) {
  const [adding, setAdding] = useDraft<Form>(`addMilestone:${trainId}`)   // survives a closed tab (session engine)
  const [editing, setEditing] = useDraft<{ id: string; form: Form }>(`editMilestone:${trainId}`)
  const [confirm, setConfirm] = useState<string | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  const [me, setMe] = useState<Me | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { getMe().then(setMe).catch(() => setMe(null)) }, [])
  const formOpen = adding !== undefined || editing !== undefined
  useEffect(() => { if (formOpen && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [formOpen, owners.length])

  const run = useCallback(async (fn: () => Promise<unknown>, after?: () => void) => {
    setErr(null); setConflict(null); setBusy(true)
    try { await fn(); after?.(); onChanged() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }   // a 409 keeps the draft; the notice says what changed
    finally { setBusy(false) }
  }, [onChanged])

  const valid = (f: Form) => (!f.name.trim() ? 'A milestone needs a name.' : !f.dueOn ? 'A milestone needs a date.' : null)
  const add = () => { const why = adding && valid(adding); if (why) { setErr(why); return } if (adding) void run(() => addMilestone(trainId, toInput(adding)), () => setAdding(undefined)) }
  const save = (m: Milestone, version = m.version) => {
    if (!editing) return
    const why = valid(editing.form); if (why) { setErr(why); return }
    void run(() => editMilestone(m.id, toInput(editing.form), version), () => setEditing(undefined))
  }
  // Done / not done: planners, or the owner when they are not a Viewer (Q-0842). Team ownership is checked by the server; a refusal shows inline.
  const canMark = (m: Milestone) => canPlan || (!!me && !me.roles.includes('Viewer') && (m.ownerUserId === me.id || !!m.ownerTeamId))
  const done = rows?.filter(m => m.done).length ?? 0
  const today = todayInZone()

  return (
    <section aria-label="Milestones">
      <div className="section-head"><h2 className="cap dark">Milestones</h2>
        <span className="muted">{rows && <><span className="mono label">{done} / {rows.length}</span> done</>}
          {canPlan && <> · <button type="button" className="text" aria-expanded={adding !== undefined} onClick={() => { setErr(null); setAdding(adding ? undefined : emptyForm()) }}>{adding ? 'Cancel adding' : 'Add milestone'}</button></>}</span></div>
      {adding && (
        <p className="inline-form" role="group" aria-label="New milestone">
          <Fields form={adding} owners={owners} onChange={setAdding} idPrefix="ms-new" />{' '}
          <button type="button" className="text" disabled={busy} onClick={add}>Add</button> <button type="button" className="text" onClick={() => setAdding(undefined)}>Cancel</button>
        </p>)}
      {(err || error) && <p className="bad" role="alert">✗ {err ?? error}</p>}
      {conflict && <Conflict error={conflict} what="milestone" />}
      {rows === null ? (error ? null : <p className="muted">Loading…</p>) : rows.length === 0 ? <p className="muted">No milestones on this train. A milestone is a named key date, such as code complete or UAT sign-off; it never blocks a gate.</p> : (
        <table className="grid milestones">
          <thead><tr><th>State</th><th>Milestone</th><th>Date</th><th>Owner</th><th>Done</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {rows.map(m => {
              const st = milestoneState(m, today)
              if (editing?.id === m.id) return (
                <tr key={m.id} className="selected">
                  <td colSpan={6}>
                    <span className="inline-form" role="group" aria-label={`Edit ${m.name}`}>
                      <Fields form={editing.form} owners={owners} onChange={f => setEditing({ id: m.id, form: f })} idPrefix={`ms-${m.id}`} />{' '}
                      <button type="button" className="text" disabled={busy} onClick={() => save(m)}>Save</button>{' '}
                      <button type="button" className="text" onClick={() => setEditing(undefined)}>Cancel</button>
                    </span>
                    {conflict && <p className="conflict-actions"><button type="button" className="text" onClick={() => save(m, (conflict.body?.current as { version?: number } | undefined)?.version)}>Overwrite with mine</button>{' '}
                      <button type="button" className="text" onClick={() => { setEditing(undefined); setConflict(null) }}>Use theirs</button></p>}
                  </td>
                </tr>)
              return (
                <tr key={m.id} data-testid="milestone-row">
                  <td className={`nowrap ${st.cls}`}><span className="ms-glyph" aria-hidden="true">◆</span> {st.word}</td>
                  <td className="wrap"><span className="label">{m.name}</span>{m.note && <span className="muted"> · {m.note}</span>}</td>
                  <td className="nowrap">{day(m.dueOn)} · <span className="mono">{tMinus(m.tMinus)}</span></td>
                  <td>{m.ownerName ?? <span className="muted">—</span>}</td>
                  <td className="mono muted nowrap">{m.done && m.doneAt ? <>{fmtDayTime(m.doneAt)}{m.doneBy ? <span className="ms-by"> · {m.doneBy}</span> : null}</> : '—'}</td>
                  <td className="nowrap">
                    {confirm === m.id ? (
                      <span role="group" aria-label={`Confirm removing ${m.name}`}>
                        <span className="warn">▲ Remove “{m.name}”?</span>{' '}
                        <button type="button" className="text destructive" disabled={busy} onClick={() => run(() => removeMilestone(m.id, m.version), () => setConfirm(null))}>Confirm remove</button>{' '}
                        <button type="button" className="text" onClick={() => setConfirm(null)}>Cancel</button>
                      </span>
                    ) : (<>
                      {canMark(m) && <button type="button" className="text" disabled={busy} onClick={() => run(() => milestoneAction(m.id, m.done ? 'undone' : 'done', m.version))}>{m.done ? 'Mark not done' : 'Mark done'}</button>}
                      {canPlan && <>{' '}<button type="button" className="text" onClick={() => { setErr(null); setConflict(null); setEditing({ id: m.id, form: fromRow(m) }) }} aria-label={`Edit ${m.name}`}>Edit</button>{' '}
                        <button type="button" className="text" onClick={() => setConfirm(m.id)} aria-label={`Remove ${m.name}`}>Remove</button></>}
                    </>)}
                  </td>
                </tr>)
            })}
          </tbody>
        </table>)}
    </section>
  )
}
