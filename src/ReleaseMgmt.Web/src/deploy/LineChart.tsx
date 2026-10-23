import { useId, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { chartSummary, timeTicks, trim, utcHM, utcHMS, yMax, type Pt } from './deployLogic.ts'

/**
 * A request-metric line chart (REOS-100, Figma 6:6058): a canary and a stable series, a dashed threshold line, the shaded canary window from the time the
 * canary started, and a time axis in UTC. Drawn as SVG with token colours, as the Overview's throughput chart is. The plot is one tab stop: hover moves a
 * cursor along the time axis and Left, Right, Home and End do the same from the keyboard; the tooltip under it (also the live readout) gives the time in
 * UTC and both values. The same numbers are in a table behind "View data".
 */
export interface LineChartProps {
  title: string; unit: string; canary: Pt[]; stable: Pt[]; threshold: number | null; thresholdLabel: string
  canaryStart: number | null; from: number; to: number; canaryName: string; stableName: string; digits?: number
}

const W = 640, H = 170, L = 44, R = 10, T = 8, B = 22

export function LineChart({ title, unit, canary, stable, threshold, thresholdLabel, canaryStart, from, to, canaryName, stableName, digits = 2 }: LineChartProps) {
  const [cursor, setCursor] = useState<number | null>(null)
  const plot = useRef<SVGRectElement>(null)
  const readoutId = useId()
  const span = Math.max(1, to - from)
  const max = yMax([canary, stable], threshold)
  const x = (t: number) => L + ((t - from) / span) * (W - L - R)
  const y = (v: number) => T + (H - T - B) * (1 - v / max)
  const times = useMemo(() => [...new Set([...canary, ...stable].map(p => p.t))].sort((a, b) => a - b), [canary, stable])
  const at = (s: Pt[], t: number) => s.find(p => p.t === t)?.v ?? null
  const path = (s: Pt[]) => s.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.t).toFixed(1)} ${y(p.v).toFixed(1)}`).join(' ')
  const fmt = (v: number | null) => (v === null ? 'no sample' : `${trim(v, digits)}${unit}`)
  const ticks = [0, max / 2, max]
  const summary = chartSummary(title, unit, canary, stable, threshold)
  const empty = times.length === 0

  const moveTo = (e: MouseEvent<SVGRectElement>) => {
    const box = plot.current?.getBoundingClientRect()
    if (!box || box.width === 0 || times.length === 0) return
    const t = from + ((e.clientX - box.left) / box.width) * span
    let best = 0
    times.forEach((c, i) => { if (Math.abs(c - t) < Math.abs(times[best] - t)) best = i })
    setCursor(best)
  }
  const keys = (e: KeyboardEvent<SVGGElement>) => {
    if (times.length === 0) return
    const last = times.length - 1
    const next = e.key === 'ArrowRight' ? Math.min(last, (cursor ?? last) + 1) : e.key === 'ArrowLeft' ? Math.max(0, (cursor ?? last) - 1) : e.key === 'Home' ? 0 : e.key === 'End' ? last : null
    if (next === null) return
    e.preventDefault(); setCursor(next)
  }
  const t = cursor !== null ? times[cursor] : null
  const readout = t === null
    ? (empty ? 'No samples in this range.' : 'Hover the chart, or focus it and use the arrow keys, for the values at a time.')
    : `${utcHMS(t)} UTC · ${canaryName} ${fmt(at(canary, t))} · ${stableName} ${fmt(at(stable, t))}${threshold !== null ? ` · threshold ${trim(threshold, digits)}${unit}` : ''}`

  return (
    <div className="dp-chart">
      <svg viewBox={`0 0 ${W} ${H}`} className="os-chart-svg" role="group" aria-label={summary} aria-roledescription="chart" aria-describedby={readoutId}>
        {ticks.map(v => <g key={v}><line x1={L} x2={W - R} y1={y(v)} y2={y(v)} className="os-chart-grid" /><text x={L - 6} y={y(v) + 3} textAnchor="end" className="os-chart-axis">{trim(v, digits)}{unit}</text></g>)}
        {canaryStart !== null && canaryStart < to && (
          <g>
            <rect x={x(Math.max(canaryStart, from))} y={T} width={Math.max(0, x(to) - x(Math.max(canaryStart, from)))} height={H - T - B} className="dp-canary-window" />
            <text x={x(Math.max(canaryStart, from)) + 4} y={H - B - 5} className="dp-chart-note">Canary started {utcHM(canaryStart)}</text>
          </g>
        )}
        {threshold !== null && <g><line x1={L} x2={W - R} y1={y(threshold)} y2={y(threshold)} className="dp-threshold" /><text x={W - R - 2} y={y(threshold) - 4} textAnchor="end" className="dp-chart-note threshold">{thresholdLabel}</text></g>}
        {stable.length > 0 && <path d={path(stable)} className="dp-line stable" />}
        {canary.length > 0 && <path d={path(canary)} className="dp-line canary" />}
        {timeTicks(from, to).map(tt => <text key={tt} x={x(tt)} y={H - 6} textAnchor="middle" className="os-chart-axis">{utcHM(tt).replace(' UTC', '')}</text>)}
        {t !== null && (
          <g aria-hidden="true">
            <line x1={x(t)} x2={x(t)} y1={T} y2={H - B} className="dp-cursor" />
            {at(canary, t) !== null && <circle cx={x(t)} cy={y(at(canary, t)!)} r={3.5} className="dp-dot canary" />}
            {at(stable, t) !== null && <circle cx={x(t)} cy={y(at(stable, t)!)} r={3.5} className="dp-dot stable" />}
          </g>
        )}
        <g tabIndex={0} className="dp-plot" aria-label={`${title}, ${times.length} samples. Left and Right arrow keys move along the time axis.`} role="group"
          onKeyDown={keys} onFocus={() => setCursor(c => c ?? (times.length ? times.length - 1 : null))} onBlur={() => setCursor(null)}>
          <rect ref={plot} x={L} y={T} width={W - L - R} height={H - T - B} className="os-chart-hit" onMouseMove={moveTo} onMouseLeave={() => setCursor(null)} />
        </g>
      </svg>
      <p id={readoutId} className="os-chart-readout dp-readout" aria-live="polite">{readout}</p>
      <details className="os-chart-data">
        <summary>View data</summary>
        <table className="os-table">
          <caption className="sr-only">{title}: samples in the range (UTC)</caption>
          <thead><tr><th scope="col">Time (UTC)</th><th scope="col" className="end">{canaryName}</th><th scope="col" className="end">{stableName}</th></tr></thead>
          <tbody>
            {times.length === 0 && <tr><td colSpan={3} className="os-table-empty">No samples in this range.</td></tr>}
            {times.map(tt => <tr key={tt}><th scope="row">{utcHMS(tt)}</th><td className="end">{fmt(at(canary, tt))}</td><td className="end">{fmt(at(stable, tt))}</td></tr>)}
          </tbody>
        </table>
      </details>
    </div>
  )
}
