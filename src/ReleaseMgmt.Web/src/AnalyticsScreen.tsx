import { lazy, Suspense, useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, get, type Me } from './api'
import { NONE_CELL, NO_DATA, chartTable, columnHeader, findingM2, findingM3, findingM4, findingM5, findingM6, findingM7, findingM8, findingM9, findingM10, findingM11, findingM13, findingM14, findingM15, headlines, normalize,
  type Metric, type MetricResponse, type Rec } from './analyticsFindings.ts'
import { download, exportName, svgWithTitle, toCsv } from './analyticsExport.ts'
import { fmtDayTime, zoneAbbr } from './time'
import { announce } from './announce'
import { useAction } from './useAction'

// The chart module carries ECharts: it is fetched only when the first chart is about to draw.
const AnalyticsChart = lazy(() => import('./AnalyticsChart'))

/**
 * Screen 2, Analytics (REOS-47, mockups/Analytics.html): a period line, six headline figures, then one chart per metric, each titled with the finding computed
 * from its own data. All 15 metrics load from GET /api/v1/analytics/{m}; M1 and M12 are headline figures only, M2/M10/M13 feed a headline and have a chart.
 * Exports per chart: CSV (client, toCsv), XLSX (server, /analytics/{m}/xlsx with the same window and as-of), SVG (the drawn chart). Decisions: Q-047a..f.
 */
const IDS = ['M1', 'M2', 'M3', 'M4', 'M5', 'M6', 'M7', 'M8', 'M9', 'M10', 'M11', 'M12', 'M13', 'M14', 'M15']
/** Metrics that are as-of snapshots and ignore the period (Q-046d). */
const SNAPSHOT = new Set(['M6', 'M9', 'M14'])

interface Spec { id: string; find: (r: Rec[]) => string; sub: string; height?: number; table?: boolean }
const SPECS: Spec[] = [
  { id: 'M2', find: findingM2, sub: 'Slip vs the baseline captured at Code Freeze, days · M2', height: 230 },
  { id: 'M3', find: findingM3, sub: 'Gate cycle time, first In progress to Certified or Waived, days · M3', height: 230 },
  { id: 'M7', find: findingM7, sub: 'Runbook step overrun vs plan, live runs, minutes · M7', height: 240 },
  { id: 'M10', find: findingM10, sub: 'Completed trains by close code · M10 (release-level, not DORA)', height: 170 },
  { id: 'M5', find: findingM5, sub: 'Gates certified after their business-day due date · M5', height: 190 },
  { id: 'M9', find: findingM9, sub: 'Open blockers by severity and age, as of now · M9', table: true },
  { id: 'M4', find: findingM4, sub: 'Gates certified at the first attempt, share of gates · M4', height: 190 },
  { id: 'M6', find: findingM6, sub: 'Products added or removed after Code Freeze, as of now · M6', height: 230 },
  { id: 'M8', find: findingM8, sub: 'Total runbook overrun per live run, minutes · M8', height: 230 },
  { id: 'M13', find: findingM13, sub: 'Compliance gates waived, share per month · M13', height: 220 },
  { id: 'M15', find: findingM15, sub: 'Trains completed per month and median lead time · M15 (release-level, not DORA)', height: 230 },
  { id: 'M11', find: findingM11, sub: 'Freeze windows overridden, and how long the overrides lasted · M11', table: true },
  { id: 'M14', find: findingM14, sub: 'External links by state, as of now · M14', table: true },
]

/** The headline tone as glyph + word, so colour is never the only cue (WCAG 1.4.1). */
const TONE = { ok: '✓ on track', warn: '▲ needs attention', bad: '✗ off track' } as const

interface Entry { metric?: Metric; error?: string }
type Loaded = Record<string, Entry>

async function loadAll(from: string, to: string): Promise<Loaded> {
  const q = from && to ? `?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}` : ''
  const res = await Promise.allSettled(IDS.map(id => get<MetricResponse>(`/api/v1/analytics/${id}${q}`)))
  const out: Loaded = {}
  res.forEach((r, i) => { out[IDS[i]] = r.status === 'fulfilled' ? { metric: normalize(r.value) } : { error: r.reason instanceof Error ? r.reason.message : String(r.reason) } })
  return out
}

export default function AnalyticsScreen(_props: { me: Me }) {
  const [data, setData] = useState<Loaded | null>(null)
  const [from, setFrom] = useState(''), [to, setTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [fatal, setFatal] = useState<string | null>(null)
  const seq = useRef(0)

  // `say`: after Apply or Retry the new figures replace the old ones, so the result is spoken once (WCAG 4.1.3); the first load is not announced.
  const run = useCallback(async (f: string, t: string, say = true) => {
    const my = ++seq.current
    setBusy(true); setFatal(null)
    try {
      const d = await loadAll(f, t)
      if (my !== seq.current) return
      setData(d)
      const any = Object.values(d).find(e => e.metric)?.metric
      if (any) { setFrom(any.from); setTo(any.to) }
      else setFatal(Object.values(d)[0]?.error ?? 'Analytics could not be loaded')
      if (say && any) {
        const failed = SPECS.filter(x => d[x.id]?.error).length
        const range = `${any.from} to ${any.to}`
        announce(`Loaded ${SPECS.length - failed} charts for ${range}${failed ? `; ${failed} could not load` : ''}.`)
      }
    } finally { if (my === seq.current) setBusy(false) }
  }, [])
  useEffect(() => { void run('', '', false) }, [run])

  const bad = !!from && !!to && from > to
  const apply = (e: FormEvent) => { e.preventDefault(); if (!bad) void run(from, to) }
  const failed = data && !fatal ? SPECS.filter(x => data[x.id]?.error).length : 0

  const rec = (id: string) => data?.[id]?.metric?.recs
  const kpis = headlines({ M1: rec('M1'), M2: rec('M2'), M10: rec('M10'), M12: rec('M12'), M13: rec('M13') })
  const asOf = Object.values(data ?? {}).find(e => e.metric)?.metric?.asOf
  const trains = rec('M1')?.[0]?.completed, gates = rec('M3')?.reduce((a, r) => a + (Number(r.samples) || 0), 0), steps = rec('M7')?.length

  return <>
    <section className="an-head">
      <div>
        <h1>Analytics</h1>
        <form className="an-filter" onSubmit={apply} aria-label="Period">
          <label>Period from <input className="line" type="date" value={from} onChange={e => setFrom(e.target.value)} required /></label>
          <label>to <input className="line" type="date" value={to} onChange={e => setTo(e.target.value)} required /></label>
          <button type="submit" className="text" disabled={busy || bad || !from || !to}>{busy ? 'Loading…' : 'Apply'}</button>
          {typeof trains === 'number' && <span className="muted">{trains} completed {trains === 1 ? 'train' : 'trains'} · {gates ?? 0} gates timed · {steps ?? 0} live steps</span>}
        </form>
        {bad && <div className="bad an-note" role="alert">From must not be after to.</div>}
      </div>
      <div className="an-side muted">{asOf ? <>Data as of <span className="mono">{fmtDayTime(asOf)} {zoneAbbr(asOf)}</span></> : ' '}<br />Blockers, scope churn and sync health are as of now and ignore the period.</div>
    </section>

    {fatal && <p className="bad" role="alert">✗ {fatal} <button type="button" className="text" disabled={busy} onClick={() => void run(from, to)}>{busy ? 'Retrying…' : 'Retry'}</button></p>}
    {failed > 0 && <p className="bad" role="alert">✗ {failed} {failed === 1 ? 'chart' : 'charts'} could not load. <button type="button" className="text" disabled={busy} onClick={() => void run(from, to)}>{busy ? 'Retrying…' : 'Retry'}</button></p>}

    <section className="an-kpis" aria-label="Headline figures">
      {kpis.map(k => <div key={k.key} className="an-kpi" data-testid="kpi" data-kpi={k.key}>
        <div className="group-label">{k.label}</div>
        <div className={`mono an-kpi-value ${k.tone === 'none' ? '' : k.tone}`}>{data ? k.value : '…'}</div>
        <div className="muted an-kpi-sub">{data && k.tone !== 'none' && <span className={k.tone}>{TONE[k.tone]} · </span>}{data ? k.sub : 'Loading'}</div>
      </div>)}
    </section>

    <section className="an-charts" aria-label="Charts">
      {SPECS.map(s => <ChartCard key={s.id} spec={s} entry={data?.[s.id]} loading={!data} />)}
    </section>
  </>
}

function ChartCard({ spec, entry, loading }: { spec: Spec; entry?: Entry; loading: boolean }) {
  const m = entry?.metric
  // while loading the heading names the chart (its subtitle) instead of thirteen identical "Loading…" headings (WCAG 2.4.6)
  const finding = m ? spec.find(m.recs) : loading ? spec.sub : 'Could not load this chart'
  const empty = !!m && (m.recs.length === 0 || finding === NO_DATA)
  const [showData, setShowData] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const svgRef = useRef<(() => string) | null>(null)
  const snap = SNAPSHOT.has(spec.id)
  const tid = `an-data-${spec.id}`
  const headline = m?.title ?? spec.id

  const name = (ext: 'csv' | 'xlsx' | 'svg') => exportName(spec.id, m!.from, m!.to, ext, snap)
  const exportCsv = () => {
    setErr(null)
    try { download(name('csv'), new Blob([toCsv(m!.cells, m!.columns.map(columnHeader), { bom: true })], { type: 'text/csv;charset=utf-8' })) }
    catch (e) { setErr(`CSV export failed: ${(e as Error).message}`) }
  }
  const { run: act, pending } = useAction()
  const exportXlsx = () => act('xlsx', async () => {
    setErr(null)
    try {
      const r = await fetch(`/api/v1/analytics/${spec.id}/xlsx?from=${encodeURIComponent(m!.from)}&to=${encodeURIComponent(m!.to)}&now=${encodeURIComponent(m!.asOf)}`)
      if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
      download(name('xlsx'), await r.blob())
    } catch (e) { setErr(`XLSX export failed: ${(e as Error).message}`) }
  })
  const exportSvg = () => {
    setErr(null)
    try {
      const svg = svgRef.current?.()
      if (!svg) throw new Error('the chart is not drawn yet')
      download(name('svg'), new Blob([svgWithTitle(svg, finding, `${spec.sub}. Data as of ${m!.asOf}.`)], { type: 'image/svg+xml;charset=utf-8' }))
    } catch (e) { setErr(`SVG export failed: ${(e as Error).message}`) }
  }

  const table = m && !empty ? chartTable(spec.id, m.recs, m.columns) : null
  return <div className="an-chart" data-testid="chart" data-metric={spec.id} aria-busy={loading || undefined}>
    <div className="an-chart-head">
      <h2 className="an-title" data-testid="chart-title">{finding}</h2>
      {m && !empty && <span className="an-export" role="group" aria-label={`Export ${headline}`}>
        <button type="button" className="text" onClick={exportCsv} aria-label={`Export ${headline} as CSV`}>CSV</button>
        {' · '}
        <button type="button" className="text" disabled={!!pending} onClick={() => void exportXlsx()} aria-label={`Export ${headline} as XLSX`}>{pending ? 'XLSX…' : 'XLSX'}</button>
        {!spec.table && <>{' · '}<button type="button" className="text" onClick={exportSvg} aria-label={`Export ${headline} as SVG`}>SVG</button></>}
      </span>}
    </div>
    <div className="muted an-sub">{loading ? 'Loading…' : spec.sub}</div>
    {entry?.error && <p className="bad">✗ {entry.error}</p>}
    {empty && <p className="muted an-empty" data-testid="chart-empty">{NO_DATA}</p>}
    {m && !empty && !spec.table && <Suspense fallback={<div className="an-canvas muted" style={{ height: spec.height }}>Loading chart…</div>}>
      <AnalyticsChart id={spec.id} recs={m.recs} finding={finding} height={spec.height ?? 220} svgRef={svgRef} />
    </Suspense>}
    {m && !empty && !spec.table && <div className="an-viewdata"><button type="button" className="text" aria-expanded={showData} aria-controls={showData ? tid : undefined} onClick={() => setShowData(v => !v)}>{showData ? 'Hide data' : 'View data'}</button></div>}
    {table && (spec.table || showData) && <DataTable id={tid} spec={spec} table={table} label={finding} />}
    {err && <div className="bad an-note" role="alert">✗ {err}</div>}
  </div>
}

function DataTable({ id, spec, table, label }: { id: string; spec: Spec; table: ReturnType<typeof chartTable>; label: string }) {
  return <div className="an-table" id={id} tabIndex={0} role="region" aria-label={`${label}: data`}>
    <table className="grid" data-testid="chart-data">
      <thead><tr>{table.headers.map((h, i) => <th key={h} scope="col" className={table.numeric[i] ? 'n' : undefined}>{h}</th>)}</tr></thead>
      <tbody>{table.rows.map((r, ri) => <tr key={ri}>{r.map((c, ci) => <td key={ci} className={table.numeric[ci] ? 'n mono' : undefined}>{c === NONE_CELL ? <><span aria-hidden="true">{c}</span><span className="sr-only">none</span></> : c}</td>)}</tr>)}</tbody>
    </table>
    {spec.id === 'M7' && <div className="muted an-note">Average and worst per step code across live runs; the CSV and XLSX carry every step of every run.</div>}
  </div>
}
