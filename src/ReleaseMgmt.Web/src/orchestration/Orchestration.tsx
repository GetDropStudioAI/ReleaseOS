import { useEffect, useMemo, useState } from 'react'
import { Pause, Play, Activity } from 'lucide-react'
import { endRun, runStepAction, startRun, type RunStepRow, type StreamRow } from '../api.ts'
import { announce } from '../announce.tsx'
import { isConflict } from '../Conflict.tsx'
import { errMsg } from '../format.ts'
import type { LiveState } from '../live.ts'
import type { Mode, Route } from '../route.ts'
import { browserZone } from '../shell/clock.ts'
import { serverNow } from '../time.ts'
import { useAction } from '../useAction.ts'
import { stampOf, stampText } from '../deploy/deployLogic.ts'
import { Button, ConfirmDialog, ConflictNotice, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, PageHeading, Panel, ReadOnlyNotice, StaleBanner, StatusChip, TabPanel, Tabs, Notice } from '../ui'
import { getReleaseKey, pauseRun, resumeRun } from './orchApi.ts'
import { defaultStage, doneCodesOf, elapsed, lastEvent, runKey, runWord, sectionTitle, stageCards } from './orchLogic.ts'
import { CanaryPanel, StageCards, StepsTable } from './Pipeline.tsx'
import { ClockCards, DeadlineAlerts, RunConfiguration, RunHistory, StepDrawer, Timeline, VariablesTab, type StepAction } from './RunPanels.tsx'
import { useOrchestration, useTick, type RunMode } from './useOrchestration.ts'
import './orchestration.css'

/**
 * The Orchestration destination (REOS-98) with the Rehearsal and Live run views inside it (REOS-128). `/orchestration/{trainId}/{live|rehearsal}[/steps/{stepId}]` names the
 * release, the mode and the open Run step drawer. It reads the existing run endpoints; every change goes through RunService (start, step actions, pause, resume, end), so
 * the plan is never overwritten by actuals (rule 7). Built to the choices in QUESTIONS Q-98a to Q-98f.
 */
const TABS = [{ id: 'pipeline', label: 'Pipeline' }, { id: 'history', label: 'Run history' }, { id: 'config', label: 'Configuration' }, { id: 'vars', label: 'Variables & secrets' }]
const OUTCOME = {
  Completed: { title: 'End the run as completed?', confirm: 'End run: completed', pending: 'Ending…', destructive: false, body: 'Completed means every step that was needed is done. The run closes and cannot be reopened.' },
  RolledBack: { title: 'End the run as rolled back?', confirm: 'End run: rolled back', pending: 'Ending…', destructive: true, body: 'The release did not stay deployed. The run closes and a post-implementation review is created. This cannot be undone.' },
  Aborted: { title: 'Abort this run?', confirm: 'Abort run', pending: 'Aborting…', destructive: true, body: 'Aborting stops the run where it is. Ending a run cannot be undone.' },
} as const
type Outcome = keyof typeof OUTCOME

export function Orchestration({ trains, trainId, mode, selection, live, refreshKey, canPlan, canAudit, onRoute, onChanged, onOpenTrain, onOpenDeployment, onOpenLogs }: {
  trains: StreamRow[] | null; trainId: string | null; mode: Mode; selection: Route['selection']; live: LiveState; refreshKey: number; canPlan: boolean; canAudit: boolean
  onRoute: (r: Partial<Route>, replace?: boolean) => void; onChanged: () => void; onOpenTrain: (trainId: string) => void; onOpenDeployment: (id: string) => void; onOpenLogs: () => void
}) {
  useTick()
  const zone = browserZone()
  const train = trains?.find(t => t.id === trainId) ?? null
  const runMode: RunMode = mode === 'live' || mode === 'rehearsal' ? mode : train?.status === 'Executing' ? 'live' : 'rehearsal'
  const [tab, setTab] = useState('pipeline')
  const [runId, setRunId] = useState<string | null>(null)
  const [stageSel, setStageSel] = useState<string | null>(null)
  const { data, error, reload } = useOrchestration(trainId, runMode, runId, refreshKey)
  const { run: act, pending } = useAction()
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)
  const [pausing, setPausing] = useState(false)
  const [reason, setReason] = useState('')
  const [reasonError, setReasonError] = useState<string | null>(null)
  const [ending, setEnding] = useState<Outcome | null>(null)
  const [key, setKey] = useState<string | null>(null)

  // A train named by nothing opens the first Executing release, else the first one; the choice replaces the address, so Back is not trapped.
  useEffect(() => {
    if (trainId || !trains || trains.length === 0) return
    const pick = trains.find(t => t.status === 'Executing') ?? trains[0]
    onRoute({ trainId: pick.id, mode: pick.status === 'Executing' ? 'live' : 'rehearsal', selection: null }, true)
  }, [trainId, trains])   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { setRunId(null); setStageSel(null); setProblem(null); setConflict(false); setTab('pipeline') }, [trainId, runMode])
  useEffect(() => { setKey(null); if (trainId) getReleaseKey(trainId).then(r => setKey(r?.key ?? null)).catch(() => setKey(null)) }, [trainId])

  const run = data?.run ?? null, fc = data?.forecast ?? null, stages = data?.stages ?? null
  const nowMs = serverNow()
  const cards = useMemo(() => (run && stages ? stageCards(stages.run, run.steps, nowMs) : []), [run, stages, Math.floor(nowMs / 1000)])   // eslint-disable-line react-hooks/exhaustive-deps
  const stepId = selection?.kind === 'step' ? selection.id : null
  const step = run?.steps.find(s => s.stepId === stepId) ?? null
  const section = stageSel && cards.some(c => c.section === stageSel) ? stageSel : defaultStage(cards)
  // The stage in view is chosen once per run (the open step's stage, else the one running) and then stays where the person put it: finishing the last step of a
  // stage must not move the table away from under the step drawer.
  const firstSection = step?.section ?? defaultStage(cards)
  useEffect(() => { if (stageSel === null && cards.length > 0 && firstSection) setStageSel(firstSection) }, [stageSel, cards.length, firstSection])
  const deployments = stages?.deployments ?? []
  const paused = !!stages?.run.paused
  const ended = !!run?.endedAt
  const word = stages ? runWord(stages.run) : null
  const label = runMode === 'live' ? 'live' : 'rehearsal'

  const after = () => { onChanged(); reload() }
  const fail = (e: unknown) => { if (isConflict(e)) setConflict(true); else setProblem(errMsg(e)); reload() }
  const stepAct = async (s: RunStepRow, action: StepAction, note: string | null, busyKey: string): Promise<boolean> =>
    (await act(busyKey, async () => {
      setProblem(null); setConflict(false)
      try { await runStepAction(run!.id, s.stepId, action, note, s.version); announce(`${s.stepCode} ${action === 'start' ? 'started' : action === 'done' ? 'done' : action === 'fail' ? 'failed' : 'skipped'}.`); after(); return true }
      catch (e) { fail(e); return false }
    })) === true
  const start = () => act('start-run', async () => { setProblem(null); try { await startRun(trainId!, runMode === 'live' ? 'Live' : 'Rehearsal'); announce(`${label} run started.`); setRunId(null); after() } catch (e) { setProblem(errMsg(e)) } })
  const finish = (o: Outcome) => act('end-run', async () => {
    setProblem(null); setConflict(false)
    try { await endRun(run!.id, o, run!.version); announce(`Run ended: ${o === 'RolledBack' ? 'rolled back' : o.toLowerCase()}.`); setEnding(null); after() } catch (e) { setEnding(null); fail(e) }
  })
  const pause = () => {
    const text = reason.trim()
    if (text.length === 0 || text.length > 500) { setReasonError('Say why the run is paused, in 1 to 500 characters.'); return }
    void act('pause', async () => {
      setProblem(null); setConflict(false)
      try { await pauseRun(run!.id, text, run!.version); announce('Run paused.'); setPausing(false); setReason(''); after() } catch (e) { setPausing(false); fail(e) }
    })
  }
  const resume = () => act('resume', async () => { setProblem(null); setConflict(false); try { await resumeRun(run!.id, run!.version); announce('Run resumed.'); after() } catch (e) { fail(e) } })

  const canary = deployments.find(d => d.strategy === 'Canary' && d.state === 'Running') ?? deployments[0]
  const trail = ['Workspace', 'Orchestration', ...(key ? [key] : train ? [train.title] : [])]
  const startedAt = run ? stampText(stampOf(run.startedAt, zone)) : ''
  const sinceMs = run ? (ended ? Date.parse(run.endedAt!) : nowMs) - Date.parse(run.startedAt) : 0

  const actions = run && !ended ? (
    <>
      {canPlan
        ? (paused ? <Button icon={Play} disabled={!!pending} onClick={() => void resume()}>{pending === 'resume' ? 'Resuming…' : 'Resume run'}</Button> : <Button icon={Pause} disabled={!!pending} onClick={() => { setReason(''); setReasonError(null); setPausing(true) }}>Pause run</Button>)
        : <DisabledAction icon={Pause} reason="Only an RTE or Release Manager can pause a run.">Pause run</DisabledAction>}
      {canary ? <Button variant="primary" icon={Activity} onClick={() => onOpenDeployment(canary.id)}>View deployment</Button>
        : <DisabledAction variant="primary" icon={Activity} reason="No deployment is linked to this run.">View deployment</DisabledAction>}
    </>
  ) : run && canary ? <Button variant="primary" icon={Activity} onClick={() => onOpenDeployment(canary.id)}>View deployment</Button> : undefined

  const selectors = trains && trains.length > 0 && trainId ? (
    <div className="or-selectors">
      <FilterSelect label="Release" value={trainId} onChange={id => { const t = trains.find(x => x.id === id); onRoute({ trainId: id, mode: t?.status === 'Executing' ? 'live' : 'rehearsal', selection: null }) }}
        options={trains.map(t => ({ value: t.id, label: `${t.title} · ${t.status}` }))} />
      <FilterSelect label="Run mode" value={runMode} onChange={m => onRoute({ mode: m as Mode, selection: null })} options={[{ value: 'live', label: 'Live run' }, { value: 'rehearsal', label: 'Rehearsal run' }]} />
    </div>
  ) : null

  return (
    <div className="os-page or-page">
      <PageHeading trail={trail} title="Orchestration pipeline" description={train ? `${train.title} · Coordinated progressive delivery, stage by stage.` : 'Coordinated progressive delivery across your releases.'} actions={actions} />
      {live !== 'live' && data && <StaleBanner offline since={data.at.slice(11, 19) + ' UTC'} onRefresh={reload} />}
      {trains && trains.length === 0 && <EmptyState title="No releases yet">Orchestration shows a release's run. Create or import a release first.</EmptyState>}
      {!trains && <LoadingState what="the releases" />}
      {error && <ErrorState what="The run" message={error} onRetry={reload} />}
      {trainId && !data && !error && <LoadingState what="the run" />}
      {data && !canPlan && <ReadOnlyNotice>You can follow this run but not start, pause or end it. An RTE or Release Manager does that.</ReadOnlyNotice>}
      {problem && <Notice tone="danger" title="That did not work" live>{problem}</Notice>}
      {conflict && <ConflictNotice onUseTheirs={() => { setConflict(false); reload() }} />}

      {data && (
        <>
          <div className="or-context">
            <div className="or-identity">
              {word && run ? <><StatusChip tone={word.tone}>{word.word}</StatusChip><span className="or-ref mono">{runKey(run.id)}</span>
                <span className="or-started">Started by {run.startedBy ?? 'unknown'} at {startedAt}</span><span className="or-elapsed mono">{ended ? 'Ran' : 'Elapsed'} {elapsed(sinceMs / 1000)}</span></>
                : <span className="or-started">No {label} run yet</span>}
            </div>
            {selectors}
          </div>
          <Tabs tabs={TABS.map(t => (t.id === 'history' ? { ...t, count: data.runs.length } : t))} value={tab} onChange={setTab} label="Orchestration" group="or" />
          <TabPanel id={tab} group="or">
            {tab === 'pipeline' && !run && (
              <EmptyState title={`There is no ${label} run for this release yet`}
                action={canPlan
                  ? (runMode === 'live' && train?.status !== 'Executing'
                    ? <DisabledAction variant="primary" icon={Play} reason="A live run needs the release to be Executing and its runbook to have steps.">Start live run</DisabledAction>
                    : <Button variant="primary" icon={Play} disabled={!!pending} onClick={() => void start()}>{pending === 'start-run' ? 'Starting…' : `Start ${label} run`}</Button>)
                  : <DisabledAction variant="primary" icon={Play} reason="Only an RTE or Release Manager can start a run.">{`Start ${label} run`}</DisabledAction>}>
                {runMode === 'live' ? 'A live run needs the release to be Executing and a runbook with steps.' : 'A rehearsal can start any time the runbook has steps.'}
                {train && <> <button type="button" className="or-link" onClick={() => onOpenTrain(train.id)}>Open the release</button></>}
              </EmptyState>)}
            {tab === 'pipeline' && run && stages && (
              <div className={`or-layout${step ? ' with-drawer' : ''}`}>
                <div className="or-main">
                  {paused && <Notice tone="warning" title={`Run paused${stages.run.pausedBy ? ` by ${stages.run.pausedBy}` : ''}`} live>{stages.run.pauseReason}. No step can start until it is resumed; steps already running can finish or fail.</Notice>}
                  <DeadlineAlerts fc={fc} />
                  <ClockCards run={run} fc={fc} zone={zone} nowMs={nowMs} />
                  <Panel title="Progressive delivery" subtitle={`${stages.run.mode} run · ${cards.length} ${cards.length === 1 ? 'stage' : 'stages'} · Failure stops downstream stages`}
                    meta={<span className="or-meta">Read-only view. Steps change only through their actions.</span>}
                    footer={<span className="or-foot"><span>On failure: stop downstream stages · Rollback: called by a person, automatic on SLO breach when armed</span>
                      <span className="mono">Running now: {run.steps.filter(s => s.status === 'Running').length}</span></span>}>
                    {cards.length === 0 ? <EmptyState title="This run has no stages">The release's runbook has no steps.</EmptyState>
                      : <StageCards cards={cards} selected={section} onSelect={s => { setStageSel(s); announce(`${sectionTitle(s)} selected.`) }} />}
                  </Panel>
                  {section && (
                    <div className="or-detail">
                      <StepsTable section={section} steps={run.steps} allDone={doneCodesOf(run.steps)} deployments={deployments} nowMs={nowMs} selectedId={stepId} busy={pending}
                        ended={ended} paused={paused} live={live === 'live'} lastEvent={(() => { const e = lastEvent(run.steps); return e ? `${new Date(e.at).toISOString().slice(11, 19)} UTC · ${e.text}` : null })()}
                        onSelect={id => onRoute({ selection: { kind: 'step', id } })} onAct={(s, a) => void stepAct(s, a, null, `${s.stepId}:${a}`)}
                        onLogs={canary ? () => onOpenDeployment(canary.id) : canAudit ? onOpenLogs : undefined} />
                      <CanaryPanel section={section} deployments={deployments} refreshKey={refreshKey} onOpenDeployment={onOpenDeployment} />
                    </div>
                  )}
                  <Timeline run={run} fc={fc} nowMs={nowMs} selectedId={stepId} onSelect={id => onRoute({ selection: { kind: 'step', id } })} />
                  {canPlan && !ended && (
                    <Panel title="Run controls" subtitle="Ending a run cannot be undone. Each choice asks first.">
                      <div className="or-button-row">
                        <Button variant="primary" disabled={!!pending} onClick={() => setEnding('Completed')}>End run: completed</Button>
                        <Button variant="destructive" disabled={!!pending} onClick={() => setEnding('RolledBack')}>End run: rolled back</Button>
                        <Button variant="destructive" disabled={!!pending} onClick={() => setEnding('Aborted')}>Abort run</Button>
                      </div>
                    </Panel>)}
                </div>
                {step && <StepDrawer run={run} step={step} fc={fc} paused={paused} pending={pending} error={problem} onAct={(a, note) => stepAct(step, a, note, `drawer:${a}`)} onClose={() => onRoute({ selection: null })} />}
              </div>)}
            {tab === 'history' && <RunHistory runs={data.runs} currentId={run?.id ?? null} zone={zone} onPick={r => { setRunId(r.id); onRoute({ mode: r.mode === 'Live' ? 'live' : 'rehearsal', selection: null }); setTab('pipeline') }} />}
            {tab === 'config' && (stages ? <RunConfiguration stages={stages.run} fc={fc} zone={zone} /> : <EmptyState title="No run to configure">Start a run to see its window, rollback deadline and failure policy.</EmptyState>)}
            {tab === 'vars' && <VariablesTab />}
          </TabPanel>
        </>
      )}

      <ConfirmDialog open={pausing} title="Pause this run?" confirmLabel="Pause run" pendingLabel="Pausing…" destructive pending={pending === 'pause'} onCancel={() => setPausing(false)} onConfirm={pause}>
        <p>While the run is paused no step can start. Steps already running can finish or fail.</p>
        <label className="or-field"><span>Reason (required)</span>
          <input value={reason} maxLength={500} onChange={e => { setReason(e.target.value); setReasonError(null) }} aria-required="true" aria-invalid={reasonError ? true : undefined} placeholder="Why is the run paused?" /></label>
        {reasonError && <p className="or-error" role="alert">{reasonError}</p>}
      </ConfirmDialog>
      <ConfirmDialog open={ending !== null} title={OUTCOME[ending ?? 'Completed'].title} confirmLabel={OUTCOME[ending ?? 'Completed'].confirm} pendingLabel={OUTCOME[ending ?? 'Completed'].pending}
        destructive={OUTCOME[ending ?? 'Completed'].destructive} pending={pending === 'end-run'} onCancel={() => setEnding(null)} onConfirm={() => ending && void finish(ending)}>{OUTCOME[ending ?? 'Completed'].body}</ConfirmDialog>
    </div>
  )
}
