import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, getStream, type Me, type UserRow } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { utcToZonedInput, zonedInputToUtc } from '../time.ts'
import { onCabChanged } from '../live.ts'
import { Button, ConfirmDialog, ConflictNotice, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, PageHeading, Panel, PropertyList, ReadOnlyNotice, StatusChip, Time, TimeRange } from '../ui'
import { Field, Problem, SelectInput } from '../mgmt/forms.tsx'
import { decideChange, getChange, getUsers, moveChange, updateChange, type ChangeRow } from './cabApi.ts'
import {
  SEPARATION_RULES, canDecide, canDecideNow, canPlan, decisionTone, formOf, isRetroactive, isSeparationGuard, patchBody, riskTone, riskWord, selfApprovalReason, statusTone, stepsFor, type ChangeForm,
} from './cabLogic.ts'
import { ChangeEditor, type Choice } from './ChangeEditor.tsx'
import { RiskPanel } from './RiskPanels.tsx'
import { ConflictsPanel } from './ConflictPanels.tsx'   // REOS-180
import { needsReason, reasonHint } from './conflictLogic.ts'
import type { Conflict as ChangeConflict } from './conflictsApi.ts'
import './cab.css'

interface Conflict { current: ChangeRow; redo: (version: number) => void }
interface Refusal { guard: string; message: string }

/**
 * One change request (REOS-176): its facts, why it has the risk level it has (every rule, applied or not, in words), its decisions and the moves open to this person. Everything that writes
 * carries the version it was loaded with (If-Match); a 409 says someone else changed it and offers their version or an overwrite, keeping what was typed. A refusal by a rule is shown
 * in the rule's own words; the three separation-of-duties rules are stated on the page before anyone runs into them.
 */
export function ChangeDetail({ me, id, onBack }: { me: Me; id: string; onBack: () => void }) {
  const [row, setRow] = useState<ChangeRow | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [missing, setMissing] = useState(false)
  const [editing, setEditing] = useState(false)
  const [editBase, setEditBase] = useState(0)   // the version the open edit started from: a live refresh does not move it, so a save over someone else's change is a 409, never a silent overwrite
  const [newer, setNewer] = useState(false)
  const [form, setForm] = useState<ChangeForm | null>(null)
  const [releases, setReleases] = useState<Choice[]>([])
  const [people, setPeople] = useState<UserRow[]>([])
  const [conflict, setConflict] = useState<Conflict | null>(null)
  const [refusal, setRefusal] = useState<Refusal | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<'cancel' | 'reject' | null>(null)
  const [decision, setDecision] = useState('Approved')
  const [rationale, setRationale] = useState('')
  const [conflicts, setConflicts] = useState<ChangeConflict[]>([])   // REOS-180: what the change collides with; approving over any of it needs a reason
  const { run, pending } = useAction()
  const editOpener = useRef<HTMLButtonElement>(null)

  const load = useCallback(() => {
    getChange(id).then(r => { setRow(r); setError(null); setMissing(false) })
      .catch(e => { if (e instanceof ApiError && e.status === 404) setMissing(true); else setError(errMsg(e)) })
  }, [id])
  useEffect(() => { load() }, [load])
  const editingRef = useRef(false)
  useEffect(() => { editingRef.current = editing }, [editing])
  const refocus = useRef(false)   // set when an edit closes: the Edit button is focused once React has drawn it again (a single animation frame can fire before that on a slow machine)
  useEffect(() => { if (!editing && refocus.current && editOpener.current) { refocus.current = false; editOpener.current.focus() } }, [editing, row])
  useEffect(() => onCabChanged(changeId => { if (changeId && changeId !== id) return; if (editingRef.current) setNewer(true); else load() }), [load, id])
  useEffect(() => {
    if (!editing) return
    getStream().then(r => setReleases(r.map(t => ({ id: t.id, title: t.title })))).catch(() => setReleases([]))
    getUsers().then(setPeople).catch(() => setPeople([]))
  }, [editing])

  if (missing) return <div className="os-page"><PageHeading trail={['Workspace', 'Change advisory board', 'Not found']} title="Change request not found" /><EmptyState title="No such change request" action={<Button onClick={onBack}>Back to the list</Button>}>It may have been mistyped, or it belongs to another workspace.</EmptyState></div>
  if (error && !row) return <div className="os-page"><ErrorState what="The change request" message={error} onRetry={load} /></div>
  if (!row) return <div className="os-page"><LoadingState what="the change request" /></div>

  const plan = canPlan(me), mayDecide = canDecide(me)
  const self = selfApprovalReason(row, me)
  const verdict = decision === 'Approved' || decision === 'Rejected'
  const retro = isRetroactive(row)

  /** Runs one write with the version it should carry; names what happened to the person in words (409, a rule, anything else). */
  const attempt = (label: string, fn: (version: number) => Promise<ChangeRow>, version = row.version, done?: (r: ChangeRow) => string, onOk?: () => void): Promise<ChangeRow | undefined> =>
    run(label, async () => {
      setRefusal(null); setConflict(null); setProblem(null)
      try {
        const r = await fn(version)
        setRow(r); setConfirm(null)
        if (done) announce(done(r))
        onOk?.()
        return r
      } catch (e) {
        setConfirm(null)
        if (e instanceof ApiError && e.status === 409 && e.body?.current) setConflict({ current: e.body.current as ChangeRow, redo: v => void attempt(label, fn, v, done, onOk) })
        else if (e instanceof ApiError && e.status === 422) setRefusal({ guard: e.body?.guard ?? '', message: e.body?.message ?? 'The change was refused.' })
        else setProblem(errMsg(e))
        return undefined
      }
    })

  const startEdit = () => { setForm(formOf(row, utcToZonedInput)); setEditBase(row.version); setNewer(false); setEditing(true) }
  const backToEdit = () => { refocus.current = true; requestAnimationFrame(() => { if (editOpener.current) { refocus.current = false; editOpener.current.focus() } }) }   // the Edit button is drawn again once the form closes
  const closeEdit = () => { setEditing(false); setNewer(false); backToEdit() }
  const save = async () => {
    if (!form) return
    const body = patchBody(form, row, zonedInputToUtc, utcToZonedInput)
    if (Object.keys(body).length === 0) { closeEdit(); return }
    await attempt('Saving…', v => updateChange(row.id, body, v), editBase, c => `${c.key} saved. Risk is ${c.riskLevel}`, closeEdit)
  }
  const move = (action: 'submit' | 'cancel' | 'implement' | 'close') => attempt('Working…', v => moveChange(row.id, action, v), row.version, c => `${c.key} is now ${c.status}. Risk is ${c.riskLevel}`)
  const decide = () => attempt('Recording…', v => decideChange(row.id, { decision, rationale: rationale.trim() || null, retroactive: retro }, v), row.version, c => `${decision} recorded on ${c.key}`)
    .then(r => { if (r) setRationale('') })

  const steps = plan ? stepsFor(row) : []
  const chips = (
    <div className="cab-chips">
      <StatusChip tone={statusTone(row.status)}>{row.status}</StatusChip>
      <StatusChip tone="neutral">{`${row.changeType} change`}</StatusChip>
      <StatusChip tone={riskTone(row.riskLevel)}>{riskWord(row.riskLevel)}</StatusChip>
    </div>
  )
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Change advisory board', row.key]} title={row.title} description={<>{row.key} · requested by {row.requestedBy.name}</>} actions={<Button onClick={onBack}>All changes</Button>} />
      {chips}
      {!plan && !mayDecide && <ReadOnlyNotice>You can read this change request. An RTE or Release Manager edits and moves it.</ReadOnlyNotice>}
      {conflict && <ConflictNotice at={conflict.current.lastChangedAt ? new Date(conflict.current.lastChangedAt).toISOString().replace('T', ' ').slice(0, 16) + ' UTC' : undefined}
        onUseTheirs={() => { setRow(conflict.current); setConflict(null); setEditing(false); setNewer(false); backToEdit() }}
        onOverwrite={() => { const c = conflict; setRow(c.current); c.redo(c.current.version) }} />}
      {refusal && (
        <Notice tone="danger" title={isSeparationGuard(refusal.guard) ? 'Refused by a separation-of-duties rule' : 'Not allowed'} live>
          {refusal.message}{refusal.guard && <> <span className="cab-mono">({refusal.guard})</span></>}
        </Notice>
      )}
      <Problem message={problem} />
      {editing && newer && <Notice tone="warning" title="This change request changed while you were editing" live>Someone saved a newer version. Your changes are still here; saving will ask you whether to use theirs or overwrite.</Notice>}
      {row.needsReview && <Notice tone="warning" title="Waiting for a second person's review">This Emergency change has been implemented. It cannot be closed until someone other than the requester records a review.</Notice>}
      <div className="mg-split">
        <div className="mg-side">
          {editing && form && (
            <ChangeEditor title="Edit change request" subtitle="A Draft can be edited; the risk rules run again on every save" form={form} onChange={setForm} releases={releases} people={people} canPickRequester={false}
              saveLabel="Save changes" pendingLabel="Saving…" pending={pending === 'Saving…'} problem={null}
              onSave={() => void save()} onCancel={closeEdit} />
          )}
          <RiskPanel risk={row.risk} level={row.riskLevel} legacy={row.riskFactors} />
          <ConflictsPanel changeId={row.id} version={row.version} onLoaded={setConflicts} />
          <Panel title="Decisions" subtitle="Each person's decision is kept as it was recorded">
            {row.decisions.length === 0 ? <p className="mg-note">No decision has been recorded.</p> : row.decisions.map(d => (
              <div key={d.id} className="cab-decision">
                <div className="cab-decision-head"><StatusChip tone={decisionTone(d.decision)}>{d.decision}</StatusChip>{d.retroactive && <StatusChip tone="info">Retroactive review</StatusChip>}<span>{d.decidedBy.name}</span><Time at={d.decidedAt} date /></div>
                {d.rationale && <p className="cab-pre">{d.rationale}</p>}
              </div>
            ))}
          </Panel>
        </div>
        <div className="mg-side">
          <Panel title="Change details">
            <PropertyList items={[
              { label: 'Type', value: row.changeType },
              { label: 'Status', value: row.status },
              { label: 'Requested by', value: row.requestedBy.name },
              ...(row.createdBy.id !== row.requestedBy.id ? [{ label: 'Keyed in by', value: row.createdBy.name }] : []),
              { label: 'Linked release', value: row.release?.title ?? 'None' },
              { label: 'Go/No-Go', value: row.goNoGo ? <span>{row.goNoGo.decision === 'NoGo' ? 'No-Go' : row.goNoGo.decision}, recorded <Time at={row.goNoGo.decidedAt} date /></span> : 'None linked' },
              { label: 'Planned window', value: row.plannedStartAt && row.plannedEndAt ? <TimeRange from={row.plannedStartAt} to={row.plannedEndAt} /> : 'Not set' },
              { label: 'Rollback note', value: row.rollbackNote ? <span className="cab-pre">{row.rollbackNote}</span> : 'None recorded' },
              ...(row.description ? [{ label: 'Description', value: <span className="cab-pre">{row.description}</span> }] : []),
              ...(row.submittedAt ? [{ label: 'Submitted', value: <Time at={row.submittedAt} date /> }] : []),
              ...(row.decidedAt ? [{ label: `${row.status === 'Rejected' ? 'Rejected' : 'Approved'} by`, value: <span>{row.decidedBy?.name} · <Time at={row.decidedAt} date /></span> }] : []),
              ...(row.implementedAt ? [{ label: 'Implemented', value: <Time at={row.implementedAt} date /> }] : []),
              ...(row.closedAt ? [{ label: 'Closed', value: <Time at={row.closedAt} date /> }] : []),
              { label: 'Last changed', value: row.lastChangedAt ? <Time at={row.lastChangedAt} date /> : 'Never' },
              { label: 'Version', value: String(row.version), mono: true },
            ]} />
          </Panel>
          {(steps.length > 0 || (plan && row.status === 'Draft')) && (
            <Panel title="Actions" subtitle={row.status === 'Draft' ? 'Edit, then submit it to the CAB' : undefined}>
              <div className="mg-actions">
                {plan && row.status === 'Draft' && !editing && <Button ref={editOpener} onClick={startEdit}>Edit details</Button>}
                {steps.map(s => s.action === 'cancel'
                  ? <Button key={s.action} variant="destructive" disabled={!!pending} onClick={() => setConfirm('cancel')}>{s.label}</Button>
                  : <Button key={s.action} variant={s.action === 'submit' ? 'primary' : 'secondary'} disabled={!!pending} onClick={() => void move(s.action)}>{s.label}</Button>)}
              </div>
              {row.status === 'Draft' && <p className="mg-note">Submitting needs a rollback note{row.changeType === 'Emergency' ? '' : ' and a planned window'}. The risk rules run again when you submit.</p>}
            </Panel>
          )}
          {mayDecide && canDecideNow(row) && (
            <Panel title={retro ? 'Record a review' : 'Record a decision'} subtitle={retro ? 'A second person reviews an Emergency change after it was implemented' : 'The CAB approves, rejects or defers'}>
              <div className="mg-form">
                <Field label="Decision"><SelectInput value={decision} onChange={e => setDecision(e.target.value)} options={[{ value: 'Approved', label: retro ? 'Approved (review passed)' : 'Approved' }, { value: 'Rejected', label: retro ? 'Rejected (review failed)' : 'Rejected' }, ...(retro ? [] : [{ value: 'Deferred', label: 'Deferred' }])]} /></Field>
                <Field label="Rationale" hint={decision === 'Rejected' ? 'Required for a rejection.' : decision === 'Approved' && !retro && conflicts.length > 0 ? 'Required: why you are approving over the conflicts shown (at least 20 characters).' : 'Optional.'}><textarea className="mg-input" rows={3} value={rationale} maxLength={2000} onChange={e => setRationale(e.target.value)} /></Field>
                <div className="mg-actions">
                  {verdict && self ? <DisabledAction variant="primary" reason={self}>{retro ? 'Record review' : 'Record decision'}</DisabledAction>
                    : !retro && needsReason(decision, conflicts, rationale) ? <DisabledAction variant="primary" reason={reasonHint(conflicts)}>Record decision</DisabledAction>
                    : decision === 'Rejected' && !rationale.trim() ? <DisabledAction variant="primary" reason="A rejection needs a rationale.">{retro ? 'Record review' : 'Record decision'}</DisabledAction>
                    : decision === 'Rejected' ? <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm('reject')}>{retro ? 'Record review' : 'Record decision'}</Button>
                    : <Button variant="primary" disabled={!!pending} onClick={() => void decide()}>{pending === 'Recording…' ? pending : retro ? 'Record review' : 'Record decision'}</Button>}
                </div>
              </div>
            </Panel>
          )}
          <Panel title="Separation of duties" subtitle="Three rules, checked by the service and again by the database">
            <ol className="cab-rules">{SEPARATION_RULES.map(r => <li key={r}>{r}</li>)}</ol>
            {self && row.status === 'Submitted' && <p className="mg-note" data-testid="self-requester">{self}</p>}
          </Panel>
        </div>
      </div>
      <ConfirmDialog open={confirm === 'cancel'} title="Cancel this change request?" confirmLabel="Cancel change" pendingLabel="Cancelling…" destructive pending={pending === 'Working…'}
        onConfirm={() => void move('cancel')} onCancel={() => setConfirm(null)}>
        {row.key} is kept for the record, but it cannot be moved again once cancelled.
      </ConfirmDialog>
      <ConfirmDialog open={confirm === 'reject'} title={retro ? 'Record a failed review?' : 'Reject this change request?'} confirmLabel={retro ? 'Record review' : 'Reject change'} pendingLabel="Recording…" destructive pending={pending === 'Recording…'}
        onConfirm={() => void decide()} onCancel={() => setConfirm(null)}>
        {retro ? 'The review is recorded as Rejected.' : `${row.key} moves to Rejected, which is final.`} Your rationale is kept with the decision.
      </ConfirmDialog>
    </div>
  )
}
