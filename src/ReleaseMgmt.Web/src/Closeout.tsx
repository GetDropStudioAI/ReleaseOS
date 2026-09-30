import { useEffect, useState } from 'react'
import {
  addKnownIssue, addPirAction, attestRollback, createPir, exitHypercare, getAttestation, getKnownIssues, getMe, getOwners, getPir,
  knownIssueAction, patchKnownIssue, pirAction, pirActionState, savePirSummary,
  type KnownIssue, type KnownIssuesData, type Me, type Owner, type Pir, type PirViewData, type RollbackAttestation, type TrainDetail,
} from './api'
import { day, errMsg, plural } from './format'
import { fmtDayTime, serverNow } from './time'

// Status is words + glyphs + coloured text (CLAUDE.md rule 9).
const PIR_STATE: Record<Pir['status'], { g: string; cls: string; word: string }> = {
  Required: { g: '▲', cls: 'warn', word: 'Required' }, Scheduled: { g: '◐', cls: 'accent', word: 'Scheduled' },
  Held: { g: '●', cls: 'ok', word: 'Held' }, Closed: { g: '✓', cls: 'ok', word: 'Closed' },
}
const SEVERITY: Record<KnownIssue['severity'], { g: string; cls: string }> = {
  Critical: { g: '✗', cls: 'bad' }, High: { g: '▲', cls: 'warn' }, Medium: { g: '◐', cls: '' }, Low: { g: '○', cls: 'muted' },
}
const ISSUE_STATE: Record<KnownIssue['status'], { g: string; cls: string }> = {
  Open: { g: '●', cls: 'warn' }, Accepted: { g: '◐', cls: 'accent' }, Resolved: { g: '✓', cls: 'ok' },
}
const SEVERITIES = ['Critical', 'High', 'Medium', 'Low'] as const

/**
 * Close-out sections of the train view (REOS-36): rollback-rehearsal attestation, post-implementation review with its actions,
 * and the hypercare known-issue log. Everything is inline; every write sends the row's Version as If-Match.
 */
export function Closeout({ train, canPlan, refreshKey, onChanged }: { train: TrainDetail; canPlan: boolean; refreshKey: number; onChanged: () => void }) {
  const [me, setMe] = useState<Me | null>(null)
  const [owners, setOwners] = useState<Owner[]>([])
  useEffect(() => { getMe().then(setMe).catch(() => setMe(null)); getOwners().then(o => setOwners(o.filter(x => x.kind === 'user'))).catch(() => setOwners([])) }, [])
  const roles = me?.roles ?? []
  const canCloseout = roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')
  const canAccept = roles.some(r => r === 'ReleaseManager' || r === 'GovernanceOfficer')
  const name = (id: string | null) => owners.find(o => o.id === id)?.name ?? '—'
  const common = { train, refreshKey, onChanged, name, owners }
  return (
    <>
      {train.status !== 'Aborted' && <RollbackSection {...common} canPlan={canPlan} />}
      {(train.status === 'Complete') && <PirSection {...common} canCloseout={canCloseout} meId={me?.id ?? null} />}
      {(train.status === 'Gated' || train.status === 'Executing' || train.status === 'Complete') && <IssuesSection {...common} canCloseout={canCloseout} canAccept={canAccept} canPlan={canPlan} />}
    </>
  )
}

interface SectionProps { train: TrainDetail; refreshKey: number; onChanged: () => void; name: (id: string | null) => string; owners: Owner[] }

function Errors({ err }: { err: string | null }) { return err ? <p className="bad" role="alert">✗ {err}</p> : null }

/** Run an action; on any failure show the message and refetch (a 409 means someone else saved first). */
function useDo(onChanged: () => void) {
  const [err, setErr] = useState<string | null>(null)
  const run = async (fn: () => Promise<unknown>, after?: () => void) => {
    setErr(null)
    try { await fn(); after?.(); onChanged() } catch (e) { setErr(errMsg(e)); onChanged() }
  }
  return { err, run, setErr }
}

// ---- rollback attestation --------------------------------------------------------------------------------------------
function RollbackSection({ train, refreshKey, onChanged, name, canPlan }: SectionProps & { canPlan: boolean }) {
  const [a, setA] = useState<RollbackAttestation | null>(null)
  const [open, setOpen] = useState(false)
  const [runId, setRunId] = useState('')
  const [note, setNote] = useState('')
  const { err, run, setErr } = useDo(onChanged)
  useEffect(() => { getAttestation(train.id).then(setA).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!a) return <Errors err={err} />
  const closed = train.status === 'Complete'
  return (
    <section aria-label="Rollback rehearsal">
      <div className="section-head"><h2 className="cap dark">Rollback rehearsal</h2>
        <span className="muted">{canPlan && !closed && <button type="button" className="text" onClick={() => setOpen(!open)}>{open ? 'Cancel' : a.rehearsedAt ? 'Attest again' : 'Attest rehearsal'}</button>}</span></div>
      {a.rehearsedAt
        ? <p><span className="ok">✓ Rehearsed</span> · <span className="mono">{fmtDayTime(a.rehearsedAt)}</span> · attested by {a.rehearsedByName ?? name(a.rehearsedByUserId)}</p>
        : a.required
          ? <p><span className="bad">✗ Not attested</span> <span className="muted">· a {train.riskTier} train needs a rehearsed rollback before Executing</span></p>
          : <p><span className="muted">○ Not attested · optional at {train.riskTier} risk</span></p>}
      {open && (
        <div className="inline-form stack">
          <label>Evidence run <select className="line" value={runId} onChange={e => setRunId(e.target.value)}>
            <option value="">None linked</option>
            {a.rehearsalRuns.map(r => <option key={r.id} value={r.id}>{fmtDayTime(r.startedAt)} · {r.outcome ?? 'ended'}</option>)}</select></label>
          <label>What was rehearsed <input className="line wide" value={note} onChange={e => setNote(e.target.value)} /></label>
          <span><button type="button" className="text" onClick={() => run(() => attestRollback(train.id, runId || null, note || null, train.version), () => { setOpen(false); setNote(''); setRunId('') })}>Record attestation</button>{' '}
            <span className="muted">You attest that the rollback section was rehearsed and works.</span></span>
        </div>)}
      <Errors err={err} />
    </section>
  )
}

// ---- PIR ---------------------------------------------------------------------------------------------------------------
function PirSection({ train, refreshKey, onChanged, name, owners, canCloseout, meId }: SectionProps & { canCloseout: boolean; meId: string | null }) {
  const [d, setD] = useState<PirViewData | null>(null)
  const [summary, setSummary] = useState<string | null>(null)   // null = not editing
  const [form, setForm] = useState({ text: '', ownerId: '', dueOn: '' })
  const { err, run, setErr } = useDo(onChanged)
  useEffect(() => { getPir(train.id).then(setD).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!d) return <Errors err={err} />
  const pir = d.pir
  if (!pir) {
    return (
      <section aria-label="Post-implementation review">
        <div className="section-head"><h2 className="cap dark">Post-implementation review</h2>
          <span className="muted">{canCloseout && <button type="button" className="text" onClick={() => run(() => createPir(train.id))}>Start a PIR</button>}</span></div>
        <p className="muted">No PIR. One is created automatically when a train closes with issues or unsuccessfully.</p>
        <Errors err={err} />
      </section>)
  }
  const s = PIR_STATE[pir.status]
  const closed = pir.status === 'Closed'
  const openActions = d.actions.filter(a => !a.doneAt).length
  const today = new Date(serverNow()).toISOString().slice(0, 10)
  return (
    <section aria-label="Post-implementation review">
      <div className="section-head"><h2 className="cap dark">Post-implementation review</h2>
        <span className="muted">{canCloseout && !closed && (
          <>
            {pir.status === 'Required' && <button type="button" className="text" onClick={() => run(() => pirAction(train.id, 'schedule', pir.version))}>Mark scheduled</button>}
            {pir.status !== 'Held' && <> <button type="button" className="text" onClick={() => run(() => pirAction(train.id, 'hold', pir.version, summary ?? undefined), () => setSummary(null))}>Mark held</button></>}
            {pir.status === 'Held' && <button type="button" className="text" title={openActions > 0 ? `${plural(openActions, 'action')} still open` : undefined} onClick={() => run(() => pirAction(train.id, 'close', pir.version))}>Close PIR</button>}
          </>)}</span></div>
      <p><span className={s.cls}>{s.g} {s.word}</span> <span className="muted">· {pir.requiredReason === 'Manual' ? 'started manually' : pir.requiredReason.replace('CloseCode=', 'close code ')}{pir.heldAt ? ` · held ${fmtDayTime(pir.heldAt)}` : ''}</span></p>
      {summary !== null ? (
        <div className="inline-form stack">
          <label>Summary<textarea className="line block" rows={3} value={summary} onChange={e => setSummary(e.target.value)} /></label>
          <span><button type="button" className="text" onClick={() => run(() => savePirSummary(train.id, summary, pir.version), () => setSummary(null))}>Save summary</button>{' '}
            <button type="button" className="text" onClick={() => setSummary(null)}>Cancel</button></span>
        </div>
      ) : (
        <p>{pir.summary ?? <span className="muted">No summary yet. Holding the review needs one.</span>}{canCloseout && !closed && <> <button type="button" className="text" onClick={() => setSummary(pir.summary ?? '')}>Edit summary</button></>}</p>)}

      <h3 className="cap">Actions</h3>
      {d.actions.length === 0 ? <p className="muted">No actions yet.</p> : (
        <table className="grid">
          <thead><tr><th>State</th><th>Action</th><th>Owner</th><th>Due</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{d.actions.map(a => {
            const late = !a.doneAt && a.dueOn < today
            return (
              <tr key={a.id}>
                <td className={a.doneAt ? 'ok nowrap' : late ? 'bad nowrap' : 'nowrap'}>{a.doneAt ? '✓ done' : late ? '✗ overdue' : '○ open'}</td>
                <td>{a.text}</td><td>{name(a.ownerUserId)}</td><td className="mono nowrap">{day(a.dueOn)}</td>
                <td>{!closed && (canCloseout || a.ownerUserId === meId) && (a.doneAt
                  ? <button type="button" className="text" onClick={() => run(() => pirActionState(a.id, 'reopen', a.version))}>Reopen</button>
                  : <button type="button" className="text" onClick={() => run(() => pirActionState(a.id, 'complete', a.version))}>Done</button>)}</td>
              </tr>)
          })}</tbody>
        </table>)}
      {canCloseout && !closed && (
        <p className="inline-form">
          <label>Action <input className="line wide" value={form.text} onChange={e => setForm({ ...form, text: e.target.value })} /></label>
          <label>Owner <select className="line" value={form.ownerId || owners[0]?.id || ''} onChange={e => setForm({ ...form, ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>
          <label>Due <input className="line" type="date" value={form.dueOn} min={today} onChange={e => setForm({ ...form, dueOn: e.target.value })} /></label>
          <button type="button" className="text" disabled={!form.text.trim() || !form.dueOn}
            onClick={() => run(() => addPirAction(train.id, { text: form.text, ownerUserId: form.ownerId || owners[0]?.id || '', dueOn: form.dueOn }), () => setForm({ text: '', ownerId: form.ownerId, dueOn: '' }))}>Add action</button>
        </p>)}
      <Errors err={err} />
    </section>
  )
}

// ---- known issues and hypercare ----------------------------------------------------------------------------------------
function IssuesSection({ train, refreshKey, onChanged, canCloseout, canAccept, canPlan }: SectionProps & { canCloseout: boolean; canAccept: boolean; canPlan: boolean }) {
  const [d, setD] = useState<KnownIssuesData | null>(null)
  const [form, setForm] = useState({ title: '', severity: 'Medium', workaround: '', externalKey: '' })
  const [edit, setEdit] = useState<{ id: string; title: string; severity: string; workaround: string; externalKey: string } | null>(null)
  const { err, run, setErr } = useDo(onChanged)
  useEffect(() => { getKnownIssues(train.id).then(setD).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!d) return <Errors err={err} />
  const ended = d.hypercareExitAt !== null
  const openCount = d.issues.filter(i => i.status === 'Open').length
  return (
    <section aria-label="Known issues and hypercare">
      <div className="section-head"><h2 className="cap dark">Known issues · hypercare</h2>
        <span className="muted">{train.status === 'Complete' && (ended
          ? <span className="ok">✓ Hypercare ended {fmtDayTime(d.hypercareExitAt!)}</span>
          : canPlan && <button type="button" className="text" onClick={() => run(() => exitHypercare(train.id, train.version))}>Exit hypercare</button>)}</span></div>
      {d.issues.length === 0 ? <p className="muted">No known issues logged.</p> : (
        <table className="grid">
          <thead><tr><th>Severity</th><th>Issue</th><th>State</th><th>Workaround</th><th>Reference</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{d.issues.map(i => {
            const sv = SEVERITY[i.severity], st = ISSUE_STATE[i.status]
            return (
              <tr key={i.id}>
                <td className={`${sv.cls} nowrap`}>{sv.g} {i.severity}</td><td>{i.title}</td><td className={`${st.cls} nowrap`}>{st.g} {i.status}</td>
                <td className="muted">{i.workaround ?? '—'}</td><td className="mono muted">{i.externalKey ?? '—'}</td>
                <td className="nowrap">{canCloseout && !ended && (
                  <>
                    {i.status === 'Open' && canAccept && <button type="button" className="text" onClick={() => run(() => knownIssueAction(train.id, i.id, 'accept', i.version))}>Accept</button>}{' '}
                    {i.status !== 'Resolved' && <button type="button" className="text" onClick={() => run(() => knownIssueAction(train.id, i.id, 'resolve', i.version))}>Resolve</button>}{' '}
                    {i.status !== 'Open' && <button type="button" className="text" onClick={() => run(() => knownIssueAction(train.id, i.id, 'reopen', i.version))}>Reopen</button>}{' '}
                    {i.status !== 'Resolved' && <button type="button" className="text" onClick={() => setEdit(edit?.id === i.id ? null : { id: i.id, title: i.title, severity: i.severity, workaround: i.workaround ?? '', externalKey: i.externalKey ?? '' })}>{edit?.id === i.id ? 'Cancel' : 'Edit'}</button>}
                  </>)}</td>
              </tr>)
          })}</tbody>
        </table>)}
      {edit && (
        <div className="inline-form stack">
          <label>Title <input className="line wide" value={edit.title} onChange={e => setEdit({ ...edit, title: e.target.value })} /></label>
          <label>Severity <select className="line" value={edit.severity} onChange={e => setEdit({ ...edit, severity: e.target.value })}>{SEVERITIES.map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Workaround <input className="line wide" value={edit.workaround} onChange={e => setEdit({ ...edit, workaround: e.target.value })} /></label>
          <label>Reference <input className="line" value={edit.externalKey} onChange={e => setEdit({ ...edit, externalKey: e.target.value })} /></label>
          <span><button type="button" className="text" onClick={() => run(() => patchKnownIssue(train.id, edit.id, { title: edit.title, severity: edit.severity, workaround: edit.workaround, externalKey: edit.externalKey }, d.issues.find(i => i.id === edit.id)!.version), () => setEdit(null))}>Save issue</button></span>
        </div>)}
      {openCount > 0 && !ended && train.status === 'Complete' && <p className="muted">{plural(openCount, 'issue')} still open. Critical and High issues must be resolved or accepted before hypercare ends.</p>}
      {canCloseout && !ended && (
        <p className="inline-form">
          <label>Issue <input className="line wide" value={form.title} onChange={e => setForm({ ...form, title: e.target.value })} /></label>
          <label>Severity <select className="line" value={form.severity} onChange={e => setForm({ ...form, severity: e.target.value })}>{SEVERITIES.map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Workaround <input className="line" value={form.workaround} onChange={e => setForm({ ...form, workaround: e.target.value })} /></label>
          <label>Reference <input className="line" value={form.externalKey} onChange={e => setForm({ ...form, externalKey: e.target.value })} /></label>
          <button type="button" className="text" disabled={!form.title.trim()}
            onClick={() => run(() => addKnownIssue(train.id, { title: form.title, severity: form.severity, workaround: form.workaround || null, externalKey: form.externalKey || null }), () => setForm({ title: '', severity: form.severity, workaround: '', externalKey: '' }))}>Log issue</button>
        </p>)}
      <Errors err={err} />
    </section>
  )
}
