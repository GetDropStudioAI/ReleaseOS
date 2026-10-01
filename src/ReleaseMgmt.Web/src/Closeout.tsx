import { useEffect, useId, useRef, useState } from 'react'
import {
  addKnownIssue, addPirAction, attestRollback, createPir, exitHypercare, getAttestation, getKnownIssues, getMe, getOwners, getPir,
  knownIssueAction, patchKnownIssue, pirAction, pirActionState, savePirSummary,
  type KnownIssue, type KnownIssuesData, type Me, type Owner, type Pir, type PirViewData, type RollbackAttestation, type TrainDetail,
} from './api'
import { day, errMsg, plural } from './format'
import { fmtDayTime, serverNow } from './time'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { announce } from './announce'
import { useDraft } from './session'

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

/**
 * Run an action; on any failure show the message and refetch (a 409 means someone else saved first).
 * One action at a time per section: a double click is ignored and `pending` names the running action for its button.
 */
function useDo(onChanged: () => void) {
  const [err, setErr] = useState<string | null>(null)
  const { run: guard, pending } = useAction()
  const run = (label: string, fn: () => Promise<unknown>, after?: () => void, done?: string) => guard(label, async () => {
    setErr(null)
    try { await fn(); after?.(); if (done) announce(done); onChanged() } catch (e) { setErr(errMsg(e)); onChanged() }
  })
  return { err, run, setErr, pending }
}
const later = (f: () => void) => requestAnimationFrame(f)

// ---- rollback attestation --------------------------------------------------------------------------------------------
function RollbackSection({ train, refreshKey, onChanged, name, canPlan }: SectionProps & { canPlan: boolean }) {
  const [a, setA] = useState<RollbackAttestation | null>(null)
  const [draft, setDraft] = useDraft<{ runId: string; note: string }>(`attest:${train.id}`)   // undefined = form closed; survives navigation
  const { err, run, setErr, pending } = useDo(onChanged)
  const formId = useId()
  const toggle = useRef<HTMLButtonElement>(null)
  const first = useRef<HTMLSelectElement>(null)
  useEffect(() => { getAttestation(train.id).then(setA).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!a) return <Errors err={err} />
  const closed = train.status === 'Complete'
  return (
    <section aria-label="Rollback rehearsal">
      <div className="section-head"><h2 className="cap dark">Rollback rehearsal</h2>
        <span className="muted">{canPlan && !closed && <button ref={toggle} type="button" className="text" aria-expanded={!!draft} aria-controls={draft ? formId : undefined} disabled={!!pending}
          onClick={() => { if (draft) setDraft(undefined); else { setDraft({ runId: '', note: '' }); later(() => first.current?.focus()) } }}>{draft ? 'Cancel' : a.rehearsedAt ? 'Attest again' : 'Attest rehearsal'}</button>}</span></div>
      {a.rehearsedAt
        ? <p><span className="ok">✓ Rehearsed</span> · <span className="mono">{fmtDayTime(a.rehearsedAt)}</span> · attested by {a.rehearsedByName ?? name(a.rehearsedByUserId)}</p>
        : a.required
          ? <p><span className="bad">✗ Not attested</span> <span className="muted">· a {train.riskTier} train needs a rehearsed rollback before Executing</span></p>
          : <p><span className="muted">○ Not attested · optional at {train.riskTier} risk</span></p>}
      {draft && !closed && (
        <div className="inline-form stack" id={formId}>
          <label>Evidence run <select ref={first} className="line" value={draft.runId} onChange={e => setDraft({ ...draft, runId: e.target.value })}>
            <option value="">None linked</option>
            {a.rehearsalRuns.map(r => <option key={r.id} value={r.id}>{fmtDayTime(r.startedAt)} · {r.outcome ?? 'ended'}</option>)}</select></label>
          <label>What was rehearsed <input className="line wide" value={draft.note} onChange={e => setDraft({ ...draft, note: e.target.value })} /></label>
          <span><button type="button" className="text" disabled={!!pending} onClick={() => run('Recording…', () => attestRollback(train.id, draft.runId || null, draft.note || null, train.version),
            () => { setDraft(undefined); later(() => toggle.current?.focus()) }, 'Rollback rehearsal attested.')}>{pending ?? 'Record attestation'}</button>{' '}
            <span className="muted">You attest that the rollback section was rehearsed and works.</span></span>
        </div>)}
      <Errors err={err} />
    </section>
  )
}

// ---- PIR ---------------------------------------------------------------------------------------------------------------
function PirSection({ train, refreshKey, onChanged, name, owners, canCloseout, meId }: SectionProps & { canCloseout: boolean; meId: string | null }) {
  const [d, setD] = useState<PirViewData | null>(null)
  const [summaryDraft, setSummaryDraft] = useDraft<string>(`pirSummary:${train.id}`)   // undefined = not editing; drafts survive navigation
  const summary = summaryDraft ?? null
  const [formDraft, setForm] = useDraft<{ text: string; ownerId: string; dueOn: string }>(`pirAction:${train.id}`)
  const form = formDraft ?? { text: '', ownerId: '', dueOn: '' }
  const { err, run, setErr, pending } = useDo(onChanged)
  const summaryBox = useRef<HTMLTextAreaElement>(null)
  const editBtn = useRef<HTMLButtonElement>(null)
  const actionInput = useRef<HTMLInputElement>(null)
  const setSummary = (v: string | null) => {
    setSummaryDraft(v ?? undefined)
    later(() => (v === null ? editBtn.current : summaryBox.current)?.focus())
  }
  useEffect(() => { getPir(train.id).then(setD).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!d) return <Errors err={err} />
  const pir = d.pir
  if (!pir) {
    return (
      <section aria-label="Post-implementation review">
        <div className="section-head"><h2 className="cap dark">Post-implementation review</h2>
          <span className="muted">{canCloseout && <button type="button" className="text" disabled={!!pending} onClick={() => run('Starting…', () => createPir(train.id), undefined, 'Post-implementation review started.')}>{pending ?? 'Start a PIR'}</button>}</span></div>
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
            {pir.status === 'Required' && <button type="button" className="text" disabled={!!pending} onClick={() => run('Scheduling…', () => pirAction(train.id, 'schedule', pir.version), undefined, 'PIR marked scheduled.')}>{pending === 'Scheduling…' ? pending : 'Mark scheduled'}</button>}
            {pir.status !== 'Held' && <> <button type="button" className="text" disabled={!!pending} onClick={() => run('Marking held…', () => pirAction(train.id, 'hold', pir.version, summary ?? undefined), () => setSummaryDraft(undefined), 'PIR marked held.')}>{pending === 'Marking held…' ? pending : 'Mark held'}</button></>}
            {pir.status === 'Held' && <ConfirmInline label="Close PIR" destructive={false} disabled={!!pending} confirmLabel="Close review" pendingLabel="Closing…"
              question={openActions > 0 ? `Close the review with ${plural(openActions, 'action')} still open?` : 'Close the review? It cannot be reopened.'}
              onConfirm={() => run('Closing…', () => pirAction(train.id, 'close', pir.version), undefined, 'PIR closed.')} />}
          </>)}</span></div>
      <p><span className={s.cls}>{s.g} {s.word}</span> <span className="muted">· {pir.requiredReason === 'Manual' ? 'started manually' : pir.requiredReason.replace('CloseCode=', 'close code ')}{pir.heldAt ? ` · held ${fmtDayTime(pir.heldAt)}` : ''}</span>
        {canCloseout && pir.status === 'Held' && openActions > 0 && <span className="warn"> · <span aria-hidden="true">▲ </span>{plural(openActions, 'action')} still open</span>}</p>
      {summary !== null ? (
        <div className="inline-form stack">
          <label>Summary<textarea ref={summaryBox} className="line block" rows={3} value={summary} onChange={e => setSummaryDraft(e.target.value)} /></label>
          <span><button type="button" className="text" disabled={!!pending} onClick={() => run('Saving…', () => savePirSummary(train.id, summary, pir.version), () => setSummary(null), 'Summary saved.')}>{pending === 'Saving…' ? pending : 'Save summary'}</button>{' '}
            <button type="button" className="text" disabled={!!pending} onClick={() => setSummary(null)}>Cancel</button></span>
        </div>
      ) : (
        <p>{pir.summary ?? <span className="muted">No summary yet. Holding the review needs one.</span>}{canCloseout && !closed && <> <button ref={editBtn} type="button" className="text" onClick={() => setSummary(pir.summary ?? '')}>Edit summary</button></>}</p>)}

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
                  ? <button type="button" className="text" disabled={!!pending} onClick={() => run('Reopening…', () => pirActionState(a.id, 'reopen', a.version), undefined, 'Action reopened.')}>Reopen</button>
                  : <button type="button" className="text" disabled={!!pending} onClick={() => run('Marking done…', () => pirActionState(a.id, 'complete', a.version), undefined, 'Action done.')}>Done</button>)}</td>
              </tr>)
          })}</tbody>
        </table>)}
      {canCloseout && !closed && (
        <p className="inline-form">
          <label>Action <input ref={actionInput} className="line wide" value={form.text} onChange={e => setForm({ ...form, text: e.target.value })} /></label>
          <label>Owner <select className="line" value={form.ownerId || owners[0]?.id || ''} onChange={e => setForm({ ...form, ownerId: e.target.value })}>{owners.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select></label>
          <label>Due <input className="line" type="date" value={form.dueOn} min={today} onChange={e => setForm({ ...form, dueOn: e.target.value })} /></label>
          <button type="button" className="text" disabled={!form.text.trim() || !form.dueOn || !!pending}
            onClick={() => run('Adding…', () => addPirAction(train.id, { text: form.text, ownerUserId: form.ownerId || owners[0]?.id || '', dueOn: form.dueOn }),
              () => { setForm(form.ownerId ? { text: '', ownerId: form.ownerId, dueOn: '' } : undefined); actionInput.current?.focus() }, 'Action added.')}>{pending === 'Adding…' ? pending : 'Add action'}</button>
        </p>)}
      <Errors err={err} />
    </section>
  )
}

// ---- known issues and hypercare ----------------------------------------------------------------------------------------
function IssuesSection({ train, refreshKey, onChanged, canCloseout, canAccept, canPlan }: SectionProps & { canCloseout: boolean; canAccept: boolean; canPlan: boolean }) {
  const [d, setD] = useState<KnownIssuesData | null>(null)
  const [formDraft, setForm] = useDraft<{ title: string; severity: string; workaround: string; externalKey: string }>(`issue:${train.id}`)   // drafts survive navigation
  const form = formDraft ?? { title: '', severity: 'Medium', workaround: '', externalKey: '' }
  const [editDraft, setEditDraft] = useDraft<{ id: string; title: string; severity: string; workaround: string; externalKey: string }>(`issueEdit:${train.id}`)
  const edit = editDraft ?? null
  const { err, run, setErr, pending } = useDo(onChanged)
  const editId = useId()
  const editBox = useRef<HTMLInputElement>(null)
  const editOpener = useRef<HTMLElement | null>(null)
  const issueInput = useRef<HTMLInputElement>(null)
  const setEdit = (v: typeof edit, from?: HTMLElement) => {
    if (from) editOpener.current = from
    setEditDraft(v ?? undefined)
    later(() => { const back = editOpener.current; if (v) editBox.current?.focus(); else if (back?.isConnected) back.focus() })
  }
  useEffect(() => { getKnownIssues(train.id).then(setD).catch(e => setErr(errMsg(e))) }, [train.id, refreshKey, setErr])
  if (!d) return <Errors err={err} />
  const ended = d.hypercareExitAt !== null
  const openCount = d.issues.filter(i => i.status === 'Open').length
  return (
    <section aria-label="Known issues and hypercare">
      <div className="section-head"><h2 className="cap dark">Known issues · hypercare</h2>
        <span className="muted">{train.status === 'Complete' && (ended
          ? <span className="ok">✓ Hypercare ended {fmtDayTime(d.hypercareExitAt!)}</span>
          : canPlan && <ConfirmInline label="Exit hypercare" destructive={false} disabled={!!pending} confirmLabel="End hypercare" pendingLabel="Ending…"
              question={openCount > 0 ? `End hypercare with ${plural(openCount, 'issue')} still open?` : 'End hypercare? Known issues can no longer be logged.'}
              onConfirm={() => run('Ending…', () => exitHypercare(train.id, train.version), undefined, 'Hypercare ended.')} />)}</span></div>
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
                    {i.status === 'Open' && canAccept && <button type="button" className="text" disabled={!!pending} onClick={() => run('Accepting…', () => knownIssueAction(train.id, i.id, 'accept', i.version), undefined, `${i.title} accepted.`)}>Accept</button>}{' '}
                    {i.status !== 'Resolved' && <button type="button" className="text" disabled={!!pending} onClick={() => run('Resolving…', () => knownIssueAction(train.id, i.id, 'resolve', i.version), undefined, `${i.title} resolved.`)}>Resolve</button>}{' '}
                    {i.status !== 'Open' && <button type="button" className="text" disabled={!!pending} onClick={() => run('Reopening…', () => knownIssueAction(train.id, i.id, 'reopen', i.version), undefined, `${i.title} reopened.`)}>Reopen</button>}{' '}
                    {i.status !== 'Resolved' && <button type="button" className="text" aria-expanded={edit?.id === i.id} aria-controls={edit?.id === i.id ? editId : undefined}
                      onClick={e => setEdit(edit?.id === i.id ? null : { id: i.id, title: i.title, severity: i.severity, workaround: i.workaround ?? '', externalKey: i.externalKey ?? '' }, e.currentTarget)}>{edit?.id === i.id ? 'Cancel' : 'Edit'}</button>}
                  </>)}</td>
              </tr>)
          })}</tbody>
        </table>)}
      {edit && d.issues.some(i => i.id === edit.id) && (
        <div className="inline-form stack" id={editId} role="group" aria-label="Edit issue">
          <label>Title <input ref={editBox} className="line wide" value={edit.title} onChange={e => setEditDraft({ ...edit, title: e.target.value })} /></label>
          <label>Severity <select className="line" value={edit.severity} onChange={e => setEditDraft({ ...edit, severity: e.target.value })}>{SEVERITIES.map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Workaround <input className="line wide" value={edit.workaround} onChange={e => setEditDraft({ ...edit, workaround: e.target.value })} /></label>
          <label>Reference <input className="line" value={edit.externalKey} onChange={e => setEditDraft({ ...edit, externalKey: e.target.value })} /></label>
          <span><button type="button" className="text" disabled={!!pending} onClick={() => run('Saving…', () => patchKnownIssue(train.id, edit.id, { title: edit.title, severity: edit.severity, workaround: edit.workaround, externalKey: edit.externalKey }, d.issues.find(i => i.id === edit.id)!.version), () => setEdit(null), 'Issue saved.')}>{pending === 'Saving…' ? pending : 'Save issue'}</button>{' '}
            <button type="button" className="text" disabled={!!pending} onClick={() => setEdit(null)}>Cancel</button></span>
        </div>)}
      {openCount > 0 && !ended && train.status === 'Complete' && <p className="muted">{plural(openCount, 'issue')} still open. Critical and High issues must be resolved or accepted before hypercare ends.</p>}
      {canCloseout && !ended && (
        <p className="inline-form">
          <label>Issue <input ref={issueInput} className="line wide" value={form.title} onChange={e => setForm({ ...form, title: e.target.value })} /></label>
          <label>Severity <select className="line" value={form.severity} onChange={e => setForm({ ...form, severity: e.target.value })}>{SEVERITIES.map(s => <option key={s}>{s}</option>)}</select></label>
          <label>Workaround <input className="line" value={form.workaround} onChange={e => setForm({ ...form, workaround: e.target.value })} /></label>
          <label>Reference <input className="line" value={form.externalKey} onChange={e => setForm({ ...form, externalKey: e.target.value })} /></label>
          <button type="button" className="text" disabled={!form.title.trim() || !!pending}
            onClick={() => run('Logging…', () => addKnownIssue(train.id, { title: form.title, severity: form.severity, workaround: form.workaround || null, externalKey: form.externalKey || null }),
              () => { setForm(form.severity !== 'Medium' ? { title: '', severity: form.severity, workaround: '', externalKey: '' } : undefined); issueInput.current?.focus() }, 'Issue logged.')}>{pending === 'Logging…' ? pending : 'Log issue'}</button>
        </p>)}
      <Errors err={err} />
    </section>
  )
}
