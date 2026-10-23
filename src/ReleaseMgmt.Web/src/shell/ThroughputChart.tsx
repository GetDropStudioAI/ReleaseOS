import { useState } from 'react'
import { chartMax, dayText, shortDay, type Day } from './overviewApi.ts'

/**
 * Release throughput (REOS-95): one stacked bar per UTC day: successful at the bottom, completed with issues, failed on top. Drawn as SVG with token
 * colours, so a theme switch needs no redraw. Each day is a tab stop that shows its figures in the readout under the chart on hover and on focus; the
 * same numbers are in a table behind "View data", and the whole chart has a one-sentence summary as its name.
 */
export function ThroughputChart({ days }: { days: Day[] }) {
  const [active, setActive] = useState<number | null>(null)
  const max = chartMax(days)
  const W = 640, H = 180, L = 28, B = 22, T = 8
  const slot = (W - L) / Math.max(days.length, 1), bar = Math.max(4, slot * 0.6)
  const y = (v: number) => T + (H - T - B) * (1 - v / max)
  const total = days.reduce((n, d) => n + d.successful + d.withIssues + d.unsuccessful, 0)
  const today = days.length - 1
  const every = Math.ceil(days.length / 7)
  const ticks = [0, max / 2, max]
  return (
    <div className="os-chart">
      <svg viewBox={`0 0 ${W} ${H}`} role="group" aria-label={`Release throughput: ${total} ${total === 1 ? 'release' : 'releases'} completed in ${days.length} days`} className="os-chart-svg">
        {ticks.map(t => <g key={t}><line x1={L} x2={W} y1={y(t)} y2={y(t)} className="os-chart-grid" /><text x={L - 6} y={y(t) + 3} textAnchor="end" className="os-chart-axis">{t}</text></g>)}
        {days.map((d, i) => {
          const x = L + i * slot + (slot - bar) / 2
          const a = d.successful, b = d.withIssues, c = d.unsuccessful
          return (
            <g key={d.date} tabIndex={0} role="img" aria-label={dayText(d)} className={`os-chart-day${i === today ? ' today' : ''}${active === i ? ' active' : ''}`}
              onMouseEnter={() => setActive(i)} onMouseLeave={() => setActive(null)} onFocus={() => setActive(i)} onBlur={() => setActive(null)}>
              <rect x={L + i * slot} y={T} width={slot} height={H - T - B} className="os-chart-hit" />
              {a > 0 && <rect x={x} y={y(a)} width={bar} height={y(0) - y(a)} className="os-bar success" />}
              {b > 0 && <rect x={x} y={y(a + b)} width={bar} height={y(a) - y(a + b)} className="os-bar warning" />}
              {c > 0 && <rect x={x} y={y(a + b + c)} width={bar} height={y(a + b) - y(a + b + c)} className="os-bar danger" />}
              {i % every === 0 || i === today ? <text x={x + bar / 2} y={H - 6} textAnchor="middle" className="os-chart-axis">{shortDay(d.date)}</text> : null}
            </g>
          )
        })}
      </svg>
      <p className="os-chart-readout" aria-live="off">{active === null ? 'Hover or focus a day for its figures. Today is outlined.' : dayText(days[active])}</p>
      <ul className="os-legend" aria-label="Legend">
        <li><span className="os-swatch success" aria-hidden="true" />Successful</li>
        <li><span className="os-swatch warning" aria-hidden="true" />With issues</li>
        <li><span className="os-swatch danger" aria-hidden="true" />Failed</li>
      </ul>
      <details className="os-chart-data">
        <summary>View data</summary>
        <table className="os-table"><caption className="sr-only">Releases completed per day (UTC)</caption>
          <thead><tr><th scope="col">Day (UTC)</th><th scope="col" className="end">Successful</th><th scope="col" className="end">With issues</th><th scope="col" className="end">Failed</th></tr></thead>
          <tbody>{days.map(d => <tr key={d.date}><th scope="row">{shortDay(d.date)}</th><td className="end">{d.successful}</td><td className="end">{d.withIssues}</td><td className="end">{d.unsuccessful}</td></tr>)}</tbody>
        </table>
      </details>
    </div>
  )
}
