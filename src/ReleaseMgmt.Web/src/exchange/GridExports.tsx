import { useCallback, useEffect, useState } from 'react'
import { getStream, type Me, type StreamRow } from '../api'
import { getGrids, type GridInfo } from '../exchangeApi'
import { errMsg } from '../format'
import { DataTable, ErrorState, FilterSelect, LoadingState, Panel, TwoLine, type Column } from '../ui'

/**
 * The grid catalogue (PROJECT_SCOPE 9, REOS-49, REOS-133): every grid as CSV and XLSX. Plain links, so the browser downloads them with the session cookie.
 * Grids of the ten import kinds say so: their files re-import unchanged. A train filter narrows the grids that have one.
 */
export function GridExports({ me }: { me: Me }) {
  const [grids, setGrids] = useState<GridInfo[] | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [train, setTrain] = useState('')
  const [error, setError] = useState<string | null>(null)
  const canAudit = me.roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')

  const load = useCallback(() => { getGrids().then(g => { setGrids(g); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => {
    load()
    getStream().then(setTrains).catch(() => setTrains([]))   // the filter is optional: without trains the grids still download unfiltered
  }, [load])

  const visible = (grids ?? []).filter(g => g.policy !== 'AuditRead' || canAudit)
  const href = (g: GridInfo, ext: string) => `/api/v1/exports/${g.key}.${ext}${train && g.filters.includes('train') ? `?train=${encodeURIComponent(train)}` : ''}`

  const columns: Column<GridInfo>[] = [
    { key: 'grid', header: 'Grid', cell: g => <TwoLine primary={g.label} secondary={`${g.description}${g.importKind ? ' · re-imports unchanged' : ''}`} /> },
    { key: 'dl', header: <span className="sr-only">Download</span>, cell: g => (
      <span className="ex-dl">
        <a className="os-button secondary" href={href(g, 'csv')} download aria-label={`${g.label} as CSV`}>CSV</a>
        <a className="os-button secondary" href={href(g, 'xlsx')} download aria-label={`${g.label} as XLSX`}>XLSX</a>
      </span>) },
  ]

  return (
    <Panel label="Grid catalogue" title="Grid catalogue" subtitle="Every grid as a file. CSV and XLSX." meta={
      <FilterSelect label="Train" value={train} onChange={setTrain} options={[{ value: '', label: 'All trains' }, ...trains.map(t => ({ value: t.id, label: t.title }))]} />}
      footer="Columns starting with # are read-only (ids, versions, statuses): imports skip them. Cells starting with = + − @ are escaped with a leading apostrophe so spreadsheets cannot run them.">
      {error && <ErrorState what="The grid catalogue" message={error} onRetry={load} />}
      {grids === null ? (error ? null : <LoadingState what="the grid catalogue" />)
        : <DataTable caption="Grid catalogue" columns={columns} rows={visible} rowKey={g => g.key} empty="No grids are available to you." />}
    </Panel>
  )
}
