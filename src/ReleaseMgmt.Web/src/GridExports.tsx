import { useEffect, useState } from 'react'
import { getStream, type Me, type StreamRow } from './api'
import { getGrids, type GridInfo } from './exchangeApi'

/**
 * The export catalogue (PROJECT_SCOPE 9, REOS-49): every grid as CSV and XLSX. Plain links, so the browser downloads them with the session cookie.
 * Grids of the nine import kinds say so: their files re-import unchanged. A train filter narrows the grids that have one.
 */
export function GridExports({ me }: { me: Me }) {
  const [grids, setGrids] = useState<GridInfo[] | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [train, setTrain] = useState('')
  const [error, setError] = useState<string | null>(null)
  const canAudit = me.roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')

  useEffect(() => {
    getGrids().then(setGrids).catch(e => setError((e as Error).message))
    getStream().then(setTrains).catch(() => setTrains([]))
  }, [])

  const visible = (grids ?? []).filter(g => g.policy !== 'AuditRead' || canAudit)
  const href = (g: GridInfo, ext: string) => `/api/v1/exports/${g.key}.${ext}${train && g.filters.includes('train') ? `?train=${encodeURIComponent(train)}` : ''}`

  return (
    <section aria-label="Grid exports">
      <div className="cap">Export · grids</div>
      <h2 className="aside-title">Grids as files</h2>
      <p className="inline-form"><label>Train <select className="line" value={train} onChange={e => setTrain(e.target.value)}>
        <option value="">All trains</option>{trains.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label></p>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      <table className="grid export-grids">
        <thead><tr><th>Grid</th><th>Formats</th><th><span className="sr-only">Download</span></th></tr></thead>
        <tbody>
          {visible.map(g => (
            <tr key={g.key}>
              <td><div className="strong">{g.label}</div><div className="muted small">{g.description}{g.importKind ? ' · re-imports unchanged' : ''}</div></td>
              <td className="muted">CSV, XLSX</td>
              <td className="nowrap">
                <a className="text" href={href(g, 'csv')} download aria-label={`${g.label} as CSV`}>CSV</a>{' '}
                <a className="text" href={href(g, 'xlsx')} download aria-label={`${g.label} as XLSX`}>XLSX</a>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted small export-note">Columns starting with # are read-only (ids, versions, statuses): imports skip them. Cells starting with = + − @ are escaped with a leading apostrophe so spreadsheets cannot run them.</p>
    </section>
  )
}
