import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, EmptyState, ErrorState, LoadingState, Panel, PropertyList, StatusChip } from './ui'
import { InspectorFrame } from './InspectorFrame'
import { createStep, getOwners, getSteps, patchStep, SECTIONS, setStepDependencies, type ContingencyView, type Owner, type StepRow } from './api'
import { ContingencyLine } from './Advisories'
import { errMsg } from './format'
import { fmtDay, fmtHM, utcToZonedInput, zonedInputToUtc } from './time'
import { Conflict, isConflict } from './Conflict'
import { useDraft } from './session'
import type { Selection } from './Planning'
import { useAction } from './useAction'
import { announce } from './announce'

const hm = fmtHM
const localInput = utcToZonedInput
const toUtc = zonedInputToUtc
type Field = 'title' | 'owner' | 'start' | 'minutes'
const planned = (s: StepRow) => `${fmtDay(s.plannedStartAt)} ${hm(s.plannedStartAt)}–${hm(s.plannedEndAt)} · ${s.plannedDurationMin} min`

// ---- The runbook: steps grouped by section, in planned order ---------------------------------------------------------------------------
export function Runbook({ trainId, canPlan, refreshKey, selection, onSelect, onChanged, contingency = null }: { trainId: string; canPlan: boolean; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void; onChanged: () => void; contingency?: ContingencyView | null }) {
  const [steps, setSteps] = useState<StepRow[] | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  const [draft, setDraft] = useDraft<{ title: string; section: string; ownerId: string; start: string; minutes: string }>(`addStep:${trainId}`)   // survives a closed tab
  const [err, setErr] = useState<string | null>(null)
  const [bad, setBad] = useState<Field | null>(null)
  const { run, pending } = useAction()
  const titleRef = useRef<HTMLInputElement>(null), addBtn = useRef<HTMLButtonElement>(null), errId = useId(), idBase = useId()
  useEffect(() => { getSteps(trainId).then(setSteps).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { if (draft && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [draft, owners.length])
  if (!steps) return err ? <p className="bad" role="alert">✗ {err}</p> : null

  const adding = draft !== undefined
  const d = draft ?? { title: '', section: 'Deploy', ownerId: '', start: '', minutes: '15' }
  const ownerId = d.ownerId || owners[0]?.id || ''
  // A new step starts where the last planned step ends (UX review 10), so a sequence needs no date typing.
  const lastEnd = steps.length === 0 ? '' : localInput(steps.reduce((a, b) => (Date.parse(b.plannedEndAt) > Date.parse(a.plannedEndAt) ? b : a)).plannedEndAt)
  const fail = (f: Field, msg: string) => { setBad(f); setErr(msg); document.getElementById(`${idBase}-${f}`)?.focus() }
  const add = (e: FormEvent) => { e.preventDefault(); void run('add', async () => {
    const owner = owners.find(o => o.id === ownerId)
    if (!d.title.trim()) return fail('title', 'Step: enter what happens.')
    if (!owner) return fail('owner', 'Owner: choose who owns this step.')
    if (!d.start) return fail('start', 'Starts: enter a start date and time.')
    if (!(Number(d.minutes) > 0)) return fail('minutes', 'Minutes: enter a duration above 0.')
    setErr(null); setBad(null)
    try {
      const made = await createStep(trainId, { title: d.title.trim(), section: d.section, ownerUserId: owner.kind === 'user' ? owner.id : null, ownerTeamId: owner.kind === 'team' ? owner.id : null, plannedStartAt: toUtc(d.start), plannedDurationMin: Number(d.minutes) })
      // The form stays open for the next step: same section and owner, starting when this one ends.
      setDraft({ ...d, ownerId, title: '', start: made?.plannedEndAt ? localInput(made.plannedEndAt) : d.start }); announce(`Step added: ${d.title.trim()}.`); onChanged(); titleRef.current?.focus()
    } catch (e) { setErr(errMsg(e)) }
  }) }
  const toggleForm = () => {
    setErr(null); setBad(null)
    if (adding) { setDraft(undefined); return }
    setDraft({ title: '', section: 'Deploy', ownerId: '', start: lastEnd, minutes: '15' }); requestAnimationFrame(() => titleRef.current?.focus())
  }
  const cancel = () => { setDraft(undefined); setErr(null); setBad(null); requestAnimationFrame(() => addBtn.current?.focus()) }
  const field = (f: Field) => ({ id: `${idBase}-${f}`, ...(bad === f ? { 'aria-invalid': true, 'aria-describedby': errId } : {}) })
  return (
    <Panel label="Runbook" title="Runbook" subtitle="The plan: steps by section, in planned order. Actuals are recorded in the run, never here."
      meta={<><span><span className="mono">{steps.length}</span> {steps.length === 1 ? 'step' : 'steps'}</span>{canPlan && <Button ref={addBtn} aria-expanded={adding} onClick={toggleForm}>Add step</Button>}</>}>
      {contingency && steps.length > 0 && <ContingencyLine c={contingency} />}
      {adding && (
        <form onSubmit={add}><p className="inline-form">
          <label>Step <input ref={titleRef} className="line wide" value={d.title} onChange={e => setDraft({ ...d, ownerId, title: e.target.value })} placeholder="What happens" {...field('title')} /></label>{' '}
          <label>Section <select className="line" value={d.section} onChange={e => setDraft({ ...d, ownerId, section: e.target.value })}>{SECTIONS.map(x => <option key={x}>{x}</option>)}</select></label>{' '}
          <label>Owner <select className="line" value={ownerId} onChange={e => setDraft({ ...d, ownerId: e.target.value })} {...field('owner')}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>{' '}
          <label>Starts <input className="line" type="datetime-local" value={d.start} onChange={e => setDraft({ ...d, ownerId, start: e.target.value })} {...field('start')} /></label>{' '}
          <label>Minutes <input className="line narrow" inputMode="numeric" value={d.minutes} onChange={e => setDraft({ ...d, ownerId, minutes: e.target.value })} {...field('minutes')} /></label>{' '}
          <button type="submit" className="text" disabled={!!pending}>{pending === 'add' ? 'Adding…' : 'Add'}</button> <button type="button" className="text" onClick={cancel}>Cancel</button>
        </p></form>)}
      {err && <p id={errId} className="bad" role="alert">✗ {err}</p>}
      {steps.length === 0 ? <p className="muted">No runbook steps yet.</p> : SECTIONS.map(sec => {
        const list = steps.filter(s => s.section === sec)
        if (list.length === 0) return null
        return (
          <div key={sec}>
            <h3 id={`${idBase}-sec-${sec}`} className={sec === 'Rollback' ? 'cap warn' : 'cap'}>{sec}</h3>
            <table className="grid" aria-labelledby={`${idBase}-sec-${sec}`}>
              <thead><tr><th>Step</th><th><span className="sr-only">Title</span></th><th>Owner</th><th>Planned</th><th>Depends on</th></tr></thead>
              <tbody>
                {list.map(s => {
                  const sel = selection?.kind === 'step' && selection.id === s.id
                  return (
                    <tr key={s.id} className={sel ? 'selected' : undefined}>
                      <td className="mono muted nowrap">{s.stepCode}</td>
                      <td><button type="button" className="text plainlink" aria-current={sel ? 'true' : undefined} onClick={() => onSelect({ kind: 'step', id: s.id })}>{s.title}</button></td>
                      <td>{s.ownerName ?? '—'}</td>
                      <td className="mono muted nowrap">{planned(s)}</td>
                      <td className="mono">{s.dependsOn.length === 0 ? <span className="muted">—</span> : s.dependsOn.map(x => x.code).join(', ')}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>)
      })}
    </Panel>
  )
}

// ---- Inspector for one step: edit the plan, and the dependencies (a cycle is refused with the loop named) ---------------------------------
export function StepInspector({ stepId, trainId, canPlan, refreshKey, onChanged, onClose }: { stepId: string; trainId: string; canPlan: boolean; refreshKey: number; onChanged: () => void; onClose: () => void }) {
  const [steps, setSteps] = useState<StepRow[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<import('./api').ApiError | null>(null)
  const [draft, setDraft] = useDraft<{ title: string; minutes: string; start: string; section: string }>(`editStep:${stepId}`)
  const [pick, setPick] = useState('')
  const { run: once, pending } = useAction()
  const titleRef = useRef<HTMLInputElement>(null), editBtn = useRef<HTMLButtonElement>(null)
  useEffect(() => { getSteps(trainId).then(setSteps).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { setErr(null); setConflict(null); setPick('') }, [stepId])
  const s = steps?.find(x => x.id === stepId)
  if (!s) return (
    <InspectorFrame focusKey={`step:${stepId}`} title="Step" subtitle="Step" loading={!steps} onClose={onClose}>
      {err ? <div role="alert"><ErrorState what="The step" message={err} /></div> : steps ? <EmptyState title="This step no longer exists" /> : <LoadingState what="the step" />}
    </InspectorFrame>)

  const run = (label: string, fn: () => Promise<unknown>, said?: string) => once(label, async () => {
    setErr(null); setConflict(null)
    try { await fn(); if (said) announce(said); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  })
  const editing = draft !== undefined
  const d = draft ?? { title: s.title, minutes: String(s.plannedDurationMin), start: localInput(s.plannedStartAt), section: s.section }
  const back = () => requestAnimationFrame(() => editBtn.current?.focus())   // the edit form unmounts: focus returns to Edit step
  const save = () => run('Saving…', async () => {
    await patchStep(s.id, { title: d.title, section: d.section, plannedDurationMin: Number(d.minutes), plannedStartAt: toUtc(d.start) }, s.version)
    setDraft(undefined); back()
  }, 'Step saved.')
  const others = (steps ?? []).filter(x => x.id !== s.id && !s.dependsOn.some(dep => dep.id === x.id))
  const setDeps = (ids: string[], label: string) => run(label, () => setStepDependencies(s.id, ids, s.version), 'Dependencies saved.')

  return (
    <InspectorFrame focusKey={`step:${stepId}`} title={s.title} subtitle={`Step · ${s.stepCode}`} onClose={onClose}>
      {!editing ? (
        <PropertyList items={[
          { label: 'Code', value: s.stepCode, mono: true }, { label: 'Section', value: s.section === 'Rollback' ? <StatusChip tone="warning">Rollback</StatusChip> : s.section },
          { label: 'Owner', value: s.ownerName ?? '—' }, { label: 'Planned', value: planned(s), mono: true }, { label: 'Product', value: s.productName ?? '—' },
        ]} />
      ) : (
        <p className="inline-form stack">
          <label>Title <input ref={titleRef} className="line block" value={d.title} onChange={e => setDraft({ ...d, title: e.target.value })} /></label>
          <label>Section <select className="line" value={d.section} onChange={e => setDraft({ ...d, section: e.target.value })}>{SECTIONS.map(x => <option key={x}>{x}</option>)}</select></label>
          <label>Starts <input className="line" type="datetime-local" value={d.start} onChange={e => setDraft({ ...d, start: e.target.value })} /></label>
          <label>Minutes <input className="line narrow" inputMode="numeric" value={d.minutes} onChange={e => setDraft({ ...d, minutes: e.target.value })} /></label>
        </p>)}
      {canPlan && <p className="actions-col">{!editing
        ? <button ref={editBtn} type="button" className="text" disabled={!!pending} onClick={() => { setDraft({ title: s.title, minutes: String(s.plannedDurationMin), start: localInput(s.plannedStartAt), section: s.section }); requestAnimationFrame(() => titleRef.current?.focus()) }}>Edit step</button>
        : <><button type="button" className="text" disabled={!!pending} onClick={() => void save()}>{pending === 'Saving…' ? 'Saving…' : 'Save step'}</button><button type="button" className="text" disabled={!!pending} onClick={() => { setDraft(undefined); back() }}>Cancel</button></>}</p>}

      <h3 className="cap">Depends on</h3>
      {s.dependsOn.length === 0 ? <p className="muted">Nothing: this step can start whenever its window opens.</p> : (
        <ul className="plain">{s.dependsOn.map(dep => (
          <li key={dep.id}><span className="mono">{dep.code}</span> {steps?.find(x => x.id === dep.id)?.title}
            {canPlan && <> <button type="button" className="text quiet" disabled={!!pending} aria-label={`Remove dependency on ${dep.code}`} onClick={() => void setDeps(s.dependsOn.filter(x => x.id !== dep.id).map(x => x.id), `rm:${dep.id}`)}>{pending === `rm:${dep.id}` ? 'Removing…' : 'Remove'}</button></>}</li>))}</ul>)}
      {canPlan && others.length > 0 && (
        <p className="inline-form"><label>Add dependency <select className="line" value={pick} onChange={e => setPick(e.target.value)}>
          <option value="">Choose a step…</option>{others.map(o => <option key={o.id} value={o.id}>{o.stepCode} {o.title}</option>)}</select></label>{' '}
          <button type="button" className="text" disabled={!pick || !!pending} onClick={() => { const id = pick; setPick(''); void setDeps([...s.dependsOn.map(x => x.id), id], 'Adding…') }}>{pending === 'Adding…' ? 'Adding…' : 'Add'}</button></p>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="step" />}
    </InspectorFrame>
  )
}
