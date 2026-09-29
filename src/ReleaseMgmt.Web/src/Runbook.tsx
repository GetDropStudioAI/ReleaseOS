import { useEffect, useState } from 'react'
import { createStep, getOwners, getSteps, patchStep, SECTIONS, setStepDependencies, type Owner, type StepRow } from './api'
import { day, errMsg } from './format'
import { Conflict, isConflict } from './Conflict'
import { useDraft } from './session'
import type { Selection } from './Planning'

const hm = (iso: string) => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })
const localInput = (iso: string) => { const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}` }
const toUtc = (local: string) => new Date(local).toISOString().replace(/\.\d{3}Z$/, 'Z')
const planned = (s: StepRow) => `${day(s.plannedStartAt.slice(0, 10))} ${hm(s.plannedStartAt)}–${hm(s.plannedEndAt)} · ${s.plannedDurationMin} min`

// ---- The runbook: steps grouped by section, in planned order ---------------------------------------------------------------------------
export function Runbook({ trainId, canPlan, refreshKey, selection, onSelect, onChanged }: { trainId: string; canPlan: boolean; refreshKey: number; selection: Selection; onSelect: (s: Selection) => void; onChanged: () => void }) {
  const [steps, setSteps] = useState<StepRow[] | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  const [draft, setDraft] = useDraft<{ title: string; section: string; ownerId: string; start: string; minutes: string }>(`addStep:${trainId}`)   // survives a closed tab
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => { getSteps(trainId).then(setSteps).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { if (draft && owners.length === 0) getOwners().then(setOwners).catch(e => setErr(errMsg(e))) }, [draft, owners.length])
  if (!steps) return err ? <p className="bad" role="alert">✗ {err}</p> : null

  const adding = draft !== undefined
  const d = draft ?? { title: '', section: 'Deploy', ownerId: '', start: '', minutes: '15' }
  const ownerId = d.ownerId || owners[0]?.id || ''
  const add = async () => {
    const owner = owners.find(o => o.id === ownerId)
    if (!d.title.trim() || !owner || !d.start || !(Number(d.minutes) > 0)) { setErr('A step needs a title, an owner, a start time and a duration above 0.'); return }
    setErr(null)
    try {
      await createStep(trainId, { title: d.title.trim(), section: d.section, ownerUserId: owner.kind === 'user' ? owner.id : null, ownerTeamId: owner.kind === 'team' ? owner.id : null, plannedStartAt: toUtc(d.start), plannedDurationMin: Number(d.minutes) })
      setDraft(undefined); onChanged()
    } catch (e) { setErr(errMsg(e)) }
  }
  return (
    <section aria-label="Runbook">
      <div className="section-head"><h2 className="cap dark">Runbook</h2>
        <span className="muted"><span className="mono label">{steps.length}</span> {steps.length === 1 ? 'step' : 'steps'}{canPlan && <> · <button type="button" className="text" onClick={() => setDraft(adding ? undefined : { title: '', section: 'Deploy', ownerId: '', start: '', minutes: '15' })}>Add step</button></>}</span></div>
      {adding && (
        <p className="inline-form">
          <label>Step <input className="line wide" value={d.title} onChange={e => setDraft({ ...d, ownerId, title: e.target.value })} placeholder="What happens" /></label>{' '}
          <label>Section <select className="line" value={d.section} onChange={e => setDraft({ ...d, ownerId, section: e.target.value })}>{SECTIONS.map(x => <option key={x}>{x}</option>)}</select></label>{' '}
          <label>Owner <select className="line" value={ownerId} onChange={e => setDraft({ ...d, ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>{' '}
          <label>Starts <input className="line" type="datetime-local" value={d.start} onChange={e => setDraft({ ...d, ownerId, start: e.target.value })} /></label>{' '}
          <label>Minutes <input className="line narrow" inputMode="numeric" value={d.minutes} onChange={e => setDraft({ ...d, ownerId, minutes: e.target.value })} /></label>{' '}
          <button type="button" className="text" onClick={add}>Add</button> <button type="button" className="text" onClick={() => { setDraft(undefined); setErr(null) }}>Cancel</button>
        </p>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {steps.length === 0 ? <p className="muted">No runbook steps yet.</p> : SECTIONS.map(sec => {
        const list = steps.filter(s => s.section === sec)
        if (list.length === 0) return null
        return (
          <div key={sec}>
            <h3 className={sec === 'Rollback' ? 'cap warn' : 'cap'}>{sec}</h3>
            <table className="grid">
              <thead><tr><th>Step</th><th /><th>Owner</th><th>Planned</th><th>Depends on</th></tr></thead>
              <tbody>
                {list.map(s => {
                  const sel = selection?.kind === 'step' && selection.id === s.id
                  return (
                    <tr key={s.id} aria-selected={sel} className={sel ? 'selected' : undefined}>
                      <td className="mono muted nowrap">{s.stepCode}</td>
                      <td><button type="button" className="text plainlink" onClick={() => onSelect({ kind: 'step', id: s.id })}>{s.title}</button></td>
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
    </section>
  )
}

// ---- Inspector for one step: edit the plan, and the dependencies (a cycle is refused with the loop named) ---------------------------------
export function StepInspector({ stepId, trainId, canPlan, refreshKey, onChanged }: { stepId: string; trainId: string; canPlan: boolean; refreshKey: number; onChanged: () => void }) {
  const [steps, setSteps] = useState<StepRow[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<import('./api').ApiError | null>(null)
  const [draft, setDraft] = useDraft<{ title: string; minutes: string; start: string; section: string }>(`editStep:${stepId}`)
  const [pick, setPick] = useState('')
  useEffect(() => { getSteps(trainId).then(setSteps).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  useEffect(() => { setErr(null); setConflict(null); setPick('') }, [stepId])
  const s = steps?.find(x => x.id === stepId)
  if (!s) return <p className="muted">{err ? `✗ ${err}` : steps ? 'This step no longer exists.' : 'Loading…'}</p>

  const run = async (fn: () => Promise<unknown>) => {
    setErr(null); setConflict(null)
    try { await fn(); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const editing = draft !== undefined
  const d = draft ?? { title: s.title, minutes: String(s.plannedDurationMin), start: localInput(s.plannedStartAt), section: s.section }
  const save = () => run(async () => {
    await patchStep(s.id, { title: d.title, section: d.section, plannedDurationMin: Number(d.minutes), plannedStartAt: toUtc(d.start) }, s.version)
    setDraft(undefined)
  })
  const others = (steps ?? []).filter(x => x.id !== s.id && !s.dependsOn.some(dep => dep.id === x.id))
  const setDeps = (ids: string[]) => run(() => setStepDependencies(s.id, ids, s.version))

  return (
    <>
      <h2>{s.title}</h2>
      {!editing ? (
        <dl className="facts"><dt>Code</dt><dd className="mono">{s.stepCode}</dd><dt>Section</dt><dd className={s.section === 'Rollback' ? 'warn' : undefined}>{s.section}</dd>
          <dt>Owner</dt><dd>{s.ownerName ?? '—'}</dd><dt>Planned</dt><dd className="mono">{planned(s)}</dd><dt>Product</dt><dd>{s.productName ?? '—'}</dd></dl>
      ) : (
        <p className="inline-form stack">
          <label>Title <input className="line block" value={d.title} onChange={e => setDraft({ ...d, title: e.target.value })} /></label>
          <label>Section <select className="line" value={d.section} onChange={e => setDraft({ ...d, section: e.target.value })}>{SECTIONS.map(x => <option key={x}>{x}</option>)}</select></label>
          <label>Starts <input className="line" type="datetime-local" value={d.start} onChange={e => setDraft({ ...d, start: e.target.value })} /></label>
          <label>Minutes <input className="line narrow" inputMode="numeric" value={d.minutes} onChange={e => setDraft({ ...d, minutes: e.target.value })} /></label>
        </p>)}
      {canPlan && <p className="actions-col">{!editing
        ? <button type="button" className="text" onClick={() => setDraft({ title: s.title, minutes: String(s.plannedDurationMin), start: localInput(s.plannedStartAt), section: s.section })}>Edit step</button>
        : <><button type="button" className="text" onClick={save}>Save step</button><button type="button" className="text" onClick={() => setDraft(undefined)}>Cancel</button></>}</p>}

      <h3 className="cap">Depends on</h3>
      {s.dependsOn.length === 0 ? <p className="muted">Nothing: this step can start whenever its window opens.</p> : (
        <ul className="plain">{s.dependsOn.map(dep => (
          <li key={dep.id}><span className="mono">{dep.code}</span> {steps?.find(x => x.id === dep.id)?.title}
            {canPlan && <> <button type="button" className="text quiet" onClick={() => setDeps(s.dependsOn.filter(x => x.id !== dep.id).map(x => x.id))}>Remove</button></>}</li>))}</ul>)}
      {canPlan && others.length > 0 && (
        <p className="inline-form"><label>Add dependency <select className="line" value={pick} onChange={e => setPick(e.target.value)}>
          <option value="">Choose a step…</option>{others.map(o => <option key={o.id} value={o.id}>{o.stepCode} {o.title}</option>)}</select></label>{' '}
          <button type="button" className="text" disabled={!pick} onClick={() => { const id = pick; setPick(''); void setDeps([...s.dependsOn.map(x => x.id), id]) }}>Add</button></p>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="step" />}
    </>
  )
}
