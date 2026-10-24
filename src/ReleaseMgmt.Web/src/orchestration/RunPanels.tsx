import { useEffect, useId, useRef, useState } from 'react'
import type { RunDetail, RunForecast, RunStepRow, RunSummary } from '../api.ts'
import { localClock } from '../shell/clock.ts'
import { fmtCountdown, fmtMinSec, serverNow } from '../time.ts'
import { stampOf, stampText } from '../deploy/deployLogic.ts'
import { Button, ConfirmDialog, DataTable, EmptyState, MetricCard, Notice, Panel, PropertyList, SidePanel, StatusChip, TwoLine, type Column } from '../ui'
import { axisX, bars, clockOf, hm, hms, runKey, runWord, sectionTitle, SECTION_ORDER, stepState, doneCodesOf } from './orchLogic.ts'
import type { RunStages } from './orchApi.ts'

/** The run clock (REOS-128): now in UTC with the person's time, the window countdown, forecast finish, time to the rollback deadline and steps done. */
export function ClockCards({ run, fc, zone, nowMs }: { run: RunDetail; fc: RunForecast | null; zone: string; nowMs: number }) {
  const c = clockOf(fc, nowMs)
  const main = run.steps.filter(s => s.section !== 'Rollback')
  const done = main.filter(s => s.status === 'Done' || s.status === 'Skipped').length
  const local = localClock(nowMs, zone)
  return (
    <section className="or-clock" aria-label="Run clock">
      <MetricCard label="Now" value={hms(nowMs)} unit="UTC" detail={local ? `${local} for you` : 'Your time zone is UTC'} />
      <MetricCard label="Window closes in" value={<span data-testid="window-countdown">{c.windowSec === null ? '—' : fmtCountdown(c.windowSec)}</span>} detail={fc?.windowEnd ? `Window ends ${hm(Date.parse(fc.windowEnd))} UTC` : 'No window set'} />
      <MetricCard label="Forecast finish" value={fc?.forecastFinish ? hm(Date.parse(fc.forecastFinish)) : '—'} unit={fc?.forecastFinish ? 'UTC' : undefined}
        detail={fc ? `Plan ${hm(Date.parse(fc.plannedFinish))} UTC${fc.alertRaised ? ' · past the rollback deadline' : ''}` : undefined} tone={fc?.alertRaised ? 'danger' : undefined} />
      <MetricCard label="To rollback deadline" value={c.toDeadlineSec === null ? '—' : fmtCountdown(c.toDeadlineSec)}
        detail={c.deadlinePassed ? 'Passed' : c.deadlineClose ? 'Under 30 minutes' : fc?.rollbackDeadline ? `Deadline ${hm(Date.parse(fc.rollbackDeadline))} UTC` : 'No deadline'} tone={c.deadlineClose ? 'warning' : undefined} />
      <MetricCard label="Steps done" value={`${done} / ${main.length}`} detail={run.endedAt ? 'Run ended' : undefined} />
    </section>
  )
}

/** The deadline alert and the stopped forecast, as notices that are announced when they appear. */
export function DeadlineAlerts({ fc }: { fc: RunForecast | null }) {
  if (!fc) return null
  return (
    <>
      {fc.alertRaised && <Notice tone="danger" title={`The forecast crosses the rollback deadline by ${fc.crossesDeadlineByMin} min`} live>A rollback must start by {fc.rollbackDeadline ? `${hm(Date.parse(fc.rollbackDeadline))} UTC` : 'its deadline'} to fit in the window.</Notice>}
      {fc.blockedByFailed.length > 0 && <Notice tone="danger" title={`The forecast is stopped: ${fc.blockedByFailed.join(', ')} failed`} live>Nothing after it can start until it is resolved.</Notice>}
    </>
  )
}

const BW = 240
const sign = (v: number) => (v > 0 ? `+${v}` : `${v}`)

/** Plan, actual and forecast bars for every step on the window-scaled axis, with the "now" and rollback-deadline lines. The text columns say the same in words. */
export function Timeline({ run, fc, nowMs, selectedId, onSelect }: { run: RunDetail; fc: RunForecast | null; nowMs: number; selectedId: string | null; onSelect: (stepId: string) => void }) {
  const win: [number, number] = fc?.windowStart && fc.windowEnd ? [Date.parse(fc.windowStart), Date.parse(fc.windowEnd)] : [Math.min(...run.steps.map(s => Date.parse(s.plannedStartAt))), Math.max(...run.steps.map(s => Date.parse(s.plannedEndAt)))]
  const deadline = fc?.rollbackDeadline ? Date.parse(fc.rollbackDeadline) : null
  const steps = [...run.steps].sort((a, b) => SECTION_ORDER.indexOf(a.section) - SECTION_ORDER.indexOf(b.section) || a.plannedStartAt.localeCompare(b.plannedStartAt))
  const columns: Column<RunStepRow>[] = [
    { key: 'step', header: 'Step', cell: s => <TwoLine primary={<button type="button" className="or-link" onClick={e => { e.stopPropagation(); onSelect(s.stepId) }} aria-label={`Open ${s.stepCode} ${s.title}`}>{s.title}</button>} secondary={`${s.stepCode} · ${sectionTitle(s.section)}`} /> },
    { key: 'plan', header: 'Plan · UTC', mono: true, cell: s => `${hm(Date.parse(s.plannedStartAt))}–${hm(Date.parse(s.plannedEndAt))}` },
    { key: 'actual', header: 'Actual / forecast · UTC', mono: true, cell: s => {
      const f = fc?.steps.find(x => x.step === s.stepCode)
      if (s.status === 'Done' || s.status === 'Failed') return `${hm(Date.parse(s.actualStartAt!))}–${s.actualEndAt ? hm(Date.parse(s.actualEndAt)) : ''}`
      if (s.status === 'Running') return `${hm(Date.parse(s.actualStartAt!))}– (forecast end ${f?.end ? hm(Date.parse(f.end)) : '—'})`
      if (s.status === 'Skipped') return 'Skipped'
      return f?.start && f.end ? `Forecast ${hm(Date.parse(f.start))}–${hm(Date.parse(f.end))}` : '—'
    } },
    { key: 'var', header: 'Variance', align: 'end', mono: true, cell: s => {
      const v = fc?.steps.find(x => x.step === s.stepCode)?.endVarianceMin
      if (v == null || s.section === 'Rollback') return ''
      return <>{v >= 5 && <span aria-hidden="true">▲ </span>}{sign(v)} min<span className="sr-only">{v >= 15 ? ', late beyond tolerance' : v >= 5 ? ', late' : ''}</span></>
    } },
    { key: 'bars', header: <>Timeline <span className="or-axis mono">{hm(win[0])}–{hm(win[1])} UTC</span></>, cell: s => {
      const b = bars(s, fc?.steps.find(x => x.step === s.stepCode), win, nowMs, BW)
      return (
        <svg className="or-bars" viewBox={`0 0 ${BW} 18`} preserveAspectRatio="none" aria-hidden="true" focusable="false">
          <line className="or-now" x1={axisX(nowMs, win, BW)} x2={axisX(nowMs, win, BW)} y1="0" y2="18" />
          {deadline !== null && <line className="or-deadline" x1={axisX(deadline, win, BW)} x2={axisX(deadline, win, BW)} y1="0" y2="18" />}
          <rect className="or-bar plan" x={b.plan[0]} y="3" width={b.plan[1] - b.plan[0]} height="4" />
          {b.actual && <rect className={`or-bar actual${b.running ? ' running' : ''}`} x={b.actual[0]} y="9" width={b.actual[1] - b.actual[0]} height="5" />}
          {b.forecast && <line className={`or-forecast${b.forecastLate ? ' late' : ''}`} x1={b.forecast[0]} x2={b.forecast[1]} y1="11.5" y2="11.5" />}
        </svg>
      )
    } },
  ]
  return (
    <Panel title="Plan, actual and forecast" subtitle="Every step on the run window: planned, actual so far, and the forecast of what remains."
      footer={<span className="or-legend"><span><i className="or-key plan" aria-hidden="true" /> Planned</span><span><i className="or-key actual" aria-hidden="true" /> Actual</span><span><i className="or-key forecast" aria-hidden="true" /> Forecast</span>
        <span><i className="or-key now" aria-hidden="true" /> Now</span><span><i className="or-key deadline" aria-hidden="true" /> Rollback deadline{fc?.rollbackDeadline ? ` ${hm(Date.parse(fc.rollbackDeadline))} UTC` : ''}</span></span>}>
      <DataTable columns={columns} rows={steps} rowKey={s => s.stepId} caption="Plan, actual and forecast by step" selectedKey={selectedId} onSelect={s => onSelect(s.stepId)} />
    </Panel>
  )
}

// ---- the Run step drawer ----------------------------------------------------------------------------------------------------------------------
export type StepAction = 'start' | 'done' | 'fail' | 'skip'

/** The right-hand column for one step: timer, dependencies, instructions, a note, and Start / Mark done / Fail / Skip. Fail asks first (D41); Skip needs a note. */
export function StepDrawer({ run, step, fc, paused, pending, error, onAct, onClose }: {
  run: RunDetail; step: RunStepRow; fc: RunForecast | null; paused: boolean; pending: string | null; error: string | null; onAct: (a: StepAction, note: string | null) => Promise<boolean>; onClose: () => void
}) {
  // Closing the drawer gives focus back to what opened it. The opener is read while rendering, before the heading takes focus, so React's development double-mount cannot capture the heading instead.
  const opener = useRef<HTMLElement | null>(null)
  if (opener.current === null && typeof document !== 'undefined') opener.current = document.activeElement as HTMLElement | null
  useEffect(() => () => {
    const o = opener.current
    requestAnimationFrame(() => { const a = document.activeElement; if ((!a || a === document.body || !a.isConnected) && o && o !== document.body && o.isConnected) o.focus() })
  }, [])
  const head = useRef<HTMLSpanElement>(null)
  const [note, setNote] = useState('')
  const [skipping, setSkipping] = useState(false)
  const [failing, setFailing] = useState(false)
  const [local, setLocal] = useState<string | null>(null)
  const hint = useId(), errId = useId()
  useEffect(() => { setNote(''); setSkipping(false); setFailing(false); setLocal(null); head.current?.focus() }, [step.stepId])

  const nowMs = serverNow()
  const running = step.status === 'Running'
  const elapsedSec = running ? (nowMs - Date.parse(step.actualStartAt!)) / 1000 : 0
  const left = running ? step.plannedDurationMin * 60 - elapsedSec : 0
  const f = fc?.steps.find(x => x.step === step.stepCode)
  const st = stepState(step, doneCodesOf(run.steps))
  const actionable = step.canAct && !run.endedAt && (step.status === 'Scheduled' || step.status === 'Running')
  const noteMissing = skipping && !!local && !note.trim()
  const go = async (a: StepAction) => {
    if (a === 'skip' && !note.trim()) { setLocal('Reason for skipping: skipping a step needs a note saying why.'); document.getElementById('or-step-note')?.focus(); return }
    setLocal(null)
    if (await onAct(a, note.trim() || null)) { setNote(''); setSkipping(false); setFailing(false) }
  }
  const busy = (a: StepAction, idle: string) => (pending === `drawer:${a}` ? { start: 'Starting…', done: 'Marking done…', fail: 'Failing…', skip: 'Skipping…' }[a] : idle)
  const dep = (c: string) => run.steps.find(x => x.stepCode === c)
  return (
    <aside className="or-drawer" aria-label={`Step ${step.stepCode}`} onKeyDown={e => { const t = e.target as HTMLElement; if (e.key === 'Escape' && !e.defaultPrevented && !['INPUT', 'SELECT', 'TEXTAREA'].includes(t.tagName) && !failing) { e.preventDefault(); onClose() } }}>
      <SidePanel title={<span ref={head} tabIndex={-1}>{step.title}</span>} subtitle={`${step.stepCode} · ${step.ownerName ?? 'No owner'}`} closeLabel="Close step drawer" onClose={onClose}>
        <div className="or-drawer-body">
          <div className="or-drawer-state"><StatusChip tone={st.tone}>{st.word}</StatusChip>
            <span className="mono">{running ? `${fmtMinSec(elapsedSec)} running · plan ${fmtMinSec(step.plannedDurationMin * 60)}` : `Plan ${step.plannedDurationMin} min`}</span></div>
          {running && <p className={`or-pace ${left < 0 ? 'late' : ''}`}>{left < 0 ? `+${fmtMinSec(-left)} over plan pace` : `${fmtMinSec(left)} left at plan pace`}</p>}
          <PropertyList items={[
            ...(step.actualStartAt ? [{ label: 'Started', value: `${hms(Date.parse(step.actualStartAt))} UTC${step.actorName ? ` by ${step.actorName}` : ''}`, mono: true }] : []),
            ...(step.actualEndAt ? [{ label: 'Ended', value: `${hms(Date.parse(step.actualEndAt))} UTC`, mono: true }] : []),
            { label: 'Depends on', value: step.dependsOn.length === 0 ? 'Nothing' : step.dependsOn.map(c => { const d = dep(c); return `${c} ${d?.status === 'Done' ? `done ${d.actualEndAt ? hm(Date.parse(d.actualEndAt)) : ''} UTC` : d?.status === 'Skipped' ? 'skipped' : 'not done'}` }).join(' · ') },
            { label: 'Blocks', value: step.blocks.length === 0 ? 'Nothing' : step.blocks.join(', ') },
            ...(f?.end ? [{ label: 'Forecast end', value: `${hm(Date.parse(f.end))} UTC`, mono: true }] : []),
            ...(step.note ? [{ label: 'Note', value: step.note }] : []),
          ]} />
          {step.instructions && <><h3 className="or-h3">Instructions</h3><ol className="or-instr">{step.instructions.split('\n').filter(Boolean).map((l, i) => <li key={i}>{l}</li>)}</ol></>}
          {actionable && (
            <div className="or-actions">
              <label className="or-field"><span>{skipping ? 'Reason for skipping (required)' : 'Note'}</span>
                <input id="or-step-note" value={note} onChange={e => setNote(e.target.value)} placeholder={skipping ? 'Why is this step skipped?' : 'Visible to everyone on the call'}
                  aria-required={skipping} aria-invalid={noteMissing || undefined} aria-describedby={noteMissing ? `${errId} ${hint}` : hint} /></label>
              <div className="or-button-row">
                {step.status === 'Scheduled' && <Button variant="primary" disabled={!!pending || paused} onClick={() => void go('start')}>{busy('start', 'Start step')}</Button>}
                {running && <Button variant="primary" disabled={!!pending} onClick={() => void go('done')}>{busy('done', 'Mark done')}</Button>}
                {running && <Button variant="destructive" disabled={!!pending} onClick={() => setFailing(true)}>Fail…</Button>}
                {step.status === 'Scheduled' && (skipping ? <Button disabled={!!pending} onClick={() => void go('skip')}>{busy('skip', 'Confirm skip')}</Button> : <Button disabled={!!pending} onClick={() => { setSkipping(true); document.getElementById('or-step-note')?.focus() }}>Skip…</Button>)}
              </div>
              {paused && step.status === 'Scheduled' && <p className="or-note">The run is paused, so no step can start. Resume it first.</p>}
              <p id={hint} className="or-note">Skip needs a note. Fail leaves the rollback steps ready to run.</p>
            </div>
          )}
          {(local || error) && <Notice tone="danger" title="That did not work" live><span id={errId}>{local ?? error}</span></Notice>}
        </div>
      </SidePanel>
      <ConfirmDialog open={failing} title={`Fail ${step.stepCode}?`} confirmLabel="Fail step" pendingLabel="Failing…" destructive pending={pending === 'drawer:fail'}
        onCancel={() => setFailing(false)} onConfirm={() => void go('fail')}>
        Failing {step.title} stops the forecast and makes the rollback steps ready to run. It cannot be undone.
      </ConfirmDialog>
    </aside>
  )
}

// ---- Run history and Configuration tabs ------------------------------------------------------------------------------------------------------
export function RunHistory({ runs, currentId, zone, onPick }: { runs: RunSummary[]; currentId: string | null; zone: string; onPick: (r: RunSummary) => void }) {
  const columns: Column<RunSummary>[] = [
    { key: 'run', header: 'Run', mono: true, cell: r => runKey(r.id) },
    { key: 'mode', header: 'Mode', cell: r => r.mode },
    { key: 'started', header: 'Started', cell: r => { const s = stampOf(r.startedAt, zone); return <TwoLine primary={s.utc} secondary={s.local ? `${s.local} for you` : undefined} /> } },
    { key: 'ended', header: 'Ended', cell: r => { if (!r.endedAt) return <span className="os-muted">Open</span>; const s = stampOf(r.endedAt, zone); return <TwoLine primary={s.utc} secondary={s.local ? `${s.local} for you` : undefined} /> } },
    { key: 'outcome', header: 'Outcome', cell: r => { const w = runWord({ paused: r.pausedAt != null, endedAt: r.endedAt, outcome: r.outcome }); return <StatusChip tone={w.tone}>{w.word}</StatusChip> } },
  ]
  if (runs.length === 0) return <EmptyState title="No runs yet">Start a rehearsal or a live run from the pipeline; every run stays here.</EmptyState>
  return <Panel title="Run history" subtitle="Every run of this release, newest first. Choose one to open it in the pipeline."><DataTable columns={columns} rows={runs} rowKey={r => r.id} caption="Runs of this release" selectedKey={currentId} onSelect={onPick}
    footer={`${runs.length} ${runs.length === 1 ? 'run' : 'runs'}`} /></Panel>
}

export function RunConfiguration({ stages, fc, zone }: { stages: RunStages; fc: RunForecast | null; zone: string }) {
  const at = (iso: string | null) => (iso ? stampText(stampOf(iso, zone)) : '—')
  return (
    <div className="or-config">
      <Panel title="Run configuration" subtitle="Read only. The plan is edited on the release; a run never changes it.">
        <PropertyList items={[
          { label: 'Run', value: runKey(stages.runId), mono: true }, { label: 'Mode', value: stages.mode }, { label: 'Version', value: String(stages.version), mono: true },
          { label: 'Started', value: at(stages.startedAt) }, { label: 'Window opens', value: at(fc?.windowStart ?? null) }, { label: 'Window closes', value: at(fc?.windowEnd ?? null) },
          { label: 'Planned finish', value: at(fc?.plannedFinish ?? null) }, { label: 'Rollback needs', value: fc ? `${fc.rollbackPlannedMin} min` : '—', mono: true }, { label: 'Rollback deadline', value: at(fc?.rollbackDeadline ?? null) },
          { label: 'Stages', value: stages.stages.map(s => sectionTitle(s.section)).join(', ') || 'None' },
        ]} />
      </Panel>
      <Panel title="Failure policy" subtitle="How a run behaves when something fails.">
        <PropertyList items={[
          { label: 'On failure', value: 'A failed step stops the forecast; steps after it cannot start. The rollback steps become ready to run.' },
          { label: 'Rollback', value: 'Called by a person. Automatic rollback applies to a deployment, when it is armed on its monitoring page.' },
          { label: 'A paused run', value: 'No step can start. Steps already running can finish or fail.' },
        ]} />
      </Panel>
    </div>
  )
}

export const VariablesTab = () => (
  <EmptyState title="No variables or secrets are kept here">Credentials never live in ReleaseOS: the deployment tool holds them, and ReleaseOS only calls its signed webhook. Run parameters belong in the runbook step instructions.</EmptyState>
)
