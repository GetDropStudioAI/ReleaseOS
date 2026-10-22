import { useEffect, useId, useRef, useState, useSyncExternalStore } from 'react'
import { useDraft, useSession } from './session'
import { ChangeRecordPanel, GoNoGo, emptyGoNoGo, goNoGoKey, type GoNoGoDraft } from './Governance'
import { FreezesPanel } from './Freezes'
import { Links } from './Links'
import { fmtDayTime } from './time'
import { day, errMsg, plural, splitId, tMinus } from './format'
import { Runbook } from './Runbook'
import { Closeout } from './Closeout'
import { commsKey, type CommsDraft } from './CommsDrawer'
import { ExportsPanel } from './ExportsPanel'
import { Checklist, Products, Timeline, WindowLine, type Selection } from './Planning'
import { scrollAndFocus } from './focus'
import { MilestonesSection, milestoneState, useMilestones } from './Milestones'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { Conflict, isConflict } from './Conflict'
import { announce } from './announce'
import { CLOSE_WORD, CompleteForm, completeKey, emptyComplete, type CompleteDraft } from './TrainClose'
import {
  abortTrain, advanceTrain, completeTrain, getFreezesAhead, getReadiness, getStream, getTrain,
  type ApiError, type FreezeAhead, type Readiness, type StreamRow, type TrainDetail,
} from './api'

// Aborted trains keep a group of their own (shown only when the list has any), so a train never silently vanishes from the Stream (UX review 26).
const GROUPS: { label: string; status: string; hideEmpty?: boolean }[] = [
  { label: 'Executing', status: 'Executing' }, { label: 'Gated', status: 'Gated' },
  { label: 'Planning', status: 'Planning' }, { label: 'Complete · last 30 days', status: 'Complete' }, { label: 'Aborted', status: 'Aborted', hideEmpty: true },
]
const ORDER = ['Planning', 'Gated', 'Executing', 'Complete']
const ABORTABLE = ['Planning', 'Gated', 'Executing']   // legal moves to Aborted (PROJECT_SCOPE 3)

// Status is words + glyphs + coloured text, never pills or badges (CLAUDE.md rule 9).
const GLYPH: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'Certified' }, Waived: { g: '●', cls: 'warn', word: 'Waived' },
  Failed: { g: '✗', cls: 'bad', word: 'Failed' }, InProgress: { g: '◐', cls: 'accent', word: 'In progress' },
  Pending: { g: '○', cls: 'muted', word: 'Pending' },
}
export function Glyph({ status }: { status: string }) {
  const x = GLYPH[status] ?? GLYPH.Pending
  return <span className={x.cls} role="img" aria-label={x.word} title={x.word}>{x.g}</span>
}

export function Stream({ rows, selected, onSelect, onNew }: { rows: StreamRow[] | null; selected: string | null; onSelect: (id: string) => void; onNew?: () => void }) {
  const { filter: q, setFilter: setQ } = useSession()   // the filter is saved with the tab's UI state
  const failed = useSyncExternalStore(streamLoad.subscribe, () => streamLoad.error)
  const shown = (rows ?? []).filter(r => r.title.toLowerCase().includes(q.trim().toLowerCase()))
  return (
    <>
      {onNew && <div className="section-head stream-head"><span className="cap">Trains</span><button type="button" className="text" onClick={onNew}>New train</button></div>}
      <p><label className="cap" htmlFor="flt">Filter trains</label>
        <input id="flt" className="line block" placeholder="Title or release id" value={q} onChange={e => setQ(e.target.value)} /></p>
      {failed && <p className="bad" role="alert">✗ The trains could not be loaded: {failed} <button type="button" className="text" onClick={() => streamLoad.retry()}>Retry</button></p>}
      {(!failed || rows) && GROUPS.map(g => {
        const list = shown.filter(r => r.status === g.status)
        if (g.hideEmpty && list.length === 0) return null
        return (
          <section key={g.label}>
            <h2 className="group-label">{g.label}</h2>
            {rows === null ? <p className="muted empty">Loading…</p> : list.length === 0 ? <p className="muted empty">No trains</p> :
              list.map(r => {
                const { id, name } = splitId(r.title)
                const done = r.status === 'Complete' || r.status === 'Aborted'
                return (
                  <button key={r.id} type="button" className="stream-row" aria-current={selected === r.id ? 'true' : undefined} onClick={() => onSelect(r.id)}>
                    <span className="stream-top"><span className="mono muted">{id}</span>
                      {!done && (r.blockers > 0 ? <span className="mono warn">▲ {plural(r.blockers, 'blocker')}</span> : <span className="mono muted">0 blockers</span>)}</span>
                    <span className="stream-title">{name}</span>
                    {done
                      ? <span className={r.closeCode === 'Successful' ? 'ok stream-meta' : 'warn stream-meta'}>{r.closeCode === 'Successful' ? '✓ ' : r.status === 'Aborted' ? '✗ ' : ''}{r.closeCode ?? r.status}{r.endedOn ? ` · ${day(r.endedOn)}` : ''}</span>
                      : <span className="stream-bottom"><span className="muted">{day(r.targetReleaseDate)} · {tMinus(r.daysToTarget)}</span>
                          <span className="mono stream-gates"><span className="sr-only">Gates: </span>{r.gates.map((s, i) => <Glyph key={i} status={s} />)}</span></span>}
                  </button>
                )
              })}
          </section>
        )
      })}
    </>
  )
}

export function TrainHeader({ id, refreshKey, onChanged, selection, onSelect, canPlan, canDecide, onMode }: { id: string; refreshKey: number; onChanged: () => void; selection: Selection; onSelect: (s: Selection) => void; canPlan: boolean; canDecide: boolean; onMode: (m: import('./route').Mode) => void }) {
  const [t, setT] = useState<TrainDetail | null>(null)
  const [r, setR] = useState<Readiness | null>(null)
  const [rErr, setRErr] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const [err, setErr] = useState<string | null>(null)
  const whyId = useId(), completeId = useId()
  const { run, pending } = useAction()   // one lifecycle action at a time: Advance, Complete or Abort
  // A refused lifecycle write, kept apart from `err` (cleared by every refetch, and the write refetches) and tied to its train (this header is reused
  // when another train is opened). A 409 keeps what the user chose (close code and notes, or the abort reason) and can apply it to the train as it is now.
  const [actErr, setActErr] = useState<{ trainId: string; text: string } | null>(null)
  const [conflict, setConflict] = useState<{ trainId: string; status: string; error: ApiError; again: string; retry: (version: number) => unknown } | null>(null)
  const [complete, setComplete] = useDraft<CompleteDraft>(completeKey(id))
  const completeBtn = useRef<HTMLButtonElement>(null)
  const closeCodes = useRef<HTMLSpanElement>(null)
  const [chosenGate, setChosenGate] = useState<string | null>(null)
  const [goDraft, setGoDraft] = useDraft<GoNoGoDraft>(goNoGoKey(id))   // the header button and the Go/No-Go section share one draft
  const [comms, setComms] = useDraft<CommsDraft>(commsKey(id))   // REOS-45: App swaps the right drawer to the Comms drawer while this is open
  const milestones = useMilestones(id, refreshKey)   // Q-0843: the timeline's ◆ markers and the Milestones section share one fetch
  useEffect(() => {
    let live = true
    setErr(null); setRErr(null)
    getTrain(id).then(async d => {
      if (!live) return
      setT(d)
      setR(d.nextStatus ? await getReadiness(id, d.nextStatus).catch(e => { if (live) setRErr(errMsg(e)); return null }) : null)
    }).catch(e => live && setErr(errMsg(e)))
    return () => { live = false }
  }, [id, refreshKey, tick])
  const retry = () => setTick(n => n + 1)

  if (err && !t) return <p className="bad" role="alert">✗ {err} <button type="button" className="text" onClick={retry}>Retry</button></p>
  if (!t) return <p className="muted">Loading…</p>

  /** Runs one lifecycle write with If-Match; on 409 the Conflict notice offers `again`, which repeats it at the version the server returned. */
  const act = (label: string, again: string, fn: (version: number) => Promise<unknown>, done: string, after?: () => void) => {
    const attempt = (version: number) => run(label, async () => {
      setActErr(null); setConflict(null)
      try { await fn(version); announce(done); after?.(); onChanged() }
      catch (e) {
        if (isConflict(e)) setConflict({ trainId: t.id, status: t.status, error: e, again, retry: attempt })
        else setActErr({ trainId: t.id, text: errMsg(e, 'Could not change the train.') })
        onChanged()
      }
    })
    return attempt(t.version)
  }
  const advance = () => t.nextStatus && act('Advancing…', `Advance to ${t.nextStatus} anyway`, v => advanceTrain(t.id, t.nextStatus!, v), `Advanced to ${t.nextStatus}.`)
  const toComplete = t.nextStatus === 'Complete'   // Executing: this hop needs a close code, so it opens the Complete form instead of posting :advance
  const toggleComplete = () => {
    setConflict(null)
    if (complete) { setComplete(undefined); return }
    setComplete(emptyComplete())
    requestAnimationFrame(() => closeCodes.current?.querySelector<HTMLElement>('[role=radio][tabindex="0"]')?.focus())
  }
  const completeNow = (code: Parameters<typeof completeTrain>[1], notes: string | null) =>
    act('Completing…', 'Complete anyway', v => completeTrain(t.id, code, notes, v), `Train completed: ${CLOSE_WORD[code]}.`, () => setComplete(undefined))
  const abort = (reason: string) => act('Aborting…', 'Abort anyway', v => abortTrain(t.id, reason, v), 'Train aborted.')
  const { id: relId, name } = splitId(t.title)
  const blockers = r?.blockers.length ?? 0
  const gatesBefore = t.nextStatus ? t.gates.filter(g => ORDER.indexOf(g.requiredBeforeStatus) <= ORDER.indexOf(t.nextStatus!)) : []
  const others = (r?.blockers ?? []).filter(b => b.failure.guard !== 'GateLockout')
  // Checklist shows the gate the user picked, else the first gate still open, else the last.
  const checklistGate = t.gates.find(g => g.id === chosenGate) ?? t.gates.find(g => g.status !== 'Certified' && g.status !== 'Waived') ?? t.gates[t.gates.length - 1]
  const pickGate = (gid: string) => { setChosenGate(gid); onSelect({ kind: 'gate', id: gid }) }

  return (
    <>
      <div className="header-line">
        <span className="cap">Release train{t.changeTicketNumber ? <> · <span className="mono">{t.changeTicketNumber}</span></> : ''} · Risk {t.riskTier} · <span className="lc">v{t.version}</span></span>
        <span className="actions">
          <button type="button" className="text" onClick={() => onMode('rehearsal')}>Rehearsal</button>
          <button type="button" className="text" onClick={() => onMode('live')}>Live runbook</button>
          {canDecide && <button type="button" className="text" onClick={e => {
            if (goDraft) { setGoDraft(undefined); return }
            setGoDraft(emptyGoNoGo())   // the form opens far down the page: take the user there (UX review 11)
            const btn = e.currentTarget
            requestAnimationFrame(() => {
              const sec = document.querySelector<HTMLElement>('section[aria-label="Go/No-Go"]')
              // Focus its heading, unless the form has already moved focus into itself: then only scroll.
              if (document.activeElement === btn || document.activeElement === document.body) scrollAndFocus(sec?.querySelector<HTMLElement>('h2') ?? sec ?? null)
              else sec?.scrollIntoView({ behavior: window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' })
            })
          }}>{goDraft ? 'Cancel Go/No-Go' : 'Record Go/No-Go'}</button>}
          <button type="button" className="text" data-comms-trigger aria-expanded={!!comms?.open} onClick={() => setComms(comms?.open ? undefined : { open: true, tab: 'message' })}>Communicate</button>
          <button type="button" className="text" onClick={() => scrollAndFocus(document.querySelector<HTMLElement>('#exports h2'))}>Export</button>
          {canPlan && ABORTABLE.includes(t.status) && <ConfirmInline label="Abort train" question={`Abort ${t.title}? An aborted train cannot be restarted.`} confirmLabel="Abort train" pendingLabel="Aborting…"
            reason={{ label: 'Reason', required: true }} disabled={!!pending} onConfirm={abort} />}
          {toComplete
            ? canPlan && <button ref={completeBtn} type="button" className="text" disabled={!!pending || blockers > 0} aria-expanded={!!complete} aria-controls={complete ? completeId : undefined}
                aria-describedby={blockers > 0 ? whyId : undefined} onClick={toggleComplete}>{complete ? 'Cancel complete' : 'Complete…'}</button>
            : t.nextStatus && <button type="button" className="text" disabled={!!pending || blockers > 0} aria-describedby={blockers > 0 ? whyId : undefined} onClick={advance}>{pending === 'Advancing…' ? pending : `Advance to ${t.nextStatus}`}</button>}
        </span>
      </div>
      {t.nextStatus && blockers > 0 && (!toComplete || canPlan) && <p id={whyId} className="muted small">{toComplete ? 'Complete' : `Advance to ${t.nextStatus}`} is off: {plural(blockers, 'guard')} not met, listed under “To reach {t.nextStatus}”.</p>}
      <h1>{relId ? `${relId} ${name}` : name}</h1>
      <p className="muted">{t.status} · Target <strong className="label">{day(t.targetReleaseDate)} {t.targetReleaseDate.slice(0, 4)}</strong> · <span className="mono">{tMinus(t.daysToTarget)}</span> business days · <WindowLine trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} /></p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {actErr?.trainId === t.id && <p className="bad" role="alert">✗ {actErr.text}</p>}
      {conflict?.trainId === t.id && <Conflict error={conflict.error} what="train">
        {/* "anyway" only while the train is still where it was: once its status moved, the user decides again from what the screen now shows */}
        {conflict.status === t.status && <><button type="button" className="text" disabled={!!pending} onClick={() => conflict.retry((conflict.error.body?.current as { version?: number } | undefined)?.version ?? t.version)}>{pending ?? conflict.again}</button>{' '}</>}
        <button type="button" className="text" disabled={!!pending} onClick={() => setConflict(null)}>Dismiss</button></Conflict>}
      {t.nextStatus && r && (
        <section className="readiness" aria-label="Readiness">
          <span className="cap">To reach {t.nextStatus}</span>
          {gatesBefore.map(g => {
            const ok = g.status === 'Certified' || g.status === 'Waived'
            return <span key={g.id}><span className={ok ? 'ok' : 'bad'} aria-hidden="true">{ok ? '✓' : '✗'}</span> {g.name}<span className="sr-only">{ok ? ': met' : ': not met'}</span>{!ok && <span className="muted"> due {day(g.dueOn)}</span>}</span>
          })}
          {others.map((b, i) => <span key={i}><span className="bad" aria-hidden="true">✗</span><span className="sr-only">Not met:</span> {b.failure.message}</span>)}
          {r.ready && <span className="ok">✓ Ready for {t.nextStatus}</span>}
        </section>
      )}
      {t.nextStatus && rErr && <p className="bad" role="alert">✗ The readiness for {t.nextStatus} could not be loaded: {rErr} <button type="button" className="text" onClick={retry}>Retry</button></p>}
      {toComplete && canPlan && complete && <CompleteForm id={completeId} draft={complete} onDraft={setComplete} pending={pending} groupRef={closeCodes} onConfirm={completeNow}
        onCancel={() => { setComplete(undefined); requestAnimationFrame(() => completeBtn.current?.focus()) }} />}
      <Products trainId={t.id} refreshKey={refreshKey} selection={selection} onSelect={onSelect} canPlan={canPlan} trainStatus={t.status} onChanged={onChanged} />
      <Timeline gates={t.gates} todayT={t.daysToTarget} targetDate={t.targetReleaseDate} selectedId={checklistGate?.id ?? null} onSelect={pickGate} trainId={t.id} trainStatus={t.status} canPlan={canPlan} onChanged={onChanged}
        milestones={(milestones.rows ?? []).map(m => ({ id: m.id, name: m.name, dueOn: m.dueOn, tMinus: m.tMinus, state: milestoneState(m) }))} />
      {checklistGate && <Checklist gateId={checklistGate.id} trainId={t.id} refreshKey={refreshKey} selection={selection} onSelect={onSelect} onChanged={onChanged} />}
      <MilestonesSection trainId={t.id} rows={milestones.rows} error={milestones.error} canPlan={canPlan} onChanged={onChanged} />
      <GoNoGo trainId={t.id} trainVersion={t.version} canDecide={canDecide} refreshKey={refreshKey} onChanged={onChanged} />
      <ChangeRecordPanel trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
      <FreezesPanel trainId={t.id} refreshKey={refreshKey} onChanged={onChanged} />
      <Links key={t.id} trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
      <Runbook trainId={t.id} canPlan={canPlan} refreshKey={refreshKey} selection={selection} onSelect={onSelect} onChanged={onChanged} />
      <Closeout train={t} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
      <ExportsPanel trainId={t.id} refreshKey={refreshKey} />
    </>
  )
}

/**
 * App owns the Stream's rows (useStream) and passes only `rows` to <Stream>, so a failed load and its retry reach the Stream through this
 * small store. A failure is an inline error with Retry, never an empty "No trains" list (UX review 7).
 */
const streamLoad = {
  error: null as string | null, retry: () => {}, listeners: new Set<() => void>(),
  set(e: string | null) { if (e !== this.error) { this.error = e; this.listeners.forEach(l => l()) } },
  subscribe: (l: () => void) => { streamLoad.listeners.add(l); return () => { streamLoad.listeners.delete(l) } },
}

export function useStream(enabled: boolean) {
  const [rows, setRows] = useState<StreamRow[] | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => { streamLoad.retry = () => setTick(n => n + 1) }, [])
  useEffect(() => { if (enabled) getStream().then(d => { setRows(d); streamLoad.set(null) }).catch(e => streamLoad.set(errMsg(e))) }, [tick, enabled])
  return { rows, reload: () => setTick(n => n + 1) }
}

const stamp = fmtDayTime

/** Pinned to the bottom of the Stream (mockups/Main.html): freezes and chills that are running or coming, with the overrides already granted. */
export function FreezeFooter({ refreshKey }: { refreshKey: number }) {
  const [rows, setRows] = useState<FreezeAhead[]>([])
  const [err, setErr] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const headId = useId()
  useEffect(() => { getFreezesAhead().then(d => { setRows(d); setErr(null) }).catch(e => setErr(errMsg(e))) }, [refreshKey, tick])
  if (err) return <div className="freeze-footer"><p className="bad" role="alert">✗ Freezes ahead could not be loaded: {err} <button type="button" className="text" onClick={() => setTick(n => n + 1)}>Retry</button></p></div>
  if (rows.length === 0) return null
  return (
    <section className="freeze-footer" aria-labelledby={headId}>
      <div id={headId} className="cap">{rows.some(r => r.active) ? 'Freeze in effect' : 'Freeze ahead'}</div>
      {rows.map(r => (
        <div key={r.id}>
          {/* Freeze and Chill differ in words and glyph, not only red vs orange (WCAG 1.4.1) */}
          <div><span className={r.kind === 'Freeze' ? 'bad' : 'warn'}>{r.kind === 'Freeze' ? '▲ Freeze' : '◐ Chill'} · {r.name}</span> · {stamp(r.startsAt)} – {stamp(r.endsAt)}{r.active ? <span className="bad"> · now</span> : null}</div>
          <div className="muted">Scope: {r.scope.replace('Products matching ', '')}{r.overridesGranted > 0 ? ` · ${plural(r.overridesGranted, 'override')} granted` : ''}</div>
        </div>))}
    </section>
  )
}
