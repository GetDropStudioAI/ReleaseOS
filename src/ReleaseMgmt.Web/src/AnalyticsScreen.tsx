import { lazy, Suspense, useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, get, type Me } from './api'
import { NONE_CELL, NO_DATA, chartTable, columnHeader, findingM2, findingM3, findingM4, findingM5, findingM6, findingM7, findingM8, findingM9, findingM10, findingM11, findingM13, findingM14, findingM15, headlines, normalize,
  type Metric, type MetricResponse, type Rec } from './analyticsFindings.ts'
import { download, exportName, presetRange, svgWithTitle, toCsv, typeParam, workbookName, workbookUrl } from './analyticsExport.ts'
import { listReleaseTypes } from './mgmt/releaseTypesApi.ts'
import { sortTypes, type ReleaseTypeRow } from './mgmt/releaseTypesLogic.ts'
import { dueText } from './shell/approvalsApi.ts'
import { chosenWorkspaces, defaultChoice, defaultGroup, execQuery, groupingsFor, parseView, portfolioPdfName, portfolioPdfUrl, quarterBasis, scopeHeading, VIEWS, type AnalyticsView, type Scope, type WorkspaceChoice } from './analyticsExecutive.ts'
import { Button, ErrorState, FilterSelect, LoadingState, MetricCard, Notice, PageHeading, Panel, TabPanel, Tabs, type Tone } from './ui'
import './analytics.css'
import { announce } from './announce'
import { useAction } from './useAction'

// The chart module carries ECharts: it is fetched only when the first chart is about to draw.
const AnalyticsChart = lazy(() => import('./AnalyticsChart'))
// REOS-121: the VP and Director views are fetched when first opened.
const AnalyticsExecutive = lazy(() => import('./AnalyticsViews'))

/**
 * Screen 2, Analytics (REOS-47, mockups/Analytics.html): a period line, six headline figures, then one chart per metric, each titled with the finding computed
 * from its own data. All 15 metrics load from GET /api/v1/analytics/{m}; M1 and M12 are headline figures only, M2/M10/M13 feed a headline and have a chart.
 * Exports per chart: CSV (client, toCsv), XLSX (server, /analytics/{m}/xlsx with the same window and as-of), SVG (the drawn chart). Decisions: Q-047a..f.
 */
const IDS = ['M1', 'M2', 'M3', 'M4', 'M5', 'M6', 'M7', 'M8', 'M9', 'M10', 'M11', 'M12', 'M13', 'M14', 'M15']
/** Metrics that are as-of snapshots and ignore the period (Q-046d). */
const SNAPSHOT = new Set(['M6', 'M9', 'M14'])

export interface Spec { id: string; find: (r: Rec[]) => string; sub: string; height?: number; table?: boolean }
export const SPECS: Spec[] = [
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
const CARD_TONE: Record<'ok' | 'warn' | 'bad', Tone> = { ok: 'success', warn: 'warning', bad: 'danger' }

/** Period presets end on the as-of UTC day; they only fill the two dates and apply them, the same query as typing the dates. */
const PRESETS = [{ label: 'Last 30 days', days: 30 }, { label: 'Last 90 days', days: 90 }, { label: 'Last 12 months', days: 365 }]

export interface Entry { metric?: Metric; error?: string }
type Loaded = Record<string, Entry>

async function loadAll(from: string, to: string, type: string, choice: WorkspaceChoice): Promise<Loaded> {
  const q = execQuery({ from, to, type, choice })
  const res = await Promise.allSettled(IDS.map(id => get<MetricResponse>(`/api/v1/analytics/${id}${q}`)))
  const out: Loaded = {}
  res.forEach((r, i) => { out[IDS[i]] = r.status === 'fulfilled' ? { metric: normalize(r.value) } : { error: r.reason instanceof Error ? r.reason.message : String(r.reason) } })
  return out
}

export default function AnalyticsScreen(_props: { me: Me }) {
  const [data, setData] = useState<Loaded | null>(null)
  const [from, setFrom] = useState(''), [to, setTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [type, setType] = useState('')   // REOS-116: a release type id, or '' for all
  const typeRef = useRef('')
  const [types, setTypes] = useState<ReleaseTypeRow[]>([])
  useEffect(() => { listReleaseTypes(true).then(r => setTypes(sortTypes(r))).catch(() => setTypes([])) }, [])
  const [fatal, setFatal] = useState<string | null>(null)
  const seq = useRef(0)
  // REOS-121 (D49): the view (kept in ?view=), the workspaces it reads (only the person's own, from /analytics/scope) and how the executive views group their rows
  const [view, setView] = useState<AnalyticsView>(() => parseView(new URLSearchParams(window.location.search).get('view')))
  const [scope, setScope] = useState<Scope | null>(null)
  const [scopeErr, setScopeErr] = useState<string | null>(null)
  const [choice, setChoice] = useState<WorkspaceChoice>(() => defaultChoice(view))
  const choiceRef = useRef(choice)
  const [groupBy, setGroupBy] = useState(() => defaultGroup(view))
  const [applied, setApplied] = useState({ from: '', to: '' })
  const [pdfErr, setPdfErr] = useState<string | null>(null)
  const [asOfExec, setAsOfExec] = useState('')
  useEffect(() => { get<Scope>('/api/v1/analytics/scope').then(setScope).catch(e => setScopeErr((e as Error).message)) }, [])
  const changeView = (v: AnalyticsView) => {
    setView(v); setChoice(defaultChoice(v)); choiceRef.current = defaultChoice(v); setGroupBy(defaultGroup(v))
    const u = new URL(window.location.href); if (v === 'ops') u.searchParams.delete('view'); else u.searchParams.set('view', v)
    window.history.replaceState(window.history.state, '', u)
  }
  const onWindow = useCallback((w: { from: string; to: string; asOf: string }) => { setFrom(w.from); setTo(w.to); setAsOfExec(w.asOf) }, [])
  const [workbookErr, setWorkbookErr] = useState<string | null>(null)
  const { run: act, pending } = useAction()

  // `say`: after Apply or Retry the new figures replace the old ones, so the result is spoken once (WCAG 4.1.3); the first load is not announced.
  const run = useCallback(async (f: string, t: string, say = true, ty = typeRef.current, ch = choiceRef.current) => {
    const my = ++seq.current
    setBusy(true); setFatal(null)
    try {
      const d = await loadAll(f, t, ty, ch)
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
  useEffect(() => { if (view === 'ops') void run('', '', false) }, [run, view])

  const bad = !!from && !!to && from > to
  const apply = (e: FormEvent) => { e.preventDefault(); if (bad) return; if (view === 'ops') void run(from, to); else setApplied({ from, to }) }
  const failed = data && !fatal ? SPECS.filter(x => data[x.id]?.error).length : 0

  const rec = (id: string) => data?.[id]?.metric?.recs
  const kpis = headlines({ M1: rec('M1'), M2: rec('M2'), M10: rec('M10'), M12: rec('M12'), M13: rec('M13') })
  const asOf = (view === 'ops' ? Object.values(data ?? {}).find(e => e.metric)?.metric?.asOf : asOfExec) || undefined
  const trains = rec('M1')?.[0]?.completed, gates = rec('M3')?.reduce((a, r) => a + (Number(r.samples) || 0), 0), steps = rec('M7')?.length

  const exportPdf = () => act('pdf', async () => {
    if (!asOf) return
    setPdfErr(null)
    try {
      const r = await fetch(portfolioPdfUrl({ from, to, now: asOf, choice }))
      if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
      download(portfolioPdfName(from, to), await r.blob())
    } catch (e) { setPdfErr((e as Error).message) }
  })
  const exportWorkbook = () => act('workbook', async () => {
    const m = view === 'ops' ? Object.values(data ?? {}).find(e => e.metric)?.metric : asOf ? { from, to, asOf } : undefined
    if (!m) return
    setWorkbookErr(null)
    try {
      const r = await fetch(workbookUrl(m.from, m.to, m.asOf, type) + execQuery({ choice, groupBy, grouped: true }).replace(/^\?/, '&'))
      if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
      download(workbookName(m.from, m.to), await r.blob())
    } catch (e) { setWorkbookErr((e as Error).message) }
  })

  const asOfText = asOf ? dueText({ dueAt: asOf, dueOn: null }) : null
  const heading = <PageHeading trail={['Workspace', 'Analytics']} title="Analytics"
    description={<>{asOfText ? <>Data as of <span className="mono">{asOfText.utc}</span>{asOfText.local ? <> ({asOfText.local})</> : null}. </> : null}Blockers, scope churn and links are as of now and ignore the period.</>}
    actions={asOf ? <>
      {view === 'vp' && <Button disabled={!!pending} onClick={() => void exportPdf()} aria-label="Download the portfolio report as a PDF">{pending === 'pdf' ? 'Report…' : 'Portfolio report (PDF)'}</Button>}
      <Button disabled={!!pending} onClick={() => void exportWorkbook()} aria-label="Export all metrics as one XLSX workbook">{pending === 'workbook' ? 'Workbook…' : 'Workbook (XLSX)'}</Button>
    </> : undefined} />

  const period = <div>
    <form className="an-period" onSubmit={apply} aria-label="Period">
      <label className="an-period-field">Period from <input className="an-date" type="date" value={from} onChange={e => setFrom(e.target.value)} required /></label>
      <label className="an-period-field">to <input className="an-date" type="date" value={to} onChange={e => setTo(e.target.value)} required /></label>
      <Button type="submit" variant="primary" disabled={busy || bad || !from || !to} aria-describedby={bad ? 'an-period-error' : undefined}>{busy ? 'Loading…' : 'Apply'}</Button>
      {asOf && <span className="an-presets" role="group" aria-label="Quick periods">
        {PRESETS.map(p => <Button key={p.days} disabled={busy} onClick={() => { const r = presetRange(asOf, p.days); setFrom(r.from); setTo(r.to); if (view === 'ops') void run(r.from, r.to); else setApplied(r) }}>{p.label}</Button>)}
      </span>}
      <FilterSelect label="Release type" value={type} options={[{ value: '', label: 'All release types' }, ...types.map(t => ({ value: t.id, label: t.name }))]}
        onChange={v => { setType(v); typeRef.current = v; if (view === 'ops') void run(from, to, true, v) }} />
      {typeof trains === 'number' && <span className="an-period-count">{trains} completed {trains === 1 ? 'train' : 'trains'} · {gates ?? 0} gates timed · {steps ?? 0} live steps · dates are UTC days</span>}
    </form>
    {workbookErr && <div role="alert"><Notice tone="danger" title="Workbook export failed">{workbookErr}</Notice></div>}
    {pdfErr && <div role="alert"><Notice tone="danger" title="Portfolio report failed">{pdfErr}</Notice></div>}
    {bad && <p id="an-period-error" className="an-period-error" role="alert">From must not be after to.</p>}
  </div>


  // the workspaces this view reads: the person's own memberships only (GET /analytics/scope); one workspace means nothing to choose, and the screen says so
  const mine = scope?.workspaces ?? []
  const pickWorkspaces = (v: string) => { setChoice(v); choiceRef.current = v; if (view === 'ops') void run(from, to, true, typeRef.current, v) }
  const wsOptions = [...(view === 'ops' ? [{ value: '', label: 'Current workspace' }] : []), { value: 'all', label: `All my workspaces (${mine.length})` }, ...mine.map(w => ({ value: w.slug, label: w.name }))]
  const shown = scope ? chosenWorkspaces(scope, choice) : []
  const controls = <div className="an-scope">
    <Tabs tabs={VIEWS} value={view} onChange={v => changeView(v as AnalyticsView)} label="Analytics view" group="an-view" />
    <div className="an-scope-row">
      {scopeErr ? <Notice tone="danger" title="Your workspaces could not be loaded">{scopeErr}</Notice>
        : !scope ? <span className="an-note">Loading your workspaces…</span>
        : mine.length <= 1 ? <span className="an-note" data-testid="an-scope-single">Workspace: {mine[0]?.name ?? 'none'}. You belong to one workspace, so there is nothing to combine.</span>
        : <span className="an-period-field"><span aria-hidden="true">Workspaces</span><FilterSelect label="Workspaces" value={choice} options={wsOptions} onChange={pickWorkspaces} /></span>}
      {view !== 'ops' && <span className="an-period-field"><span aria-hidden="true">Group by</span><FilterSelect label="Group by" value={groupBy} options={groupingsFor(view).map(g => ({ value: g.value, label: g.label }))} onChange={setGroupBy} /></span>}
    </div>
    {scope && <p className="an-note" data-testid="an-scope-heading">{scopeHeading(shown, mine.length)}{view !== 'ops' ? <> · {quarterBasis(scope)}</> : null}</p>}
  </div>

  if (view !== 'ops') return <div className="os-page">{heading}{controls}{period}
    <TabPanel id={view} group="an-view">
      <Suspense fallback={<LoadingState what="analytics" />}>
        <AnalyticsExecutive view={view} choice={choice} groupBy={groupBy} from={applied.from} to={applied.to} type={type} onWindow={onWindow} />
      </Suspense>
    </TabPanel>
  </div>
  if (!data) return <div className="os-page">{heading}{controls}{period}<LoadingState what="analytics" /></div>
  return <div className="os-page">
    {heading}
    {controls}
    {period}
    <TabPanel id="ops" group="an-view">
    {fatal && <ErrorState what="Analytics" message={fatal} onRetry={busy ? undefined : () => void run(from, to)} />}
    {failed > 0 && <ErrorState what={`${failed} ${failed === 1 ? 'chart' : 'charts'}`} message={`${failed === 1 ? 'It' : 'They'} did not load; the others are current.`} onRetry={busy ? undefined : () => void run(from, to)} />}

    <section className="os-metrics an-metrics" aria-label="Headline figures">
      {kpis.map(k => <div key={k.key} data-testid="kpi" data-kpi={k.key}>
        <MetricCard label={k.label} value={k.value} tone={k.tone === 'none' ? 'neutral' : CARD_TONE[k.tone]} detail={<>{k.tone !== 'none' && <>{TONE[k.tone]} · </>}{k.sub}</>} />
      </div>)}
    </section>

    <section className="an-charts" aria-label="Charts">
      {SPECS.map(s => <ChartCard key={s.id} spec={s} entry={data[s.id]} type={type} extraQuery={execQuery({ choice }).replace(/^\?/, '&')} />)}
    </section>
    </TabPanel>
  </div>
}

export function ChartCard({ spec, entry, type, extraQuery = '' }: { spec: Spec; entry?: Entry; type: string; extraQuery?: string }) {
  const m = entry?.metric
  const finding = m ? spec.find(m.recs) : 'Could not load this chart'
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
      const r = await fetch(`/api/v1/analytics/${spec.id}/xlsx?from=${encodeURIComponent(m!.from)}&to=${encodeURIComponent(m!.to)}&now=${encodeURIComponent(m!.asOf)}${typeParam(type)}${extraQuery}`)
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
  const exports = m && !empty && <span className="an-export" role="group" aria-label={`Export ${headline}`}>
    <Button onClick={exportCsv} aria-label={`Export ${headline} as CSV`}>CSV</Button>
    <Button disabled={!!pending} onClick={() => void exportXlsx()} aria-label={`Export ${headline} as XLSX`}>{pending ? 'XLSX…' : 'XLSX'}</Button>
    {!spec.table && <Button onClick={exportSvg} aria-label={`Export ${headline} as SVG`}>SVG</Button>}
  </span>
  return <div className="an-card" data-testid="chart" data-metric={spec.id}>
    <Panel title={<span data-testid="chart-title">{finding}</span>} subtitle={spec.sub} meta={exports || undefined}>
      {entry?.error && <ErrorState what={headline} message={entry.error} />}
      {empty && <p className="an-note" data-testid="chart-empty">{NO_DATA}</p>}
      {m && !empty && !spec.table && <Suspense fallback={<div className="an-canvas" style={{ height: spec.height }}><LoadingState what="the chart" /></div>}>
        <AnalyticsChart id={spec.id} recs={m.recs} finding={finding} height={spec.height ?? 220} svgRef={svgRef} />
      </Suspense>}
      {m && !empty && !spec.table && <div className="an-chart-actions"><Button aria-expanded={showData} aria-controls={showData ? tid : undefined} onClick={() => setShowData(v => !v)}>{showData ? 'Hide data' : 'View data'}</Button></div>}
      {table && (spec.table || showData) && <DataTable id={tid} spec={spec} table={table} label={finding} />}
      {err && <div role="alert"><Notice tone="danger" title={err.split(': ')[0]}>{err.includes(': ') ? err.slice(err.indexOf(': ') + 2) : undefined}</Notice></div>}
    </Panel>
  </div>
}

function DataTable({ id, spec, table, label }: { id: string; spec: Spec; table: ReturnType<typeof chartTable>; label: string }) {
  return <div className="an-table" id={id} tabIndex={0} role="region" aria-label={`${label}: data`}>
    <table className="os-table" data-testid="chart-data">
      <thead><tr>{table.headers.map((h, i) => <th key={h} scope="col" className={table.numeric[i] ? 'end' : undefined}>{h}</th>)}</tr></thead>
      <tbody>{table.rows.map((r, ri) => <tr key={ri}>{r.map((c, ci) => <td key={ci} className={table.numeric[ci] ? 'end mono' : undefined}>{c === NONE_CELL ? <><span aria-hidden="true">{c}</span><span className="sr-only">none</span></> : c}</td>)}</tr>)}</tbody>
    </table>
    {spec.id === 'M7' && <p className="an-note">Average and worst per step code across live runs; the CSV and XLSX carry every step of every run.</p>}
  </div>
}
