import { useMemo, useState } from 'react'
import { Plus } from 'lucide-react'
import type { StreamRow } from '../api.ts'
import { useSession } from '../session.tsx'
import type { LiveState } from '../live.ts'
import { day, plural, tMinus } from '../format.ts'
import { statusChip } from '../release/model.ts'
import { stampOf, stampText } from '../deploy/deployLogic.ts'
import { browserZone } from '../shell/clock.ts'
import { Button, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, PageHeading, SearchField, StaleBanner, StatusChip, TwoLine, type Column } from '../ui'
import { ALL, NO_FILTERS, STATUSES, filterReleases, filtersFromSearch, filtersToSearch, gateRollup, isFiltered, optionsOf, sortReleases, teamLabel, type Filters } from './releasesLogic.ts'
import './releases.css'

/**
 * The Releases destination (REOS-125, D50): every release in the workspace as one full-width table with filters for status, team, environment and type. A row opens
 * the release page (`/trains/{id}`, REOS-97). The 272 px Stream and its freeze footer are gone; freezes are on Portfolio & calendar and on each release.
 */
export function Releases({ rows, error, live, loadedAt, canCreate, onReload, onOpen, onNew, onImport }: {
  rows: StreamRow[] | null; error: string | null; live: LiveState; loadedAt: string | undefined; canCreate: boolean
  onReload: () => void; onOpen: (id: string) => void; onNew: () => void; onImport: () => void
}) {
  const { filter: saved, setFilter: save } = useSession()   // the filters are saved with the tab's UI state, as the Stream's filter was (REOS-26); an address with filters wins
  const [filters, setFilters] = useState<Filters>(() => filtersFromSearch(window.location.search || saved))
  const zone = browserZone()
  const set = (patch: Partial<Filters>) => {
    const next = { ...filters, ...patch }
    setFilters(next)
    save(filtersToSearch(next))
    const url = window.location.pathname + filtersToSearch(next)
    if (url !== window.location.pathname + window.location.search) window.history.replaceState(null, '', url)
  }

  const teams = useMemo(() => optionsOf(rows ?? [], r => (r.ownerTeam ? [{ id: r.ownerTeam.id, label: teamLabel(r)! }] : []), 'All teams', 'No team'), [rows])
  const environments = useMemo(() => optionsOf(rows ?? [], r => r.environments.map(e => ({ id: e.id, label: e.name })), 'All environments', 'No environment'), [rows])
  const types = useMemo(() => optionsOf(rows ?? [], r => (r.type ? [{ id: r.type.id, label: r.type.name }] : []), 'All types', 'No type'), [rows])
  const shown = useMemo(() => sortReleases(filterReleases(rows ?? [], filters)), [rows, filters])
  // A filter whose choice has gone from the list (the release moved on, was archived) would hide everything with no way to see why: say so and offer the reset.
  const filtered = isFiltered(filters)

  const columns: Column<StreamRow>[] = [
    { key: 'release', header: 'Release', cell: r => (
      <TwoLine primary={<a href={`/trains/${r.id}`} className="rl-keylink mono" aria-label={`Open ${r.key ?? r.title}, ${r.title}`} onClick={e => { e.preventDefault(); e.stopPropagation(); onOpen(r.id) }}>{r.key ?? 'No key'}</a>} secondary={r.title} />) },
    { key: 'status', header: 'Status', cell: r => {
      const s = statusChip(r.status, r.closeCode), open = r.status !== 'Complete' && r.status !== 'Aborted'
      return <TwoLine primary={<StatusChip tone={s.tone}>{s.word}</StatusChip>} secondary={open ? (r.blockers > 0 ? `▲ ${plural(r.blockers, 'blocker')}` : 'No blockers') : undefined} />
    } },
    { key: 'type', header: 'Type', cell: r => r.type?.name ?? <span className="os-muted">Not set</span> },
    { key: 'team', header: 'Team and owner', cell: r => (r.ownerTeam || r.owner)
      ? <TwoLine primary={teamLabel(r) ?? <span className="os-muted">No team</span>} secondary={r.owner?.name ?? 'No owner'} />
      : <span className="os-muted">Not set</span> },
    { key: 'environments', header: 'Environments', cell: r => r.environments.length > 0 ? r.environments.map(e => e.name).join(', ') : <span className="os-muted">Not set</span> },
    { key: 'target', header: 'Target date (UTC)', cell: r => {
      const done = r.status === 'Complete' || r.status === 'Aborted'
      return <TwoLine primary={day(r.targetReleaseDate)} secondary={done ? (r.endedOn ? `${r.status === 'Aborted' ? 'Aborted' : 'Closed'} ${day(r.endedOn)}` : undefined) : tMinus(r.daysToTarget)} />
    } },
    { key: 'gates', header: 'Gates', cell: r => { const g = gateRollup(r.gates); return <TwoLine primary={<span className="mono">{g.words}</span>} secondary={g.failed > 0 ? `${g.failed} failed` : undefined} /> } },
  ]

  const actions = canCreate ? <Button variant="primary" icon={Plus} onClick={onNew}>New train</Button> : undefined
  const heading = <PageHeading trail={['Workspace', 'Releases']} title="Releases" description="Every release in the workspace. Open one to plan its gates, record decisions and run it." actions={actions} />
  if (error && !rows) return <div className="os-page">{heading}<ErrorState what="The releases" message={error} onRetry={onReload} /></div>
  if (!rows) return <div className="os-page">{heading}<LoadingState what="the releases" /></div>
  const since = loadedAt ? stampText(stampOf(loadedAt, zone)) : undefined
  return (
    <div className="os-page">
      {heading}
      {live !== 'live' && <StaleBanner offline since={since} onRefresh={onReload} />}
      {error && <ErrorState what="The releases" message={error} onRetry={onReload} />}
      {rows.length === 0 ? (
        <EmptyState title="No releases yet" action={canCreate ? <Button variant="primary" icon={Plus} onClick={onNew}>New train</Button> : undefined}>
          Releases appear here once they are planned. {canCreate ? <>Start one with New train, or <a href="/importexport" onClick={e => { e.preventDefault(); onImport() }}>import trains from a CSV</a>.</> : 'Ask a release manager or release train engineer to plan the first one.'}
        </EmptyState>
      ) : (
        <>
          <div className="rl-filters" role="search" aria-label="Filter releases">
            <SearchField label="Search the release list" placeholder="Search key, title, owner, team…" value={filters.query} onChange={query => set({ query })} />
            <FilterSelect label="Status" value={filters.status} onChange={status => set({ status })} options={[{ value: ALL, label: 'All statuses' }, ...STATUSES.map(s => ({ value: s, label: s }))]} />
            <FilterSelect label="Team" value={filters.team} onChange={team => set({ team })} options={teams} />
            <FilterSelect label="Environment" value={filters.environment} onChange={environment => set({ environment })} options={environments} />
            <FilterSelect label="Type" value={filters.type} onChange={type => set({ type })} options={types} />
            {filtered && <Button onClick={() => set({ ...NO_FILTERS })}>Clear filters</Button>}
          </div>
          <DataTable columns={columns} rows={shown} rowKey={r => r.id} caption="Releases" onSelect={r => onOpen(r.id)}
            empty={filtered ? 'No release matches these filters.' : 'No releases.'}
            footer={`${shown.length} of ${rows.length} ${rows.length === 1 ? 'release' : 'releases'} · complete and aborted releases stay listed for 30 days${since ? ` · loaded ${since}` : ''}`} />
        </>
      )}
    </div>
  )
}
