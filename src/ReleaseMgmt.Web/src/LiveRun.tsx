import { useEffect, useState } from 'react'
import { endRun, getForecast, getRun, getRuns, runStepAction, startRun, type ApiError, type RunDetail, type RunForecast, type RunStepRow } from './api'
import { Conflict, isConflict } from './Conflict'
import { errMsg, plural } from './format'
import { fmtCountdown, fmtDay, fmtHM, fmtHMS, fmtMinSec, serverNow, zoneAbbr } from './time'
import type { Mode } from './route'
import type { Selection } from './Planning'

const SECTIONS = ['PreCheck', 'Deploy', 'Verify', 'Hypercare', 'Rollback']
const SECTION_LABEL: Record<string, string> = { PreCheck: 'Pre-check', Deploy: 'Deploy', Verify: 'Verify', Hypercare: 'Hypercare', Rollback: 'Rollback (only if called)' }

/** Loads the run for a train and mode (the open Live run, else the latest of that mode) with its forecast, and refetches when asked. */
export function useRun(trainId: string | null, mode: Mode, refreshKey: number) {
  const [run, setRun] = useState<RunDetail | null | undefined>(undefined)
  const [forecast, setForecast] = useState<RunForecast | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => {
    if (!trainId || mode === 'plan') { setRun(undefined); setForecast(null); return }
    let live = true
    ;(async () => {
      try {
        const want = mode === 'live' ? 'Live' : 'Rehearsal'
        const list = (await getRuns(trainId)).filter(r => r.mode === want)
        const pick = list.find(r => r.endedAt === null) ?? list[0]
        if (!pick) { if (live) { setRun(null); setForecast(null) } return }
        const [d, f] = await Promise.all([getRun(pick.id), getForecast(pick.id)])
        if (live) { setRun(d); setForecast(f); setErr(null) }
      } catch (e) { if (live) setErr(errMsg(e)) }
    })()
    return () => { live = false }
  }, [trainId, mode, refreshKey])
  return { run, forecast, err }
}

/** Re-renders every second on the server's clock, so the countdowns move without new data. */
function useTick() {
  const [, set] = useState(0)
  useEffect(() => { const t = window.setInterval(() => set(n => n + 1), 1000); return () => window.clearInterval(t) }, [])
}

const varCls = (v: number | null) => v === null ? 'muted' : v >= 15 ? 'bad' : v >= 5 ? 'warn' : ''
const sign = (v: number) => v > 0 ? `+${v}` : `${v}`

function stateOf(s: RunStepRow, nowMs: number, doneCodes: Set<string>) {
  switch (s.status) {
    case 'Done': return { text: '✓ Done', cls: 'ok' }
    case 'Failed': return { text: '✗ Failed', cls: 'bad' }
    case 'Skipped': return { text: '▲ Skipped', cls: 'warn' }
    case 'Running': return { text: `● ${fmtMinSec((nowMs - Date.parse(s.actualStartAt!)) / 1000)}`, cls: 'accent' }
    default: return s.dependsOn.every(d => doneCodes.has(d)) ? { text: '○ Ready', cls: '' } : { text: '○ Waiting', cls: 'muted' }
  }
}

// ---- per-row bars on the window-scaled axis: planned (light), actual (solid), forecast (dashed) ---------------------------------------------
const W = 200
function Bars({ s, f, win, nowMs, deadlineMs }: { s: RunStepRow; f: RunForecast['steps'][number] | undefined; win: [number, number]; nowMs: number; deadlineMs: number | null }) {
  const [a, b] = win, span = Math.max(1, b - a)
  const x = (ms: number) => Math.max(0, Math.min(W, ((ms - a) / span) * W))
  const bar = (from: number, to: number, y: number, h: number, cls: string) => <rect className={cls} x={x(from)} y={y} width={Math.max(2, x(to) - x(from))} height={h} />
  const ps = Date.parse(s.plannedStartAt), pe = Date.parse(s.plannedEndAt)
  return (
    <svg className="gantt" width={W} height="18" viewBox={`0 0 ${W} 18`} aria-hidden="true">
      <line className="g-now" x1={x(nowMs)} x2={x(nowMs)} y1="0" y2="18" strokeDasharray="2 2" />
      {deadlineMs !== null && <line className="g-deadline" x1={x(deadlineMs)} x2={x(deadlineMs)} y1="0" y2="18" strokeDasharray="2 2" />}
      {bar(ps, pe, 3, 4, 'g-plan')}
      {s.actualStartAt && bar(Date.parse(s.actualStartAt), s.actualEndAt ? Date.parse(s.actualEndAt) : nowMs, 9, 5, s.status === 'Running' ? 'g-running' : 'g-actual')}
      {s.status === 'Running' && f?.end && <line className="g-fc" x1={x(nowMs)} x2={x(Date.parse(f.end))} y1="11.5" y2="11.5" strokeWidth="2" strokeDasharray="3 2" />}
      {(s.status === 'Scheduled') && f?.start && f.end && <line className="g-fc late" x1={x(Date.parse(f.start))} x2={x(Date.parse(f.end))} y1="11.5" y2="11.5" strokeWidth="2" strokeDasharray="3 2" />}
    </svg>
  )
}

export function LiveRun({ trainId, mode, canPlan, data, selection, onSelect, onMode, onChanged }: {
  trainId: string; mode: Mode; canPlan: boolean; data: ReturnType<typeof useRun>; selection: Selection; onSelect: (s: Selection) => void; onMode: (m: Mode) => void; onChanged: () => void
}) {
  useTick()
  const { run, forecast: fc, err } = data
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const label = mode === 'live' ? 'Live' : 'Rehearsal'

  const modeNav = (
    <nav aria-label="Runbook mode" className="tabs mode-tabs">
      {(['plan', 'rehearsal', 'live'] as Mode[]).map(m => <a key={m} href="#" aria-current={mode === m ? 'page' : undefined} onClick={e => { e.preventDefault(); onMode(m) }}>{m === 'plan' ? 'Plan' : m === 'rehearsal' ? 'Rehearsal' : 'Live'}</a>)}
    </nav>
  )
  if (err) return <>{modeNav}<p className="bad" role="alert">✗ {err}</p></>
  if (run === undefined) return <>{modeNav}<p className="muted">Loading…</p></>
  if (run === null) {
    const start = async () => { setProblem(null); try { await startRun(trainId, mode === 'live' ? 'Live' : 'Rehearsal'); onChanged() } catch (e) { setProblem(errMsg(e)) } }
    return <>{modeNav}<h1>{label} run</h1><p className="muted">There is no {label.toLowerCase()} run for this train yet.{mode === 'live' ? ' A live run needs the train to be Executing and a runbook with steps.' : ' A rehearsal can start any time the runbook has steps.'}</p>
      {canPlan && <p><button type="button" className="text" onClick={start}>Start {label.toLowerCase()} run</button></p>}
      {problem && <p className="bad" role="alert">✗ {problem}</p>}</>
  }

  // "now" is the server's clock, so a wrong browser clock changes nothing on this screen.
  const nowMs = serverNow()
  const asOfMs = fc ? Date.parse(fc.asOf) : nowMs
  const sinceAsOf = (nowMs - asOfMs) / 1000
  const windowSec = fc?.windowClosesInSec != null ? fc.windowClosesInSec - sinceAsOf : null
  const deadlineMs = fc?.rollbackDeadline ? Date.parse(fc.rollbackDeadline) : null
  const toDeadline = deadlineMs !== null ? (deadlineMs - nowMs) / 1000 : null
  const doneCodes = new Set(run.steps.filter(s => s.status === 'Done' || s.status === 'Skipped').map(s => s.stepCode))
  const main = run.steps.filter(s => s.section !== 'Rollback')
  const done = main.filter(s => s.status === 'Done' || s.status === 'Skipped').length
  const win: [number, number] = fc?.windowStart && fc.windowEnd ? [Date.parse(fc.windowStart), Date.parse(fc.windowEnd)] : [Math.min(...run.steps.map(s => Date.parse(s.plannedStartAt))), Math.max(...run.steps.map(s => Date.parse(s.plannedEndAt)))]
  const fcRow = (code: string) => fc?.steps.find(x => x.step === code)
  const end = async (outcome: 'Completed' | 'RolledBack' | 'Aborted') => { setProblem(null); setConflict(null); try { await endRun(run.id, outcome, run.version); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setProblem(errMsg(e)) } }
  const lateMin = fc ? fc.steps.reduce((m, s) => Math.max(m, s.endVarianceMin ?? 0), 0) : 0

  return (
    <>
      <div className="header-line">
        <span className="cap">{label} run · started {fmtHM(run.startedAt)} by {run.startedBy ?? 'unknown'}{run.endedAt ? ` · ended ${fmtHM(run.endedAt)} (${run.outcome})` : ''}</span>
        {modeNav}
      </div>
      <h1>{label} run</h1>

      <section className="metrics" aria-label="Run clock">
        <div><div className="mono big">{fmtHMS(nowMs)}</div><div className="muted">Now · {fmtDay(nowMs)} {zoneAbbr(nowMs)}</div></div>
        <div><div className="mono big" data-testid="window-countdown">{windowSec === null ? '—' : fmtCountdown(windowSec)}</div><div className="muted">Window closes {fc?.windowEnd ? fmtHM(fc.windowEnd) : '—'}</div></div>
        <div><div className={`mono big ${fc?.alertRaised ? 'bad' : ''}`}>{fc?.forecastFinish ? fmtHM(fc.forecastFinish) : '—'}</div><div className="muted">Finish forecast · plan {fc ? fmtHM(fc.plannedFinish) : '—'}</div></div>
        <div><div className={`mono big ${toDeadline !== null && toDeadline < 1800 ? 'warn' : ''}`}>{toDeadline === null ? '—' : fmtCountdown(toDeadline)}</div><div className="muted">To rollback deadline</div></div>
        <div><div className="mono big">{done} / {main.length}</div><div className="muted">Steps done</div></div>
      </section>

      {fc?.alertRaised && (
        <section role="alert" className="alert-line">
          <span className="bad">Forecast crosses the rollback deadline by {fc.crossesDeadlineByMin} min.</span> Rollback must start by {fmtHM(fc.rollbackDeadline!)}.
        </section>)}
      {fc && fc.blockedByFailed.length > 0 && <section role="alert" className="alert-line"><span className="bad">The forecast is stopped: {fc.blockedByFailed.join(', ')} failed.</span> Nothing after it can start until it is resolved.</section>}
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
      {conflict && <Conflict error={conflict} what="run" />}

      <table className="grid runtable">
        <thead><tr><th>Step</th><th>Title · owner · plan</th><th>Actual / fc</th><th className="n">Var</th><th>State</th>
          <th aria-hidden="true" className="axis"><span className="mono">{fmtHM(win[0])}</span><span className="mono">{fmtHM(win[1])}</span></th></tr></thead>
        <tbody>
          {SECTIONS.map(sec => {
            const rows = run.steps.filter(s => s.section === sec)
            if (rows.length === 0) return null
            return [
              <tr key={sec} className="sec"><td colSpan={6}>{SECTION_LABEL[sec]}</td></tr>,
              ...rows.map(s => {
                const f = fcRow(s.stepCode); const st = stateOf(s, nowMs, doneCodes)
                const sel = selection?.kind === 'step' && selection.id === s.stepId
                const actual = s.status === 'Done' || s.status === 'Failed' ? `${fmtHM(s.actualStartAt!)}–${fmtHM(s.actualEndAt!)}`
                  : s.status === 'Running' ? `${fmtHM(s.actualStartAt!)}–` : null
                const fcText = f?.start && f.end ? `${fmtHM(f.start)}–${fmtHM(f.end)}` : ''
                const lateStart = s.actualStartAt ? Math.floor((Date.parse(s.actualStartAt) - Date.parse(s.plannedStartAt)) / 60000) : 0
                return (
                  <tr key={s.stepId} aria-selected={sel} className={sel ? 'selected' : undefined}>
                    <td className={`mono ${s.status === 'Running' ? 'accent' : 'muted'}`}>{s.stepCode}</td>
                    <td><button type="button" className="text plainlink" onClick={() => onSelect({ kind: 'step', id: s.stepId })}>{s.title}</button>
                      <div className="muted small">{s.ownerName ?? '—'} · {fmtHM(s.plannedStartAt)} · {s.plannedDurationMin} min{s.dependsOn.length > 0 && s.status === 'Scheduled' ? ` · after ${s.dependsOn.join(', ')}` : ''}{lateStart >= 5 && s.actualStartAt ? <span className="warn"> · {lateStart} late</span> : null}</div></td>
                    <td className="mono small">{actual ?? ''}{s.status === 'Running' && fcText ? <span className="muted">{f?.end ? fmtHM(f.end) : ''}</span> : null}{!actual && fcText ? <span className="muted">{fcText}</span> : null}</td>
                    <td className={`n small ${varCls(f?.endVarianceMin ?? null)}`}>{f?.endVarianceMin != null && s.section !== 'Rollback' ? sign(f.endVarianceMin) : ''}</td>
                    <td className={`small ${st.cls}`}>{st.text}</td>
                    <td><Bars s={s} f={f} win={win} nowMs={nowMs} deadlineMs={deadlineMs} /></td>
                  </tr>)
              }),
            ]
          })}
        </tbody>
      </table>
      <p className="muted legend"><span className="lg plan" /> planned <span className="lg actual" /> actual <span className="lg fc" /> forecast <span className="accent">┆ now</span> <span className="bad">┆ rollback deadline{fc?.rollbackDeadline ? ` ${fmtHM(fc.rollbackDeadline)}` : ''}</span> · {plural(lateMin > 0 ? lateMin : 0, 'min')} behind the plan at worst</p>

      {canPlan && !run.endedAt && (
        <p className="actions-col">
          <button type="button" className="text" onClick={() => end('Completed')}>End run: completed</button>
          <button type="button" className="text" onClick={() => end('RolledBack')}>End run: rolled back</button>
          <button type="button" className="text destructive" onClick={() => end('Aborted')}>End run: aborted</button>
        </p>)}
    </>
  )
}

// ---- the running-step drawer (right pane): timer, dependencies, instructions, and Done / Fail / Skip ------------------------------------------
export function RunStepDrawer({ stepId, data, onClose, onChanged }: { stepId: string; data: ReturnType<typeof useRun>; onClose: () => void; onChanged: () => void }) {
  useTick()
  const { run, forecast: fc } = data
  const [note, setNote] = useState('')
  const [skipping, setSkipping] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  useEffect(() => { setNote(''); setSkipping(false); setErr(null); setConflict(null) }, [stepId])
  const s = run?.steps.find(x => x.stepId === stepId)
  const head = <div className="section-head"><span className="cap">Step · <span className="mono">{s?.stepCode ?? ''}</span></span><button type="button" className="text quiet" aria-label="Close inspector" onClick={onClose}>Close</button></div>
  if (!run || !s) return <>{head}<p className="muted">Loading…</p></>

  const nowMs = serverNow()
  const running = s.status === 'Running'
  const elapsed = running ? (nowMs - Date.parse(s.actualStartAt!)) / 1000 : 0
  const left = running ? s.plannedDurationMin * 60 - elapsed : 0
  const f = fc?.steps.find(x => x.step === s.stepCode)
  const act = async (action: 'start' | 'done' | 'fail' | 'skip') => {
    setErr(null); setConflict(null)
    if (action === 'skip' && !note.trim()) { setErr('Skipping a step needs a note saying why.'); return }
    try { await runStepAction(run.id, s.stepId, action, note.trim() || null, s.version); setNote(''); setSkipping(false); onChanged() }
    catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const stepCls = s.status === 'Done' ? 'ok' : s.status === 'Failed' ? 'bad' : s.status === 'Running' ? 'accent' : ''
  return (
    <>
      {head}
      <h2>{s.title}</h2>
      <p className="muted">{s.ownerName ?? '—'}</p>
      <div className="metrics drawer-metrics">
        <div><div className={`mono big ${stepCls}`}>{running ? fmtMinSec(elapsed) : s.status}</div><div className="muted">{running ? `running · plan ${fmtMinSec(s.plannedDurationMin * 60)}` : `plan ${s.plannedDurationMin} min`}</div></div>
        {running && <div><div className={`mono big ${left < 0 ? 'bad' : ''}`}>{left < 0 ? `+${fmtMinSec(-left)}` : fmtMinSec(left)}</div><div className="muted">{left < 0 ? 'over plan pace' : 'left at plan pace'}</div></div>}
      </div>
      <dl className="facts">
        {s.actualStartAt && <><dt>Started</dt><dd className="mono">{fmtHMS(s.actualStartAt)} <span className="muted">{s.actorName ? `by ${s.actorName}` : ''}</span></dd></>}
        {s.actualEndAt && <><dt>Ended</dt><dd className="mono">{fmtHMS(s.actualEndAt)}</dd></>}
        <dt>Depends on</dt><dd>{s.dependsOn.length === 0 ? '—' : s.dependsOn.map(c => { const d = run.steps.find(x => x.stepCode === c); return <span key={c}><span className="mono">{c}</span> <span className={d?.status === 'Done' || d?.status === 'Skipped' ? 'ok' : 'muted'}>{d?.status === 'Done' ? `✓ done ${fmtHM(d.actualEndAt!)}` : d?.status === 'Skipped' ? '▲ skipped' : '○ not done'}</span> </span> })}</dd>
        <dt>Blocks</dt><dd>{s.blocks.length === 0 ? '—' : <><span className="mono">{s.blocks.join(', ')}</span> <span className="muted">· {plural(s.blocks.length, 'step')}</span></>}</dd>
        {f?.end && <><dt>Forecast end</dt><dd className="mono">{fmtHM(f.end)}</dd></>}
        {s.note && <><dt>Note</dt><dd>{s.note}</dd></>}
      </dl>
      {s.instructions && <><p className="cap">Instructions</p><ol className="instr">{s.instructions.split('\n').filter(Boolean).map((l, i) => <li key={i}>{l}</li>)}</ol></>}
      {s.canAct && !run.endedAt && (s.status === 'Scheduled' || s.status === 'Running') && (
        <>
          <p className="inline-form"><label className="cap" htmlFor="step-note">Note</label>
            <input id="step-note" className="line block" placeholder={skipping ? 'Why is this step skipped? (required)' : 'Visible to everyone on the call'} value={note} onChange={e => setNote(e.target.value)} /></p>
          <p className="actions-col">
            {s.status === 'Scheduled' && <button type="button" className="text" onClick={() => act('start')}>Start step</button>}
            {running && <button type="button" className="text" onClick={() => act('done')}>Mark done</button>}
            {running && <button type="button" className="text destructive" onClick={() => act('fail')}>Fail</button>}
            {s.status === 'Scheduled' && (skipping ? <button type="button" className="text" onClick={() => act('skip')}>Confirm skip</button> : <button type="button" className="text" onClick={() => setSkipping(true)}>Skip…</button>)}
          </p>
          <p className="muted">Skip needs a note. Fail leaves the rollback steps ready to run.</p>
        </>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="step" />}
    </>
  )
}
