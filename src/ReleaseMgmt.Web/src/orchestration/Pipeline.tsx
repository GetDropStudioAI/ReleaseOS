import { useEffect, useState, type MouseEvent } from 'react'
import { Ban, Check, Circle, CircleX, LoaderCircle, Lock, SlidersHorizontal } from 'lucide-react'
import type { RunStepRow } from '../api.ts'
import { getMonitoring, type Deployment, type Monitoring } from '../deploy/deployApi.ts'
import { metricName, metricUnit, rollbackBanner, trim, utcHM } from '../deploy/deployLogic.ts'
import { onDeploymentChanged } from '../live.ts'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, Notice, Panel, ProgressBar, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { duration, hms, sectionTitle, stepSeconds, stepState, strategyFor, type StageCard, type StageWord } from './orchLogic.ts'

const ICON: Record<StageWord, typeof Check> = { Successful: Check, Running: LoaderCircle, Failed: CircleX, Blocked: Lock, Planned: Circle, Skipped: Ban }
const GLYPH = { done: '✓', running: '●', failed: '✗', skipped: '–', waiting: '○' } as const

/** The progressive-delivery graph: one card per stage joined by a connector line; it wraps (and the page scrolls) when there are more or fewer than five. */
export function StageCards({ cards, selected, onSelect }: { cards: StageCard[]; selected: string | null; onSelect: (section: string) => void }) {
  return (
    <ol className="or-stages" aria-label="Stages">
      {cards.map(c => {
        const Icon = ICON[c.word], on = c.section === selected
        return (
          <li key={c.section} className="or-stage-item">
            <button type="button" className={`or-stage ${c.tone}${on ? ' selected' : ''}`} aria-pressed={on} aria-label={c.announce} onClick={() => onSelect(c.section)}>
              <span className="or-stage-head">
                <span className={`or-stage-icon ${c.tone}`}><Icon aria-hidden="true" size={16} strokeWidth={1.6} className={c.word === 'Running' ? 'os-spin' : undefined} /></span>
                <span className="or-stage-title">{c.title}</span>
              </span>
              <span className="or-stage-chip"><StatusChip tone={c.tone}>{c.word}</StatusChip></span>
              <span className="or-bullets">
                {c.bullets.map((b, i) => <span key={i} className={`or-bullet ${b.mark}`}><span aria-hidden="true" className="or-bullet-mark">{GLYPH[b.mark]}</span>{b.text}<span className="sr-only"> ({b.word})</span></span>)}
              </span>
              <span className={`or-stage-summary ${c.tone}`}>{c.summary}</span>
              <span className="or-stage-span">{c.span}</span>
            </button>
          </li>
        )
      })}
    </ol>
  )
}

/** The execution steps of the selected stage: step and service, strategy, start (UTC), duration and state. Start and Mark done are on the row; the drawer holds notes, Fail and Skip. */
export function StepsTable({ section, steps, allDone, deployments, nowMs, selectedId, busy, ended, paused, live, lastEvent, onSelect, onAct, onLogs }: {
  section: string; steps: RunStepRow[]; allDone: Set<string>; deployments: Deployment[]; nowMs: number; selectedId: string | null; busy: string | null
  ended: boolean; paused: boolean; live: boolean; lastEvent: string | null; onSelect: (stepId: string) => void; onAct: (s: RunStepRow, action: 'start' | 'done') => void; onLogs?: () => void
}) {
  const rows = steps.filter(s => s.section === section).sort((a, b) => a.plannedStartAt.localeCompare(b.plannedStartAt))
  const stop = (e: { stopPropagation: () => void }) => e.stopPropagation()
  const columns: Column<RunStepRow>[] = [
    { key: 'step', header: 'Step / service', cell: s => {
      const st = stepState(s, allDone)
      return <TwoLine primary={s.title} secondary={`${s.stepCode} · ${s.ownerName ?? 'No owner'}${st.waitingOn.length > 0 ? ` · after ${st.waitingOn.join(', ')}` : ''}`} />
    } },
    { key: 'strategy', header: 'Strategy', cell: s => strategyFor(s, deployments) ?? <span className="os-muted">Not linked<span className="sr-only"> to a deployment</span></span> },
    { key: 'started', header: 'Started · UTC', mono: true, cell: s => (s.actualStartAt ? hms(Date.parse(s.actualStartAt)) : <span className="os-muted">Not started</span>) },
    { key: 'duration', header: 'Duration', mono: true, cell: s => { const d = stepSeconds(s, nowMs); return d === null ? <span className="os-muted">Not started</span> : duration(d) } },
    { key: 'state', header: 'State', cell: s => {
      const st = stepState(s, allDone)
      const ready = s.status === 'Scheduled' && st.waitingOn.length === 0
      const inline = s.canAct && !ended && s.section !== 'Rollback' && (ready || s.status === 'Running') ? (s.status === 'Running' ? 'done' : 'start') : null
      const disabled = busy !== null || (inline === 'start' && paused)
      return (
        <span className="or-state">
          <StatusChip tone={st.tone}>{st.word}</StatusChip>
          {inline && (
            <span onKeyDown={stop} onClick={stop as (e: MouseEvent) => void}>
              <Button disabled={disabled} aria-label={`${inline === 'start' ? 'Start' : 'Mark done'} ${s.stepCode}`} onClick={() => onAct(s, inline)}>
                {busy === `${s.stepId}:${inline}` ? (inline === 'start' ? 'Starting…' : 'Marking done…') : inline === 'start' ? 'Start' : 'Mark done'}
              </Button>
            </span>
          )}
        </span>
      )
    } },
  ]
  return (
    <Panel title={`${sectionTitle(section)} · execution steps`} meta={live && !ended ? <StatusChip tone="info">Live</StatusChip> : undefined}
      footer={<span className="or-foot"><span className="mono">{lastEvent ? `Last event ${lastEvent}` : 'No events yet'}</span>{onLogs && <button type="button" className="or-link" onClick={onLogs}>Open run logs →</button>}</span>}>
      <DataTable columns={columns} rows={rows} rowKey={s => s.stepId} caption={`${sectionTitle(section)} steps`} selectedKey={selectedId} onSelect={s => onSelect(s.stepId)}
        empty="This stage has no steps." />
    </Panel>
  )
}

/** Canary analysis for the selected stage. The Deploy stage is where a canary rolls out, so only it shows the analysis; the others say so. */
export function CanaryPanel({ section, deployments, refreshKey, onOpenDeployment }: { section: string; deployments: Deployment[]; refreshKey: number; onOpenDeployment: (id: string) => void }) {
  const canaries = deployments.filter(d => d.strategy === 'Canary')
  const [pickId, setPickId] = useState<string | null>(null)
  const pick = canaries.find(d => d.id === pickId) ?? canaries.find(d => d.state === 'Running') ?? canaries[0] ?? null
  const [mon, setMon] = useState<Monitoring | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const id = section === 'Deploy' ? pick?.id ?? null : null
  useEffect(() => { setMon(null); setError(null) }, [id])
  useEffect(() => {
    if (!id) return
    let current = true
    getMonitoring(id).then(m => { if (current) { setMon(m); setError(null) } }).catch((e: Error) => { if (current) setError(e.message) })
    return () => { current = false }
  }, [id, refreshKey, tick])
  useEffect(() => {
    if (!id) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const off = onDeploymentChanged(d => { if (d === id) { clearTimeout(timer); timer = setTimeout(() => setTick(n => n + 1), 350) } })
    return () => { off(); clearTimeout(timer) }
  }, [id])

  const sub = pick && section === 'Deploy' ? `Selected step · ${pick.serviceKey}` : `${sectionTitle(section)} stage`
  const settings = id ? <Button aria-label={`Open ${pick?.serviceName ?? 'the'} deployment monitoring, canary settings`} icon={SlidersHorizontal} onClick={() => onOpenDeployment(id)}>Monitoring</Button> : undefined
  if (section !== 'Deploy') return <Panel title="Canary analysis" subtitle={sub}><p className="or-note">A canary rolls out in the Deploy stage. Select Deploy to see its traffic, thresholds and next promotion.</p></Panel>
  if (!pick) return <Panel title="Canary analysis" subtitle={sub}><EmptyState title="No canary deployment is linked to this run">A deployment tool reports a canary to the ingest API with this release's key; it then appears here.</EmptyState></Panel>
  return (
    <Panel title="Canary analysis" subtitle={sub} meta={settings}>
      {error && <ErrorState what="The canary analysis" message={error} onRetry={() => setTick(n => n + 1)} />}
      {!mon && !error && <LoadingState what="the canary analysis" />}
      {mon && <CanaryBody m={mon} />}
      {canaries.length > 1 && (
        <label className="or-pick">Service
          <select value={pick.id} onChange={e => setPickId(e.target.value)}>{canaries.map(d => <option key={d.id} value={d.id}>{d.serviceName} · {d.releaseVersion}</option>)}</select>
        </label>
      )}
    </Panel>
  )
}

function CanaryBody({ m }: { m: Monitoring }) {
  const c = m.canary
  if (!c.isCanary) return <p className="or-note">This deployment is not a canary ({c.strategy}).</p>
  const check = (metric: string) => {
    const x = c.checks.find(k => k.metric === metric)
    if (!x || x.value === null) return 'No samples yet'
    const u = metricUnit(metric)
    return x.threshold === null ? `${trim(x.value)}${u} · no threshold set` : `${trim(x.value)}${u} ${x.passed === false ? '>' : '<'} ${trim(x.threshold)}${u} threshold`
  }
  const window = `${Math.floor(Math.min(c.observedSeconds, c.observationWindowSeconds) / 60)}m / ${Math.round(c.observationWindowSeconds / 60)}m`
  const next = c.nextTrafficPercent !== null ? `${c.nextTrafficPercent}%${c.nextPromotionAt ? ` at ${utcHM(Date.parse(c.nextPromotionAt))}` : ''}` : c.currentStep >= c.stepCount ? 'Final step' : 'Not scheduled'
  const banner = rollbackBanner(m)
  return (
    <div className="or-canary">
      <div className="or-canary-top"><p className="or-canary-pct">{c.trafficPercent}%<span className="sr-only"> of traffic</span></p><p className="or-canary-step">Step {c.currentStep} of {c.stepCount}</p></div>
      <ProgressBar value={c.trafficPercent / 100} label="Canary traffic" tone="info" />
      <PropertyList items={[
        { label: 'Observation window', value: window, mono: true },
        { label: metricName('ErrorRate'), value: check('ErrorRate'), mono: true },
        { label: metricName('LatencyP95'), value: check('LatencyP95'), mono: true },
        { label: 'Next promotion', value: next, mono: true },
      ]} />
      <Notice tone={banner.tone} title={banner.title}>{banner.body}</Notice>
    </div>
  )
}
