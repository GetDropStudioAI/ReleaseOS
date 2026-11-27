import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { onCabChanged, onCabPresence } from '../live.ts'
import { useAction } from '../useAction.ts'
import { Button, ConfirmDialog, ConflictNotice, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, PageHeading, Panel, PropertyList, ReadOnlyNotice, StatusChip, Time, TimeRange } from '../ui'
import { Field, Problem } from '../mgmt/forms.tsx'
import { listChanges, type ChangeRow } from './cabApi.ts'
import { canDecide, decisionTone, isSeparationGuard, riskSentence, riskTone, riskWord, statusTone } from './cabLogic.ts'
import {
  getMeeting, getPresence, decideInMeeting, moveAgendaItem, moveMeeting, setAttendance, type Attendee, type Meeting,
} from './workbenchApi.ts'
import {
  ATTENDANCE_WORD, NO_FLAGS, VERDICT_LABEL, attendanceTone, canDecideHere, canRun, clock, completeWarning, connectedCount, decidedRows, decisionBlock, defaultSelection, flagsFor, isOpen, meetingStatusWord,
  meetingTone, newTimer, pauseTimer, pendingRows, positionAfterSkip, positionForTakeNext, recordOf, resetTimer, roleWord, rowsOf, spentOn, startTimer, summaryLine, type Row, type Verdict,
} from './workbenchLogic.ts'
import './cab.css'
import './workbench.css'

interface Refusal { guard: string; message: string }

/**
 * The CAB workbench (REOS-179, D56, D63): one screen to run a meeting. The agenda (reorder, skip to the end of the queue, take next), the person in front of the CAB with the flags that matter
 * (conflict, freeze, blackout), a timer per item, Approve / Reject / Defer with a comment, who is on the roll and who is connected right now, and the meeting's record. Completing the meeting
 * fixes it: decisions are append-only, and a Complete meeting no longer changes. It exists only while the CAB module is on (the page and routes are absent otherwise; the API answers 404).
 * Every write carries the version it was loaded with; a 409 is shown with the current row, and a separation-of-duties refusal in the rule's words.
 */
export function Workbench({ me, id, onBack, onOpenChange }: { me: Me; id: string; onBack: () => void; onOpenChange: (changeId: string) => void }) {
  const [meeting, setMeeting] = useState<Meeting | null>(null)
  const [changes, setChanges] = useState<Map<string, ChangeRow>>(new Map())
  const [connected, setConnected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [missing, setMissing] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const [comment, setComment] = useState('')
  const [confirm, setConfirm] = useState<'complete' | 'reject' | null>(null)
  const [conflict, setConflict] = useState<string | null>(null)
  const [refusal, setRefusal] = useState<Refusal | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [timer, setTimer] = useState(newTimer)
  const [now, setNow] = useState(() => Date.now())
  const { run, pending } = useAction()
  const picked = useRef(false)

  const load = useCallback(() => {
    Promise.all([getMeeting(id), listChanges()]).then(([m, cs]) => {
      setMeeting(m); setChanges(new Map(cs.map(c => [c.id, c]))); setError(null); setMissing(false)
    }).catch(e => { if (e instanceof ApiError && e.status === 404) setMissing(true); else setError(errMsg(e)) })
  }, [id])
  const loadPresence = useCallback(() => { getPresence(id).then(p => setConnected(new Set(p.userIds))).catch(() => { /* presence is a nicety: the roll still works without it */ }) }, [id])
  useEffect(() => { load(); loadPresence() }, [load, loadPresence])
  useEffect(() => onCabChanged(changeId => { if (!changeId || changeId === id || changes.has(changeId)) load() }), [load, id, changes])
  useEffect(() => onCabPresence(loadPresence), [loadPresence])
  useEffect(() => {
    if (!timer.running) return
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [timer.running])

  const rows = useMemo(() => (meeting ? rowsOf(meeting, changes) : []), [meeting, changes])
  useEffect(() => {
    // the first item still to decide is picked once; after that the chair's choice stays, unless that item left the agenda
    if (rows.length === 0) return
    if (!picked.current || (selected && !rows.some(r => r.item.id === selected))) { setSelected(defaultSelection(rows)); picked.current = true }
  }, [rows, selected])

  if (missing) return <div className="os-page"><PageHeading trail={['Workspace', 'Change advisory board', 'Not found']} title="Meeting not found" /><EmptyState title="No such meeting" action={<Button onClick={onBack}>All meetings</Button>}>It may have been mistyped, or it belongs to another workspace.</EmptyState></div>
  if (error && !meeting) return <div className="os-page"><ErrorState what="The meeting" message={error} onRetry={load} /></div>
  if (!meeting) return <div className="os-page"><LoadingState what="the meeting" /></div>

  const m = meeting
  const run_ = canRun(me, m), mayDecide = canDecideHere(me, m)
  const pend = pendingRows(rows), done = decidedRows(rows)
  const row = rows.find(r => r.item.id === selected)
  const allChanges = rows.flatMap(r => (r.change ? [r.change] : []))

  /** One write: names what happened in words (409 shows the current row, a rule shows its own message) and refetches on success. */
  const attempt = <T,>(label: string, fn: () => Promise<T>, said?: string, onOk?: (r: T) => void) => run(label, async () => {
    setRefusal(null); setConflict(null); setProblem(null)
    try {
      const r = await fn()
      setConfirm(null)
      if (said) announce(said)
      onOk?.(r)
      load()
      return r
    } catch (e) {
      setConfirm(null)
      if (e instanceof ApiError && e.status === 409) {
        const cur = e.body?.current as Record<string, unknown> | undefined   // a meeting (reorder, start) or a change (decision)
        if (cur && 'agenda' in cur) setMeeting(cur as unknown as Meeting)
        else if (cur && 'decisions' in cur) setChanges(prev => new Map(prev).set(cur.id as string, cur as unknown as ChangeRow))
        setConflict('Someone else got there first. The screen now shows their version.')
        load()
      } else if (e instanceof ApiError && e.status === 422) setRefusal({ guard: e.body?.guard ?? '', message: e.body?.message ?? 'That was refused.' })
      else setProblem(errMsg(e))
      return undefined
    }
  })

  const start = () => attempt('Starting…', () => moveMeeting(m.id, 'start', m.version), `${m.title} started`, setMeeting)
  const complete = () => attempt('Completing…', () => moveMeeting(m.id, 'complete', m.version), `${m.title} is complete`, r => { setMeeting(r); setTimer(t => pauseTimer(t, Date.now())) })
  const reorder = (r: Row, position: number, said: string) => attempt('Moving…', () => moveAgendaItem(m.id, r.item.id, position, m.version), said, setMeeting)
  const mark = (a: Attendee, attendance: string) => attempt('Saving…', () => setAttendance(m.id, a.person.id, attendance, a.version), `${a.person.name}: ${ATTENDANCE_WORD[attendance]}`)
  const decide = (verdict: Verdict) => {
    if (!row?.change) return
    const c = row.change
    return attempt('Recording…', () => decideInMeeting(c.id, m.id, verdict, comment.trim() || null, c.version), `${verdict} recorded on ${c.key}`, () => setComment(''))
  }

  const select = (itemId: string) => { setSelected(itemId); setComment(''); setRefusal(null); setConflict(null) }
  const t0 = timer.running ? now : Date.now()
  const flagged = (r: Row) => flagsFor(r.change, allChanges)

  const itemRow = (r: Row, index: number, list: Row[], isPendingList: boolean) => {
    const flags = flagged(r), on = r.item.id === selected, key = r.item.changeKey
    return (
      <li key={r.item.id} className={`wb-item${on ? ' selected' : ''}`} data-testid={`agenda-${key}`}>
        <button type="button" className="wb-item-main" aria-current={on ? 'true' : undefined} onClick={() => select(r.item.id)}>
          <span className="wb-item-head"><span className="wb-pos">{index + 1}.</span><span><span className="wb-key">{key}</span> {r.item.changeTitle}</span></span>
          <span className="wb-chips">
            {r.outcome ? <StatusChip tone={decisionTone(r.outcome)}>{r.outcome}</StatusChip> : r.change ? <StatusChip tone={statusTone(r.change.status)}>{r.change.status}</StatusChip> : null}
            {r.change && <StatusChip tone={riskTone(r.change.riskLevel)}>{riskWord(r.change.riskLevel)}</StatusChip>}
            {flags.map(f => <StatusChip key={f.label} tone={f.tone}>{f.label}</StatusChip>)}
          </span>
        </button>
        {run_ && isPendingList && (
          <div className="wb-item-actions">
            {index > 0 && <Button disabled={!!pending} aria-label={`Move ${key} up`} onClick={() => void reorder(r, r.item.sequenceOrder - 1, `${key} moved up`)}>Move up</Button>}
            {index < list.length - 1 && <Button disabled={!!pending} aria-label={`Move ${key} down`} onClick={() => void reorder(r, r.item.sequenceOrder + 1, `${key} moved down`)}>Move down</Button>}
            {r.item.sequenceOrder < rows.length && <Button disabled={!!pending} aria-label={`Skip ${key} to the end of the agenda`} onClick={() => void reorder(r, positionAfterSkip(rows), `${key} skipped to the end of the agenda`)}>Skip</Button>}
            {index > 0 && <Button disabled={!!pending} aria-label={`Take ${key} next`} onClick={() => void reorder(r, positionForTakeNext(rows, r.item.id), `${key} is next`)}>Take next</Button>}
          </div>
        )}
      </li>
    )
  }

  const detail = (r: Row) => {
    const c = r.change
    if (!c) return <p className="mg-note">This change could not be read.</p>
    const flags = flagged(r), spent = spentOn(timer, r.item.id, t0), running = timer.running === r.item.id
    const earlier = c.decisions.filter(d => d.meetingId !== m.id)
    const block = (v: Verdict) => decisionBlock(v, comment, c, me)
    return (
      <div className="mg-stack">
        <PropertyList items={[
          { label: 'Type', value: c.changeType },
          { label: 'Requested by', value: c.requestedBy.name },
          { label: 'Planned window', value: c.plannedStartAt && c.plannedEndAt ? <TimeRange from={c.plannedStartAt} to={c.plannedEndAt} /> : 'Not set' },
          { label: 'Linked release', value: c.release?.title ?? 'None' },
          { label: 'Rollback note', value: c.rollbackNote ? <span className="cab-pre">{c.rollbackNote}</span> : 'None recorded' },
          ...(r.item.notes ? [{ label: 'Agenda note', value: <span className="cab-pre">{r.item.notes}</span> }] : []),
          ...(c.risk ? [{ label: 'Risk', value: riskSentence(c.risk) }] : []),
        ]} />
        <div>
          <h3 className="wb-sub">Conflicts, freezes and blackouts</h3>
          {flags.length === 0 ? <p className="mg-note">{NO_FLAGS}</p> : (
            <ul className="wb-flags">{flags.map(f => <li key={f.label} className="wb-flag"><span className="wb-chips"><StatusChip tone={f.tone}>{f.label}</StatusChip></span><span>{f.detail}</span></li>)}</ul>
          )}
        </div>
        {isOpen(m) && (
          <div role="group" aria-label="Time on this item" className="wb-timer">
            <span className="wb-clock" role="timer" aria-label={`Time on ${c.key}: ${clock(spent)}`}>{clock(spent)}</span>
            {running ? <Button onClick={() => setTimer(t => pauseTimer(t, Date.now()))}>Pause timer</Button> : <Button onClick={() => setTimer(t => startTimer(t, r.item.id, Date.now()))}>{spent > 0 ? 'Resume timer' : 'Start timer'}</Button>}
            {spent > 0 && <Button onClick={() => setTimer(t => resetTimer(t, r.item.id))}>Reset timer</Button>}
          </div>
        )}
        {earlier.length > 0 && (
          <div>
            <h3 className="wb-sub">Earlier decisions on this change</h3>
            {earlier.map(d => <div key={d.id} className="wb-record"><span className="wb-chips"><StatusChip tone={decisionTone(d.decision)}>{d.decision}</StatusChip><span>{d.decidedBy.name}</span><Time at={d.decidedAt} date /></span>{d.rationale && <span className="cab-pre">{d.rationale}</span>}</div>)}
          </div>
        )}
        {r.outcome && !pendingRows([r]).length && <Notice tone="info" title={`${r.outcome} in this meeting`}>The decision is in the meeting's record below and cannot be changed.</Notice>}
        {pendingRows([r]).length > 0 && c.status === 'Submitted' && (
          mayDecide ? (
            <div className="mg-form">
              <Field label="Comment" hint="Required to approve or reject. It is kept with the decision."><textarea className="mg-input" rows={3} value={comment} maxLength={2000} onChange={e => setComment(e.target.value)} /></Field>
              <div className="wb-verdicts">
                {block('Approved') ? <DisabledAction variant="primary" reason={block('Approved')!}>{VERDICT_LABEL.Approved}</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={() => void decide('Approved')}>{VERDICT_LABEL.Approved}</Button>}
                {block('Rejected') ? <DisabledAction variant="destructive" reason={block('Rejected')!}>{VERDICT_LABEL.Rejected}</DisabledAction>
                  : <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm('reject')}>{VERDICT_LABEL.Rejected}</Button>}
                <Button disabled={!!pending} onClick={() => void decide('Deferred')}>{VERDICT_LABEL.Deferred}</Button>
              </div>
            </div>
          ) : !canDecide(me) && m.status === 'InProgress' ? <ReadOnlyNotice>You can follow the meeting. An RTE, Release Manager or Governance Officer records decisions.</ReadOnlyNotice>
            : <p className="mg-note">Decisions are recorded while the meeting is in progress{m.status === 'Planned' ? ': start the meeting first.' : '.'}</p>
        )}
      </div>
    )
  }

  const record = recordOf(rows, m.id)
  const rejecting = row?.change
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Change advisory board', 'Meetings', m.title]} title={m.title}
        description={<>Chair {m.chair.name} · <TimeRange from={m.startsAt} to={m.endsAt} />{m.location ? ` · ${m.location}` : ''}</>}
        actions={<span className="mg-actions">
          <Button onClick={onBack}>All meetings</Button>
          {run_ && m.status === 'Planned' && <Button variant="primary" disabled={!!pending} onClick={() => void start()}>{pending === 'Starting…' ? pending : 'Start meeting'}</Button>}
          {run_ && m.status === 'InProgress' && <Button variant="primary" disabled={!!pending} onClick={() => setConfirm('complete')}>Complete meeting</Button>}
        </span>} />
      <div className="cab-chips"><StatusChip tone={meetingTone(m.status)}>{meetingStatusWord(m.status)}</StatusChip></div>
      {!run_ && !mayDecide && isOpen(m) && <ReadOnlyNotice>You can follow this meeting. An RTE or Release Manager runs it; deciders record decisions.</ReadOnlyNotice>}
      {m.status === 'Completed' && <Notice tone="success" title="Meeting complete">Ended <Time at={m.completedAt ?? m.lastChangedAt ?? m.endsAt} date />. Its decisions are final and the meeting can no longer be edited.</Notice>}
      {m.status === 'Cancelled' && <Notice tone="neutral" title="Meeting cancelled">This meeting was cancelled and records no decisions.</Notice>}
      {m.status === 'Planned' && <Notice tone="warning" title="Not started">Decisions are recorded once the meeting is in progress.</Notice>}
      {conflict && <ConflictNotice onUseTheirs={() => { setConflict(null); load() }} />}
      {refusal && (
        <Notice tone="danger" title={isSeparationGuard(refusal.guard) ? 'Refused by a separation-of-duties rule' : 'Not allowed'} live>
          {refusal.message}{refusal.guard && <> <span className="cab-mono">({refusal.guard})</span></>}
        </Notice>
      )}
      <Problem message={problem} />
      <div className="wb-grid">
        <div className="wb-col">
          <Panel title="Agenda" subtitle={summaryLine(rows)}>
            {rows.length === 0 ? <p className="mg-note">No change is on this agenda.</p> : (
              <>
                <h3 className="wb-sub">Still to decide</h3>
                {pend.length === 0 ? <p className="mg-note">Every change has a decision.</p> : <ol className="wb-list" aria-label="Still to decide">{pend.map((r, i) => itemRow(r, i, pend, true))}</ol>}
                {done.length > 0 && <><h3 className="wb-sub">Decided</h3><ol className="wb-list" aria-label="Decided">{done.map((r, i) => itemRow(r, i, done, false))}</ol></>}
              </>
            )}
          </Panel>
          <Panel title="Attendees" subtitle={`${connectedCount(m.attendees, connected)} of ${m.attendees.length} connected now`}>
            <ul className="wb-people">
              {m.attendees.map(a => (
                <li key={a.person.id} className="wb-person" data-testid={`attendee-${a.person.name}`}>
                  <span className="wb-person-name"><span>{a.person.name}</span><span className="os-muted">{roleWord(a)}</span></span>
                  <span className="wb-chips">
                    <StatusChip tone={attendanceTone(a.attendance)}>{ATTENDANCE_WORD[a.attendance] ?? a.attendance}</StatusChip>
                    <StatusChip tone={connected.has(a.person.id) ? 'success' : 'neutral'}>{connected.has(a.person.id) ? 'Connected' : 'Not connected'}</StatusChip>
                    {run_ && a.attendance !== 'Attended' && <Button disabled={!!pending} aria-label={`Mark ${a.person.name} attended`} onClick={() => void mark(a, 'Attended')}>Attended</Button>}
                    {run_ && a.attendance !== 'Absent' && <Button disabled={!!pending} aria-label={`Mark ${a.person.name} absent`} onClick={() => void mark(a, 'Absent')}>Absent</Button>}
                  </span>
                </li>
              ))}
            </ul>
            <p className="mg-note">Invitations and replies come with meeting scheduling; here the chair records who attended.</p>
          </Panel>
        </div>
        <div className="wb-col">
          <Panel title={row ? <>{row.item.changeKey} · {row.item.changeTitle}</> : 'No change selected'} subtitle={row ? 'The change in front of the CAB' : undefined}
            meta={row?.change ? <Button onClick={() => onOpenChange(row.change!.id)}>Open change request</Button> : undefined}>
            {row ? detail(row) : <p className="mg-note">Select a change on the agenda.</p>}
          </Panel>
          <Panel title="Meeting record" subtitle="Decisions are kept as they were recorded and cannot be edited">
            {record.length === 0 ? <p className="mg-note">No decision has been recorded in this meeting.</p> : record.map(({ change, decision: d }) => (
              <div key={d.id} className="wb-record" data-testid="record-row">
                <span className="wb-chips"><StatusChip tone={decisionTone(d.decision)}>{d.decision}</StatusChip><span className="wb-key">{change.key}</span><span>{d.decidedBy.name}</span><Time at={d.decidedAt} date /></span>
                {d.rationale && <span className="cab-pre">{d.rationale}</span>}
              </div>
            ))}
          </Panel>
        </div>
      </div>
      <ConfirmDialog open={confirm === 'complete'} title="Complete this meeting?" confirmLabel="Complete meeting" pendingLabel="Completing…" destructive pending={pending === 'Completing…'}
        onConfirm={() => void complete()} onCancel={() => setConfirm(null)}>
        {completeWarning(rows)} Once complete, its decisions are final and the meeting cannot be edited.
      </ConfirmDialog>
      <ConfirmDialog open={confirm === 'reject'} title={`Reject ${rejecting?.key ?? 'this change'}?`} confirmLabel="Reject change" pendingLabel="Recording…" destructive pending={pending === 'Recording…'}
        onConfirm={() => void decide('Rejected')} onCancel={() => setConfirm(null)}>
        {rejecting?.key} moves to Rejected, which is final. Your comment is kept with the decision.
      </ConfirmDialog>
    </div>
  )
}
