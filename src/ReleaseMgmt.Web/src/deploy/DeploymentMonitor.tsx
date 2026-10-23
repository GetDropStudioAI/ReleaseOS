import { useCallback, useEffect, useRef, useState } from 'react'
import { Pause, Play, Undo2 } from 'lucide-react'
import { ApiError } from '../api.ts'
import { announce } from '../announce'
import { onDeploymentChanged, type LiveState } from '../live.ts'
import { browserZone } from '../shell/clock.ts'
import {
  Button, ConfirmDialog, ConflictNotice, DataTable, DisabledAction, ErrorState, FilterSelect, LoadingState, MetricCard, Notice, PageHeading, Panel, StaleBanner, StatusChip, TwoLine,
  type Column,
} from '../ui'
import {
  control, getDeployment, getDeployments, getMetrics, getMonitoring, getTrainDeployments,
  type Deployment, type DeploymentEvent, type Metrics, type Monitoring,
} from './deployApi.ts'
import {
  DEFAULT_RANGE, RANGES, anchorOf, availabilityCard, depKey, errorCard, healthOf, latencyCard, pendingControl, pointsOf, rangeStart, rollbackBanner, stampText, stampOf, stateTone, stateWord, trafficCard, trim, utcHMS,
} from './deployLogic.ts'
import { EventStream } from './EventStream.tsx'
import { LineChart } from './LineChart.tsx'
import './deploy.css'

/**
 * Deployment monitoring for one deployment (REOS-100, Figma 6:6058). One read (GET /deployments/{id}/monitoring) feeds the header, the metric cards, the
 * automatic-rollback banner and the table; the charts read the series for the chosen range; the events panel reads the deployment's events. A DeploymentChanged
 * push from the live channel (samples, events and control calls all send one) refetches all three. What the data model lacks is left out or says so: see
 * QUESTIONS Q-1000 to Q-1003.
 */
const EVENT_PAGE = 200, EVENT_MAX = 1000, HEARTBEAT_MS = 30000

interface Loaded { mon: Monitoring; metrics: Metrics; events: DeploymentEvent[]; siblings: Deployment[]; at: number }

export function DeploymentMonitor({ id, live, refreshKey, canControl, onBack, onOpen, onOpenTrain }: {
  id: string; live: LiveState; refreshKey: number; canControl: boolean
  onBack: () => void; onOpen: (id: string) => void; onOpenTrain: (trainId: string) => void
}) {
  const zone = browserZone()
  const [range, setRange] = useState(DEFAULT_RANGE)
  const [data, setData] = useState<Loaded | null>(null)
  const [error, setError] = useState<{ message: string; missing: boolean } | null>(null)
  const [eventLimit, setEventLimit] = useState(EVENT_PAGE)
  const [environments, setEnvironments] = useState<Deployment[]>([])   // the newest deployment of this service in each environment
  const [dialog, setDialog] = useState<null | 'rollback' | 'pause' | 'resume'>(null)
  const [reason, setReason] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const [conflict, setConflict] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const seq = useRef(0)   // a slow answer for an earlier range or deployment never overwrites a newer one

  const load = useCallback(async (opts: { keepEvents?: boolean } = {}) => {
    const mine = ++seq.current
    try {
      const mon = await getMonitoring(id)
      const anchor = anchorOf(mon, Date.now())
      const [metrics, detail, siblings] = await Promise.all([
        getMetrics(id, rangeStart(anchor, Number(range))),
        getDeployment(id, eventLimit),
        (mon.deployment.trainId ? getTrainDeployments(mon.deployment.trainId) : Promise.resolve([mon.deployment])).catch(() => [mon.deployment]),
      ])
      if (mine !== seq.current) return
      setData({ mon, metrics, events: detail.events, siblings, at: Date.now() })
      setError(null)
    } catch (e) {
      if (mine !== seq.current) return
      if (opts.keepEvents) return
      setError({ message: (e as Error).message, missing: e instanceof ApiError && e.status === 404 })
    }
  }, [id, range, eventLimit])

  // First load, and a reload for a new deployment, range or reconnect (refreshKey).
  useEffect(() => { void load() }, [load, refreshKey])

  // Live: a push for this deployment refetches it (bursts coalesce into one read); a slow heartbeat covers time-based changes (a canary window ending).
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const off = onDeploymentChanged(changed => { if (changed !== id) return; clearTimeout(timer); timer = setTimeout(() => { void load({ keepEvents: true }) }, 350) })
    const beat = setInterval(() => { if (live === 'live') void load({ keepEvents: true }) }, HEARTBEAT_MS)
    return () => { off(); clearTimeout(timer); clearInterval(beat) }
  }, [id, live, load])

  // The environment selector: this service's newest deployment in each environment.
  const serviceId = data?.mon.deployment.serviceId
  useEffect(() => {
    if (!serviceId) return
    getDeployments({ serviceId, take: 200 }).then(rows => {
      const newest = new Map<string, Deployment>()
      for (const r of rows) if (!newest.has(r.environmentId)) newest.set(r.environmentId, r)   // rows are newest first
      setEnvironments([...newest.values()])
    }).catch(() => setEnvironments([]))
  }, [serviceId, refreshKey, data?.mon.deployment.version])

  if (error) {
    return (
      <div className="os-page">
        <PageHeading trail={['Workspace', 'Deployments']} title="Deployment monitoring" />
        {error.missing
          ? <Notice tone="warning" title="This deployment is not here" action={<Button onClick={onBack}>All deployments</Button>}>It was removed, or it belongs to another workspace.</Notice>
          : <ErrorState what="The deployment" message={error.message} onRetry={() => { setError(null); void load() }} />}
      </div>
    )
  }
  if (!data) return <div className="os-page"><PageHeading trail={['Workspace', 'Deployments']} title="Deployment monitoring" /><LoadingState what="the deployment" /></div>

  const { mon, metrics, events, siblings } = data
  const d = mon.deployment
  const key = depKey(d.id)
  const awaiting = pendingControl(mon.actions, d.state)
  const lastFailed = mon.actions[0]?.status === 'Failed' ? mon.actions[0] : null
  const banner = rollbackBanner(mon)

  const planner = canControl
  const noRole = 'Only an RTE or a Release Manager can control a deployment.'
  const rollbackWhy = !planner ? noRole : d.state === 'Queued' || d.state === 'RolledBack' ? `A ${stateWord(d.state).toLowerCase()} deployment cannot be rolled back.` : null
  const pauseOrResume = d.state === 'Paused' ? 'resume' as const : 'pause' as const
  const pauseWhy = !planner ? noRole : d.state !== 'Running' && d.state !== 'Paused' ? `Only a running deployment can be paused; this one is ${stateWord(d.state).toLowerCase()}.` : awaiting?.status === 'Pending' ? 'A control call is still in flight.' : null

  const openDialog = (which: 'rollback' | 'pause' | 'resume') => { setReason(''); setProblem(null); setActionError(null); setDialog(which) }
  const confirm = async () => {
    if (!dialog) return
    if (dialog === 'rollback' && reason.trim() === '') { setProblem('Say why you are rolling back: the reason is recorded in the audit log.'); return }
    setPending(true); setProblem(null)
    try {
      await control(d.id, dialog, d.version, reason)
      setDialog(null); setConflict(false)
      announce(dialog === 'rollback' ? 'Rollback requested. It is recorded in the audit log.' : dialog === 'pause' ? 'Pause requested. It is recorded in the audit log.' : 'Resume requested. It is recorded in the audit log.')
      await load()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { setDialog(null); setConflict(true) }
      else if (e instanceof ApiError && (e.status === 502 || e.body?.guard === 'ControlFailed')) { setDialog(null); setActionError(e.message); await load() }
      else setProblem((e as Error).message)
    } finally { setPending(false) }
  }

  // ---- the charts ------------------------------------------------------------------------------------------------------
  const from = Date.parse(metrics.from), to = Date.parse(metrics.to)
  const sv = (name: 'Canary' | 'Stable') => metrics.series.find(s => s.series === name)?.points
  const canaryName = d.strategy === 'Canary' ? `Canary · ${d.releaseVersion}` : `This deployment · ${d.releaseVersion}`
  const errCanary = pointsOf(sv('Canary'), p => p.errorRatePct), errStable = pointsOf(sv('Stable'), p => p.errorRatePct)
  const latCanary = pointsOf(sv('Canary'), p => p.latencyP95Ms), latStable = pointsOf(sv('Stable'), p => p.latencyP95Ms)
  const canaryStart = metrics.canaryStartedAt ? Date.parse(metrics.canaryStartedAt) : null
  const cur = (s: { t: number; v: number }[], other: { t: number; v: number }[]) => (s.length ? s[s.length - 1].v : other.length ? other[other.length - 1].v : null)
  const errNow = cur(errCanary, errStable), latNow = cur(latCanary, latStable)

  // ---- the table -------------------------------------------------------------------------------------------------------
  const rows = siblings.filter(r => r.environmentId === d.environmentId)
  const counts = { done: rows.filter(r => r.state === 'Successful').length, running: rows.filter(r => r.state === 'Running' || r.state === 'Paused').length, queued: rows.filter(r => r.state === 'Queued').length }
  const columns: Column<Deployment>[] = [
    { key: 'service', header: 'Service', cell: r => <TwoLine primary={r.serviceName} secondary={r.id === d.id ? 'This deployment' : depKey(r.id)} /> },
    { key: 'version', header: 'Version', cell: r => r.releaseVersion, mono: true },
    { key: 'pods', header: 'Ready pods', cell: r => (r.readyPods !== null && r.desiredPods !== null ? `${r.readyPods} / ${r.desiredPods}` : 'Not reported'), mono: true },
    { key: 'traffic', header: 'Traffic', cell: r => `${trim(r.trafficPercent)}%`, mono: true },
    { key: 'health', header: 'Health', cell: r => (r.id === d.id ? <HealthChip m={mon} /> : <span className="os-muted">Not reported</span>) },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={stateTone(r.state)}>{stateWord(r.state)}</StatusChip> },
  ]

  const envOptions = environments.length > 0 ? environments : [d]
  const lastAt = stampText(stampOf(new Date(data.at).toISOString(), zone))
  const offline = live !== 'live'

  return (
    <div className="os-page dp-page">
      <button type="button" className="dp-back" onClick={onBack}>← All deployments</button>
      <PageHeading trail={['Workspace', 'Deployments', key]} title="Deployment monitoring"
        description={`${d.serviceName} · Live health and progressive rollout control for ${d.environmentName}.`}
        actions={<>
          {rollbackWhy
            ? <DisabledAction variant="destructive" icon={Undo2} reason={rollbackWhy}>Rollback</DisabledAction>
            : <Button variant="destructive" icon={Undo2} onClick={() => openDialog('rollback')}>Rollback</Button>}
          {pauseWhy
            ? <DisabledAction variant="primary" icon={pauseOrResume === 'resume' ? Play : Pause} reason={pauseWhy}>{pauseOrResume === 'resume' ? 'Resume rollout' : 'Pause rollout'}</DisabledAction>
            : <Button variant="primary" icon={pauseOrResume === 'resume' ? Play : Pause} onClick={() => openDialog(pauseOrResume)}>{pauseOrResume === 'resume' ? 'Resume rollout' : 'Pause rollout'}</Button>}
        </>} />

      {offline && <StaleBanner offline since={lastAt} onRefresh={() => void load()} />}
      {!planner && <p className="os-muted dp-note"><NoPermissionNote /></p>}
      {conflict && <ConflictNotice by="Someone" onUseTheirs={() => { setConflict(false); void load() }} />}
      {actionError && <Notice tone="danger" title="The call to the deployment tool did not succeed" live>{actionError} It is recorded on the deployment and as an alert.</Notice>}
      {lastFailed && !actionError && <Notice tone="danger" title={`The last ${lastFailed.action.toLowerCase()} call did not succeed`}>{lastFailed.error ?? 'The deployment tool did not accept it.'} Requested {stampText(stampOf(lastFailed.requestedAt, zone))}.</Notice>}
      {awaiting && (
        <Notice tone="warning" title={awaiting.status === 'Pending' ? `${awaiting.action} request in flight` : `${awaiting.action} requested`} live>
          {awaiting.status === 'Pending' ? 'Waiting for the deployment tool to answer.' : `The deployment tool accepted it. The state changes to ${awaiting.action === 'Pause' ? 'Paused' : 'Running'} when the tool reports it.`}
          {awaiting.requestedByName ? ` Requested by ${awaiting.requestedByName}.` : ''}
        </Notice>
      )}
      {d.state === 'Paused' && !awaiting && <Notice tone="warning" title="Rollout paused" live>The deployment tool reports this rollout as paused. Traffic stays at {trim(d.trafficPercent)}% until it is resumed.</Notice>}

      <div className="dp-context">
        <div className="dp-identity">
          <StatusChip tone={stateTone(d.state)}>{stateWord(d.state)}</StatusChip>
          <span className="dp-ref">
            {d.trainId && d.releaseKey ? <a href={`/trains/${d.trainId}`} onClick={e => { e.preventDefault(); onOpenTrain(d.trainId!) }}>{d.releaseKey}</a> : <span>No release</span>}
            {d.runId ? <> / <span title={d.runId}>RUN {d.runId.replace(/[^0-9a-z]/gi, '').slice(-6).toUpperCase()}</span></> : null}
          </span>
          <FilterSelect label="Environment" value={d.environmentId} onChange={envId => { const next = envOptions.find(e => e.environmentId === envId); if (next && next.id !== d.id) onOpen(next.id) }}
            options={envOptions.map(e => ({ value: e.environmentId, label: e.environmentName }))} />
          <span className="dp-version mono">{d.serviceKey} · {d.releaseVersion}</span>
        </div>
        <div className="dp-live">
          <span className={`dp-live-dot ${live === 'live' ? 'on' : 'off'}`} aria-hidden="true" />
          <span className="dp-live-text" role="status">{live === 'live' ? `LIVE · UPDATED ${utcHMS(data.at)} UTC` : live === 'offline' ? 'OFFLINE · NOT UPDATING' : 'RECONNECTING'}</span>
          <FilterSelect label="Time range" value={range} onChange={setRange} options={RANGES} />
        </div>
      </div>

      <div className="dp-metrics">
        <Card label="Canary traffic" c={trafficCard(mon, zone)} />
        <Card label="Error rate" c={errorCard(mon)} />
        <Card label="p95 latency" c={latencyCard(mon)} />
        <Card label="Availability" c={availabilityCard(mon)} />
      </div>

      <div className="dp-charts">
        <Panel title="Request error rate" meta={<Current value={errNow} unit="%" digits={2} />}>
          <LineChart title="Request error rate" unit="%" canary={errCanary} stable={errStable} threshold={mon.thresholds.errorRateMaxPct} thresholdLabel={mon.thresholds.errorRateMaxPct !== null ? `Rollback threshold ${trim(mon.thresholds.errorRateMaxPct)}%` : ''}
            canaryStart={canaryStart} from={from} to={to} canaryName={canaryName} stableName="Stable" digits={2} />
        </Panel>
        <Panel title="Request latency · p95" meta={<Current value={latNow} unit="ms" digits={0} />}>
          <LineChart title="Request latency p95" unit=" ms" canary={latCanary} stable={latStable} threshold={mon.thresholds.latencyP95MaxMs} thresholdLabel={mon.thresholds.latencyP95MaxMs !== null ? `Rollback threshold ${trim(mon.thresholds.latencyP95MaxMs)} ms` : ''}
            canaryStart={canaryStart} from={from} to={to} canaryName={canaryName} stableName="Stable" digits={0} />
        </Panel>
      </div>

      <div className="dp-policy">
        <ul className="os-legend" aria-label="Legend">
          <li><span className="dp-key canary" aria-hidden="true" />{canaryName}</li>
          <li><span className="dp-key stable" aria-hidden="true" />Stable</li>
          <li><span className="dp-key threshold" aria-hidden="true" />Rollback threshold</li>
          <li><span className="dp-key window" aria-hidden="true" />Canary window</li>
        </ul>
        <Notice tone={banner.tone} title={banner.title} live>{banner.body}</Notice>
      </div>

      <div className="dp-detail">
        <Panel title="Service rollout" subtitle={`${d.environmentName} · ${counts.done} complete, ${counts.running} running, ${counts.queued} queued`}>
          <DataTable columns={columns} rows={rows.length ? rows : [d]} rowKey={r => r.id} caption="Service rollout" selectedKey={d.id} onSelect={r => { if (r.id !== d.id) onOpen(r.id) }}
            footer={`${rows.length || 1} ${(rows.length || 1) === 1 ? 'deployment' : 'deployments'} of this release in ${d.environmentName}. Pod counts and health are what each deployment tool reports; a deployment that reports none shows "Not reported".`} />
        </Panel>
        <EventStream events={events} live={live} capped={events.length >= eventLimit && eventLimit < EVENT_MAX} onLoadMore={() => setEventLimit(EVENT_MAX)} />
      </div>

      <ConfirmDialog open={dialog !== null} destructive={dialog === 'rollback'} pending={pending}
        title={dialog === 'rollback' ? `Roll back ${key}?` : dialog === 'resume' ? `Resume ${key}?` : `Pause ${key}?`}
        confirmLabel={dialog === 'rollback' ? 'Roll back' : dialog === 'resume' ? 'Resume rollout' : 'Pause rollout'} pendingLabel="Sending…"
        onConfirm={() => void confirm()} onCancel={() => { if (!pending) setDialog(null) }}>
        <p>
          {dialog === 'rollback'
            ? `This asks the ${d.environmentName} deployment tool to roll back ${d.serviceName} ${d.releaseVersion}. The request and your reason are recorded in the audit log, and the state changes when the tool reports it.`
            : dialog === 'resume'
              ? `This asks the ${d.environmentName} deployment tool to continue the rollout of ${d.serviceName} ${d.releaseVersion}. It is recorded in the audit log.`
              : `This asks the ${d.environmentName} deployment tool to hold the rollout of ${d.serviceName} ${d.releaseVersion} at ${trim(d.trafficPercent)}% traffic. It is recorded in the audit log.`}
        </p>
        <label className="dp-reason">
          <span>{dialog === 'rollback' ? 'Reason (required)' : 'Reason (optional)'}</span>
          <textarea value={reason} rows={3} maxLength={500} onChange={e => setReason(e.target.value)} aria-invalid={problem !== null && dialog === 'rollback' && reason.trim() === ''} />
        </label>
        {problem && <p className="dp-problem" role="alert">{problem}</p>}
      </ConfirmDialog>
    </div>
  )
}

function NoPermissionNote() { return <>You can watch this deployment. Rollback and pause are for RTEs and Release Managers.</> }

function Card({ label, c }: { label: string; c: ReturnType<typeof trafficCard> }) {
  return <MetricCard label={label} value={c.value} unit={c.unit} detail={c.detail} tone={c.tone} />
}

function Current({ value, unit, digits }: { value: number | null; unit: string; digits: number }) {
  return <span className="dp-current mono" aria-label={value === null ? 'No current value' : `Current value ${trim(value, digits)} ${unit === '%' ? 'percent' : 'milliseconds'}`}>{value === null ? '–' : trim(value, digits)} <span className="dp-unit">{unit}</span></span>
}

function HealthChip({ m }: { m: Monitoring }) {
  const h = healthOf(m)
  return <StatusChip tone={h === 'Healthy' ? 'success' : h === 'At risk' ? 'warning' : h === 'Failing' ? 'danger' : 'neutral'}>{h}</StatusChip>
}
