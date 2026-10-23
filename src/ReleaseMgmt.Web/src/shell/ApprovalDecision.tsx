import { useCallback, useEffect, useId, useState, type ReactNode } from 'react'
import { ShieldCheck, ShieldX, FileText } from 'lucide-react'
import {
  ApiError, approveWaiver, closeCondition, gateAction, getDecisions, getFreezes, getGate, getTrain, getWaivers, getWindow, grantOverride, recordDecision, get,
  type Condition, type DecisionView, type FreezeWindow, type GateDetail, type TrainDetail, type Waiver, type WindowRow,
} from '../api'
import { Button, ConflictNotice, DisabledAction, ErrorState, LoadingState, Notice, PropertyList, StatusChip, TwoLine, chipTone } from '../ui'
import { KIND, dueText, type ApprovalItem } from './approvalsApi.ts'
import { noteProblem, parseOverrideId, shortRef } from './approvalsLogic.ts'

/**
 * The decision pane of the Approvals screen (REOS-99, Figma 6:5838): what is being decided, the release it belongs to, the evidence that exists, the checks
 * that decide whether the person may act, a decision note where the API takes one, and the actions. Every action is the existing endpoint for that kind
 * (certify gate, approve waiver, grant override, record Go/No-Go, close condition); nothing here writes a status itself. A rule the server enforces is
 * shown before the person clicks: the action is disabled with its reason beside it (ui DisabledAction).
 */
interface Evidence { id: string; fileName: string; sizeBytes: number; sha256: string; uploadedByName: string | null }
type Ctx = {
  train: TrainDetail; window: WindowRow | null; evidence: Evidence[]
  gate?: GateDetail; waiver?: Waiver; freeze?: FreezeWindow; decisions?: DecisionView[]; condition?: Condition
}

async function load(item: ApprovalItem): Promise<Ctx> {
  const [train, window] = await Promise.all([getTrain(item.trainId), getWindow(item.trainId)])
  const ctx: Ctx = { train, window, evidence: [] }
  const gateId = item.kind === 'CertifyGate' ? item.id : item.kind === 'ApproveWaiver' ? item.gateId ?? undefined : undefined
  if (gateId) {
    ctx.gate = await getGate(gateId)
    ctx.evidence = await get<Evidence[]>(`/api/v1/trains/${item.trainId}/attachments?entityType=Gate&entityId=${encodeURIComponent(gateId)}`).catch(() => [])
  }
  if (item.kind === 'ApproveWaiver' && gateId) {
    ctx.waiver = (await getWaivers(gateId)).find(w => w.id === item.id)
    if (!ctx.waiver) throw new Error('This waiver has already been decided.')
  }
  if (item.kind === 'GrantOverride') {
    const ids = parseOverrideId(item.id)
    ctx.freeze = (await getFreezes(item.trainId)).map(f => f.window).find(w => w.id === ids?.windowId)
    if (!ctx.freeze) throw new Error('This freeze window no longer exists.')
  }
  if (item.kind === 'RecordGoNoGo' || item.kind === 'CloseCondition') {
    ctx.decisions = await getDecisions(item.trainId)
    if (item.kind === 'CloseCondition') {
      ctx.condition = ctx.decisions.flatMap(d => d.conditions).find(c => c.id === item.id)
      if (!ctx.condition) throw new Error('This condition has already been closed.')
    }
  }
  return ctx
}

const fmtSize = (n: number) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`)

function When({ iso }: { iso: string }) {
  const t = dueText({ dueAt: iso, dueOn: null })
  return <>{t.utc}{t.local && <span className="os-muted"> ({t.local})</span>}</>
}

/** "Today, 15:00–16:00 UTC" in the design; the date is always written out, with the person's own time beside it. */
function WindowText({ w }: { w: WindowRow | null }) {
  if (!w) return <span className="os-muted">No deployment window set</span>
  const s = dueText({ dueAt: w.startsAt, dueOn: null }), e = dueText({ dueAt: w.endsAt, dueOn: null })
  return <>{s.utc.replace(' UTC', '')} to {e.utc}{s.local && <span className="os-muted"> ({s.local} to {e.local})</span>}</>
}

function Check({ ok, children }: { ok: boolean; children: ReactNode }) {
  const Icon = ok ? ShieldCheck : ShieldX
  return <li className={`ap-check ${ok ? 'met' : 'unmet'}`}><Icon aria-hidden="true" size={16} strokeWidth={1.6} /><span><span className="sr-only">{ok ? 'Passed: ' : 'Not met: '}</span>{children}</span></li>
}

export function DecisionPane({ item, onDecided, onOpen }: { item: ApprovalItem; onDecided: (what: string) => void; onOpen: (trainId: string, gateId?: string) => void }) {
  const [ctx, setCtx] = useState<Ctx | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState('')
  const [expiry, setExpiry] = useState('')
  const [shown, setShown] = useState(false)   // the note's validation message is shown after a first attempt, not while the person is still typing
  const [pending, setPending] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)
  const noteId = useId(), noteHint = useId(), expiryId = useId()
  const key = `${item.kind}:${item.id}`

  const reload = useCallback(() => {
    setCtx(null); setError(null)
    load(item).then(c => {
      setCtx(c)
      if (item.kind === 'GrantOverride' && c.freeze) setExpiry(c.freeze.endsAt.slice(0, 16))   // proposed: the freeze's end, in UTC
    }).catch((e: Error) => setError(e.message))
  }, [item]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { setNote(''); setShown(false); setFailure(null); setConflict(false); reload() }, [key]) // eslint-disable-line react-hooks/exhaustive-deps

  const run = async (label: string, done: string, fn: () => Promise<unknown>) => {
    if (pending) return
    setPending(label); setFailure(null); setConflict(false)
    try { await fn(); onDecided(done) }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) setConflict(true)
      else setFailure(e instanceof ApiError ? e.body?.message ?? e.message : (e as Error).message)
    } finally { setPending(null) }
  }

  if (error) return <ErrorState what="This decision" message={error} onRetry={reload} />
  if (!ctx) return <LoadingState what="the decision" />

  const { train, gate, waiver, freeze, decisions, condition } = ctx
  const ids = shortRef(item)
  const due = dueText(item)
  const overrideNote = noteProblem('override', note), nogoNote = noteProblem('nogo', note)

  // The checks the design calls "policy checks". Only rules this app has: a gate's certify eligibility, the separation of duties the queue already applied.
  const checks: { ok: boolean; text: string }[] = []
  if (gate) checks.push({ ok: gate.certify.eligible || item.kind === 'ApproveWaiver', text: item.kind === 'CertifyGate' ? 'Ready to certify (tasks done, earlier gates passed)' : 'Gate is open' })
  checks.push({ ok: true, text: 'Independent approver (you did not request or work on this)' })
  if (train.status === 'Complete' || train.status === 'Aborted') checks.push({ ok: false, text: `The release is ${train.status}` })

  let actions: ReactNode
  let noteField: ReactNode = null
  const noNote = <p className="os-muted ap-hint">This decision takes no note: the record carries who decided, when and the version they saw.</p>
  const declineReason = 'Declining is only available for Go/No-Go today (as No-Go). Request changes needs REOS-113.'
  const decline = <DisabledAction reason={declineReason}>Request changes</DisabledAction>

  if (item.kind === 'CertifyGate' && gate) {
    const why = gate.certify.reasons.join(' ') || 'This gate cannot be certified yet.'
    actions = <>{decline}{gate.certify.eligible
      ? <Button variant="primary" icon={ShieldCheck} disabled={!!pending} onClick={() => run('certify', 'Gate certified', () => gateAction(gate.id, 'certify', gate.version))}>{pending ? 'Recording…' : 'Certify & record'}</Button>
      : <DisabledAction variant="primary" icon={ShieldCheck} reason={why}>Certify & record</DisabledAction>}</>
    noteField = noNote
  } else if (item.kind === 'ApproveWaiver' && waiver) {
    actions = <>{decline}<Button variant="primary" icon={ShieldCheck} disabled={!!pending} onClick={() => run('waiver', 'Waiver approved', () => approveWaiver(waiver.id, waiver.version))}>{pending ? 'Recording…' : 'Approve & record'}</Button></>
    noteField = <>{noNote}<p className="ap-quote"><span className="os-muted">Requested reason: </span>{waiver.reason}</p></>
  } else if (item.kind === 'GrantOverride' && freeze) {
    const ids2 = parseOverrideId(item.id)!
    const grant = () => {
      setShown(true)
      const expiryOk = expiry !== '' && Date.parse(`${expiry}:00Z`) > Date.now()
      if (overrideNote || !expiryOk) { if (!expiryOk) setFailure('Choose an expiry in the future (UTC): an override is never permanent.'); return } void run('override', 'Override granted', () => grantOverride(freeze.id, { trainId: train.id, requestedByUserId: ids2.requesterId, reason: note.trim(), expiresAt: `${expiry}:00Z` })) }
    actions = <>{decline}<Button variant="primary" icon={ShieldCheck} disabled={!!pending} onClick={grant}>{pending ? 'Recording…' : 'Grant & record'}</Button></>
    noteField = (
      <>
        <label className="ap-field" htmlFor={expiryId}><span>Override expires (UTC)</span>
          <input id={expiryId} type="datetime-local" value={expiry} onChange={e => setExpiry(e.target.value)} aria-invalid={shown && expiry === ''} /></label>
        <Note id={noteId} hintId={noteHint} label="Decision note: reason for the override" required="Required: at least 20 characters" value={note} onChange={setNote} problem={shown ? overrideNote : null} />
      </>
    )
  } else if (item.kind === 'RecordGoNoGo' && decisions) {
    const go = () => run('go', 'Go recorded', () => recordDecision(train.id, { decision: 'Go', notes: note.trim() || null, newTargetReleaseDate: null, conditions: [] }, train.version))
    const nogo = () => { setShown(true); if (nogoNote) return; void run('nogo', 'No-Go recorded', () => recordDecision(train.id, { decision: 'NoGo', notes: note.trim(), newTargetReleaseDate: null, conditions: [] }, train.version)) }
    actions = (
      <>
        <Button variant="secondary" icon={ShieldX} disabled={!!pending} onClick={nogo}>{pending === 'nogo' ? 'Recording…' : 'Record No-Go'}</Button>
        <Button variant="primary" icon={ShieldCheck} disabled={!!pending} onClick={go}>{pending === 'go' ? 'Recording…' : 'Go & record'}</Button>
      </>
    )
    noteField = (
      <>
        <Note id={noteId} hintId={noteHint} label="Decision note" required="Required for No-Go" value={note} onChange={setNote} problem={shown ? nogoNote : null} />
        <p className="os-muted ap-hint">Go with conditions needs conditions, owners and expiry dates: <button type="button" className="os-link os-link-button" onClick={() => onOpen(train.id)}>record it in the release</button>.</p>
      </>
    )
  } else if (item.kind === 'CloseCondition' && condition) {
    actions = <>{decline}<Button variant="primary" icon={ShieldCheck} disabled={!!pending} onClick={() => run('close', 'Condition closed', () => closeCondition(condition.id, condition.version))}>{pending ? 'Recording…' : 'Close & record'}</Button></>
    noteField = <>{noNote}<p className="ap-quote"><span className="os-muted">Condition: </span>{condition.text} <span className="os-muted">(expires <When iso={condition.expiresAt} />)</span></p></>
  }

  return (
    <section className="os-panel ap-pane" aria-label={`Decision: ${item.title}`}>
      <header className="os-panel-head">
        <div>
          <h2 className="os-panel-title">{item.title}</h2>
          <p className="os-panel-subtitle"><span className="mono">{ids}</span> · {KIND[item.kind]}{item.detail ? ` · ${item.detail}` : ''}</p>
        </div>
        <div className="os-panel-meta"><StatusChip tone={item.overdue ? 'danger' : 'warning'}>{item.overdue ? 'Overdue' : 'Pending decision'}</StatusChip></div>
      </header>
      <div className="os-panel-body ap-body">
        <div className="ap-release">
          <TwoLine primary={<button type="button" className="os-link os-link-button" onClick={() => onOpen(train.id, gate?.id)}>{train.title}</button>}
            secondary={<>{train.changeTicketNumber ?? 'No change ticket'} · {train.status}</>} />
          <div className="ap-release-meta"><StatusChip tone={chipTone(`${train.riskTier} risk`)}>{`${train.riskTier} risk`}</StatusChip><span><WindowText w={ctx.window} /></span></div>
        </div>

        <PropertyList items={[
          { label: 'Due', value: <>{due.utc}{due.local && <span className="os-muted"> ({due.local})</span>}</> },
          { label: 'Release target', value: train.targetReleaseDate, mono: true },
          ...(freeze ? [{ label: 'Freeze', value: `${freeze.name} (${freeze.kind})` }] : []),
          ...(gate ? [{ label: 'Gate', value: `${gate.name} · ${gate.class} · ${gate.tasks.filter(t => t.done).length} of ${gate.tasks.length} tasks done` }] : []),
        ]} />

        {gate && (
          <div className="ap-evidence">
            <h3 className="ap-h3">Evidence</h3>
            {ctx.evidence.length === 0
              ? <p className="os-muted">No evidence is attached to this gate. {/* the approvals design's test report, scan and rollback cards have no source here (Q-0990) */}</p>
              : <ul className="ap-cards">{ctx.evidence.map(e => (
                <li key={e.id} className="ap-card">
                  <span className="ap-card-title"><FileText aria-hidden="true" size={16} strokeWidth={1.6} /> {e.fileName}</span>
                  <span className="os-muted">{fmtSize(e.sizeBytes)} · SHA-256 <span className="mono">{e.sha256.slice(0, 8)}</span>{e.uploadedByName ? ` · ${e.uploadedByName}` : ''}</span>
                  <a className="os-link" href={`/api/v1/attachments/${e.id}`} target="_blank" rel="noopener noreferrer" aria-label={`View evidence: ${e.fileName} (opens in a new tab)`}>View evidence ↗</a>
                </li>))}</ul>}
          </div>
        )}

        {decisions && item.kind === 'RecordGoNoGo' && (
          <p className="os-muted">{decisions.length === 0 ? 'No decision has been recorded for this release.' : `Last decision: ${decisions[0].decision.decision} (${decisions.length} recorded).`}</p>
        )}

        <h3 className="ap-h3">Checks</h3>
        <ul className="ap-checks" aria-label="Checks before this decision">{checks.map((c, i) => <Check key={i} ok={c.ok}>{c.text}</Check>)}</ul>
        <p className="os-muted ap-hint">Several required approvers, a change-window check and policy rules are not modelled yet (REOS-113): this lists the one approver the app requires.</p>

        {noteField}
        {failure && <Notice tone="danger" title="This decision was refused" live>{failure}</Notice>}
        {conflict && <ConflictNotice by="Someone" onUseTheirs={() => { setConflict(false); reload() }} />}

        <div className="ap-actions">
          <p className="os-muted ap-signed">Your decision is recorded in the audit log with your name, the time (UTC) and the version you saw.</p>
          <div className="ap-buttons">{actions}</div>
        </div>
      </div>
    </section>
  )
}

function Note({ id, hintId, label, required, value, onChange, problem }: { id: string; hintId: string; label: string; required: string; value: string; onChange: (v: string) => void; problem: string | null }) {
  return (
    <div className="ap-note">
      <div className="ap-note-head"><label htmlFor={id}>{label}</label><span id={hintId} className="os-muted">{required}</span></div>
      <textarea id={id} rows={3} value={value} onChange={e => onChange(e.target.value)} aria-describedby={hintId} aria-invalid={problem !== null} placeholder="Add rationale or conditions to the decision record…" />
      {problem && <p className="ap-problem" role="alert">✗ {problem}</p>}
    </div>
  )
}
