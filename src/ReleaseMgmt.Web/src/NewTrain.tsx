import { useEffect, useState } from 'react'
import { cloneTrain, createTrain, getTemplates, type CreatedTrain, type StreamRow, type TemplateRow } from './api'
import { errMsg, plural } from './format'
import { serverNow, utcToZonedInput, zonedInputToUtc } from './time'
import { useDraft } from './session'

/** How the new train starts (REOS-80). Underlined text choices, never a segmented control (UI.md). */
export type NewTrainMode = 'blank' | 'template' | 'copy'
export interface NewTrainDraft { open: boolean; mode: NewTrainMode; templateId: string; sourceId: string; title: string; target: string; risk: string; start: string; end: string }
export const newTrainKey = 'newtrain'
export const emptyNewTrain = (): NewTrainDraft => ({ open: true, mode: 'blank', templateId: '', sourceId: '', title: '', target: '', risk: '', start: '', end: '' })

const MODES: { mode: NewTrainMode; label: string }[] = [{ mode: 'blank', label: 'Blank' }, { mode: 'template', label: 'From template' }, { mode: 'copy', label: 'Copy of a train' }]
const TIERS = ['Low', 'Moderate', 'High', 'VeryHigh']
const TIER_WORD: Record<string, string> = { Low: 'Low', Moderate: 'Moderate', High: 'High', VeryHigh: 'Very high' }

/** Today in the display zone (D24), from the server clock: the earliest target the API accepts. */
const todayInZone = () => utcToZonedInput(new Date(serverNow()).toISOString()).slice(0, 10)

/**
 * The "New train" drawer (REOS-80): blank, from an Approved template, or a copy of a train in the Stream. Opens in the right drawer (no modal); the form is a
 * session draft like the other drawers, so a closed tab keeps it. On success the caller opens the new train's workspace.
 */
export function NewTrainDrawer({ trains, onCreated }: { trains: StreamRow[]; onCreated: (t: CreatedTrain) => void }) {
  const [draft, setDraft] = useDraft<NewTrainDraft>(newTrainKey)
  const [templates, setTemplates] = useState<TemplateRow[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<CreatedTrain | null>(null)
  const d: NewTrainDraft = draft ?? emptyNewTrain()
  const set = (patch: Partial<NewTrainDraft>) => { setDraft({ ...d, ...patch }); setErr(null) }
  const close = () => setDraft({ ...d, open: false })   // Close keeps what was typed (a session draft); Discard and Done drop it

  useEffect(() => { getTemplates().then(ts => setTemplates(ts.filter(t => t.status === 'Approved'))).catch(e => { setTemplates([]); setErr(errMsg(e, 'Could not load the templates.')) }) }, [])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.defaultPrevented) close() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  const template = d.mode === 'template' ? templates?.find(t => t.id === d.templateId) : undefined
  const source = d.mode === 'copy' ? trains.find(t => t.id === d.sourceId) : undefined
  const today = todayInZone()
  const defaultTier = template?.defaultRiskTier ?? source?.riskTier ?? 'Moderate'
  const defaultFrom = template ? 'template default' : source ? 'same as the source' : 'default'

  // Why Create is off, in words on the line below it (UI.md: disabled actions always say why).
  const why = d.mode === 'template' && !template ? 'Choose an approved template.'
    : d.mode === 'copy' && !source ? 'Choose the train to copy.'
    : d.title.trim() === '' ? 'Enter a title.'
    : d.target === '' ? 'Choose a target release date.'
    : d.target < today ? 'The target release date is in the past.'
    : d.mode === 'template' && (d.start === '') !== (d.end === '') ? 'Give the window both a start and an end, or neither.'
    : null

  const create = async () => {
    if (why) return
    setBusy(true); setErr(null)
    try {
      const risk = d.risk || null
      const created = d.mode === 'copy'
        ? await cloneTrain(d.sourceId, { title: d.title.trim(), targetReleaseDate: d.target, riskTier: risk })
        : await createTrain({
            title: d.title.trim(), targetReleaseDate: d.target, riskTier: risk, templateId: d.mode === 'template' ? d.templateId : null,
            windowStartsAt: d.mode === 'template' && d.start ? zonedInputToUtc(d.start) : null, windowEndsAt: d.mode === 'template' && d.end ? zonedInputToUtc(d.end) : null,
          })
      setDraft({ ...emptyNewTrain(), mode: d.mode })   // the form is done with; the drawer stays open to show what was created
      setDone(created)
      onCreated(created)
    } catch (e) { setErr(errMsg(e, 'Could not create the train.')) }
    finally { setBusy(false) }
  }

  const head = (
    <div className="section-head"><span className="cap">Drawer · new train</span>
      <span><button type="button" className="text quiet" onClick={close}>Close</button></span></div>)

  if (done) {
    const c = done.created
    const parts = [plural(c.products, 'product'), plural(c.gates, 'gate'), plural(c.tasks, 'task'), plural(c.steps, 'runbook step'), plural(c.commTemplates, 'message'), plural(c.commSchedule, 'T-minus item')]
    return (
      <>
        {head}
        <h2>New train</h2>
        <p className="ok" role="status">✓ Created {done.title}</p>
        <p className="muted">{done.source === 'Clone' ? 'Copied' : done.source === 'Template' ? 'From the template' : 'Blank'} · Planning · risk {TIER_WORD[done.riskTier] ?? done.riskTier} · {parts.join(' · ')}{c.window ? ' · window set' : ''}</p>
        {done.notes.length > 0 && <ul className="plain">{done.notes.map((n, i) => <li key={i} className="warn">▲ {n}</li>)}</ul>}
        <p className="actions-col">
          <button type="button" className="text" onClick={() => setDone(null)}>Create another</button>
          <button type="button" className="text" onClick={() => setDraft(undefined)}>Done</button>
        </p>
      </>
    )
  }

  return (
    <>
      {head}
      <h2>New train</h2>
      <p className="muted">A new train starts in Planning. Its gates are due a set number of business days before the target date.</p>
      <p><span className="cap" id="nt-mode">Start from</span><br />
        <span className="tabs" role="group" aria-labelledby="nt-mode">
          {MODES.map(m => <button key={m.mode} type="button" className="choice" aria-pressed={d.mode === m.mode} onClick={() => set({ mode: m.mode })}>{m.label}</button>)}
        </span></p>

      {d.mode === 'template' && (templates === null ? <p className="muted">Loading templates…</p>
        : templates.length === 0 ? <p className="muted">No template is approved yet. A Release Manager or Governance Officer approves one on Templates.</p>
        : <>
            <p><label className="cap" htmlFor="nt-template">Template</label>
              <select id="nt-template" className="line block" value={d.templateId} onChange={e => set({ templateId: e.target.value })}>
                <option value="">Choose an approved template</option>
                {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select></p>
            {template && <p className="muted">{plural(template.gateCount, 'gate')} · {plural(template.stepCount, 'runbook step')} · {plural(template.scheduleCount, 'T-minus message')} · risk {TIER_WORD[template.defaultRiskTier] ?? template.defaultRiskTier}
              {template.reviewOverdue && <span className="warn"> · ▲ review overdue</span>}</p>}
          </>)}

      {d.mode === 'copy' && <>
        <p><label className="cap" htmlFor="nt-source">Copy of</label>
          <select id="nt-source" className="line block" value={d.sourceId} onChange={e => set({ sourceId: e.target.value })}>
            <option value="">Choose a train</option>
            {trains.map(t => <option key={t.id} value={t.id}>{t.title} · {t.status}</option>)}
          </select></p>
        <p className="muted">Copies products, gates, tasks (all open), runbook steps and messages, moved to the new target date. Decisions, evidence, runs and the audit trail stay with the original.</p>
      </>}

      <p><label className="cap" htmlFor="nt-title">Title</label>
        <input id="nt-title" className="line block" maxLength={200} placeholder="R27.01 Payments winter release" value={d.title} onChange={e => set({ title: e.target.value })} /></p>
      <p><label className="cap" htmlFor="nt-target">Target release date</label>
        <input id="nt-target" className="line block" type="date" min={today} value={d.target} onChange={e => set({ target: e.target.value })} /></p>
      <p><label className="cap" htmlFor="nt-risk">Risk tier</label>
        <select id="nt-risk" className="line block" value={d.risk} onChange={e => set({ risk: e.target.value })}>
          <option value="">{TIER_WORD[defaultTier] ?? defaultTier} ({defaultFrom})</option>
          {TIERS.map(t => <option key={t} value={t}>{TIER_WORD[t]}</option>)}
        </select></p>

      {d.mode === 'template' && template && template.stepCount > 0 && <>
        <p className="muted">Deployment window, optional: the template&apos;s {plural(template.stepCount, 'runbook step')} are planned from its start. Without a window the steps are left out.</p>
        <p className="inline-form">
          <label>Starts <input className="line" type="datetime-local" value={d.start} onChange={e => set({ start: e.target.value })} /></label>
          <label>Ends <input className="line" type="datetime-local" value={d.end} onChange={e => set({ end: e.target.value })} /></label>
        </p>
      </>}

      <p className="actions-col">
        <button type="button" className="text quiet" onClick={() => setDraft(undefined)}>Discard</button>
        <button type="button" className="text strong" disabled={busy || why !== null} onClick={create}>{d.mode === 'copy' ? 'Create copy' : 'Create train'}</button>
      </p>
      {why && <p className="muted">{why}</p>}
      {busy && <p className="muted" role="status">Creating…</p>}
      {err && <p className="bad" role="alert">✗ {err}</p>}
    </>
  )
}
