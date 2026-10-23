import { useCallback, useEffect, useMemo, useState } from 'react'
import { onDeploymentChanged, type LiveState } from '../live.ts'
import { browserZone } from '../shell/clock.ts'
import { DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, PageHeading, SearchField, StaleBanner, StatusChip, TwoLine, type Column } from '../ui'
import { getDeployments, type Deployment } from './deployApi.ts'
import { depKey, stampOf, stateTone, stateWord } from './deployLogic.ts'
import { DeploymentMonitor } from './DeploymentMonitor.tsx'
import './deploy.css'

/**
 * The Deployments destination (REOS-140, REOS-100). `/deployments` is the list; `/deployments/{id}` is the monitoring page for one deployment. The page reads its own
 * sub-path from the address bar (the app's router knows only the first segment), so a deep link, a refresh and the browser's Back button all work, and the sidebar's
 * Deployments item returns to the list.
 */
const STATES = ['Queued', 'Running', 'Paused', 'Successful', 'Failed', 'RolledBack']
const ALL = ''

const deploymentPath = (id: string | null) => (id ? `/deployments/${encodeURIComponent(id)}` : '/deployments')
const selectedId = () => { const seg = window.location.pathname.split('/').filter(Boolean); return seg[0] === 'deployments' && seg[1] ? decodeURIComponent(seg[1]) : null }

export function Deployments({ live, refreshKey, canControl, onOpenTrain }: { live: LiveState; refreshKey: number; canControl: boolean; onOpenTrain: (trainId: string) => void }) {
  const [, redraw] = useState(0)
  const id = selectedId()   // read on every render: the app re-renders on Back and on the sidebar, and `go` below redraws for our own moves
  const go = useCallback((next: string | null) => {
    const path = deploymentPath(next)
    if (path !== window.location.pathname) window.history.pushState(null, '', path)
    redraw(n => n + 1)
    window.scrollTo?.({ top: 0 })
  }, [])
  useEffect(() => { const pop = () => redraw(n => n + 1); window.addEventListener('popstate', pop); return () => window.removeEventListener('popstate', pop) }, [])

  return id
    ? <DeploymentMonitor key={id} id={id} live={live} refreshKey={refreshKey} canControl={canControl} onBack={() => go(null)} onOpen={go} onOpenTrain={onOpenTrain} />
    : <DeploymentList live={live} refreshKey={refreshKey} onOpen={go} onOpenTrain={onOpenTrain} />
}

function DeploymentList({ live, refreshKey, onOpen, onOpenTrain }: { live: LiveState; refreshKey: number; onOpen: (id: string) => void; onOpenTrain: (trainId: string) => void }) {
  const [rows, setRows] = useState<Deployment[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [environment, setEnvironment] = useState(ALL)
  const [service, setService] = useState(ALL)
  const [state, setState] = useState(ALL)
  const [loadedAt, setLoadedAt] = useState<string | undefined>()
  const zone = browserZone()

  const load = useCallback(() => {
    getDeployments({ take: 500 }).then(r => { setRows(r); setError(null); setLoadedAt(stampOf(new Date().toISOString(), zone).utc) }).catch((e: Error) => setError(e.message))
  }, [zone])
  useEffect(load, [load, refreshKey])
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const off = onDeploymentChanged(() => { clearTimeout(timer); timer = setTimeout(load, 350) })
    return () => { off(); clearTimeout(timer) }
  }, [load])

  const environments = useMemo(() => distinct(rows ?? [], r => [r.environmentId, r.environmentName]), [rows])
  const services = useMemo(() => distinct(rows ?? [], r => [r.serviceId, r.serviceName]), [rows])
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (rows ?? []).filter(r =>
      (environment === ALL || r.environmentId === environment) && (service === ALL || r.serviceId === service) && (state === ALL || r.state === state)
      && (q === '' || [depKey(r.id), r.externalId, r.serviceName, r.serviceKey, r.releaseVersion, r.releaseKey ?? '', r.trainTitle ?? '', r.environmentName].some(t => t.toLowerCase().includes(q))))
  }, [rows, query, environment, service, state])
  const filtered = query !== '' || environment !== ALL || service !== ALL || state !== ALL

  const columns: Column<Deployment>[] = [
    { key: 'key', header: 'Deployment', cell: r => (
      <a href={deploymentPath(r.id)} className="dp-keylink mono" aria-label={`Open ${depKey(r.id)}, ${r.serviceName} ${r.releaseVersion} in ${r.environmentName}`}
        onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(r.id) }}>{depKey(r.id)}</a>) },
    { key: 'service', header: 'Service', cell: r => <TwoLine primary={r.serviceName} secondary={r.serviceKey} /> },
    { key: 'environment', header: 'Environment', cell: r => <TwoLine primary={r.environmentName} secondary={r.environmentKind} /> },
    { key: 'version', header: 'Version', cell: r => r.releaseVersion, mono: true },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={stateTone(r.state)}>{stateWord(r.state)}</StatusChip> },
    { key: 'release', header: 'Release', cell: r => r.trainId
      ? <a href={`/trains/${r.trainId}`} onClick={e => { e.preventDefault(); e.stopPropagation(); onOpenTrain(r.trainId!) }}><TwoLine primary={r.releaseKey ?? r.trainTitle ?? 'Release'} secondary={r.releaseKey ? r.trainTitle : undefined} /></a>
      : <span className="os-muted">No release</span> },
    { key: 'updated', header: 'Updated', cell: r => { const s = stampOf(r.lastEventAt ?? r.updatedAt, zone); return <TwoLine primary={s.utc} secondary={s.local ? `${s.local} for you` : undefined} /> } },
  ]

  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Deployments']} title="Deployments" description="Every rollout of a service to an environment, with its state and the release it belongs to." />
      {live !== 'live' && <StaleBanner offline since={loadedAt} onRefresh={load} />}
      {error && <ErrorState what="The deployments" message={error} onRetry={load} />}
      {!rows && !error && <LoadingState what="the deployments" />}
      {rows && rows.length === 0 && (
        <EmptyState title="No deployments yet">Deployments appear here when a deployment tool reports them to the ingest API. An administrator creates the ingest key; the service catalog and the environments must exist first.</EmptyState>
      )}
      {rows && rows.length > 0 && (
        <>
          <div className="dp-filters" role="search" aria-label="Filter deployments">
            <SearchField label="Search deployments" placeholder="Search key, service, version, release…" value={query} onChange={setQuery} />
            <FilterSelect label="Environment" value={environment} onChange={setEnvironment} options={[{ value: ALL, label: 'All environments' }, ...environments]} />
            <FilterSelect label="Service" value={service} onChange={setService} options={[{ value: ALL, label: 'All services' }, ...services]} />
            <FilterSelect label="State" value={state} onChange={setState} options={[{ value: ALL, label: 'All states' }, ...STATES.map(s => ({ value: s, label: stateWord(s) }))]} />
          </div>
          <DataTable columns={columns} rows={shown} rowKey={r => r.id} caption="Deployments" onSelect={r => onOpen(r.id)}
            empty={filtered ? 'No deployment matches these filters.' : 'No deployments.'}
            footer={`${shown.length} of ${rows.length} ${rows.length === 1 ? 'deployment' : 'deployments'}${rows.length >= 500 ? ' (the newest 500)' : ''}${loadedAt ? ` · loaded ${loadedAt}` : ''}`} />
        </>
      )}
    </div>
  )
}

/** The distinct (id, name) pairs of a list, by name, as select options. */
function distinct<T>(rows: T[], pick: (r: T) => [string, string]): { value: string; label: string }[] {
  const seen = new Map<string, string>()
  for (const r of rows) { const [v, l] = pick(r); seen.set(v, l) }
  return [...seen].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))
}
