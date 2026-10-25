import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, get } from './api'
import { download, toCsv } from './analyticsExport.ts'
import { normalize, type Metric, type MetricResponse } from './analyticsFindings.ts'
import { directorHeadlines, execQuery, execTable, groupList, onTimeByQuarter, onTimeFinding, vpHeadlines, type AnalyticsView, type ExecHeadline, type WorkspaceChoice } from './analyticsExecutive.ts'
import { ChartCard, SPECS } from './AnalyticsScreen'
import { Button, ErrorState, LoadingState, MetricCard, Notice, Panel, ScrollRegion, type Tone } from './ui'
import { useAction } from './useAction'

// The same lazy chart module as the Operations view: ECharts is fetched when the first chart is about to draw.
const AnalyticsChart = lazy(() => import('./AnalyticsChart'))

/**
 * The VP and Director views (REOS-121, D49). Every panel is one metric from GET /api/v1/analytics/{M} for the chosen workspaces (only the person's own can be chosen) and grouping,
 * with its table beside it as the text equivalent of any chart. A panel that fails says so and the others stay current. M16 to M24 take groupBy; M3 and M9 do not.
 */
interface Entry { metric?: Metric; error?: string }
type Loaded = Record<string, Entry>
const TONE = { ok: '✓ on track', warn: '▲ needs attention', bad: '✗ off track' } as const
const CARD_TONE: Record<'ok' | 'warn' | 'bad', Tone> = { ok: 'success', warn: 'warning', bad: 'danger' }

/** key -> [metric, grouped by the choice | ungrouped total | by quarter] */
type Ask = { id: string; mode: 'chosen' | 'total' | 'quarter' | 'plain' }
const VP: Record<string, Ask> = {
  M17: { id: 'M17', mode: 'chosen' }, M17t: { id: 'M17', mode: 'total' }, M17q: { id: 'M17', mode: 'quarter' }, M16: { id: 'M16', mode: 'chosen' }, M16t: { id: 'M16', mode: 'total' },
  M18: { id: 'M18', mode: 'chosen' }, M18t: { id: 'M18', mode: 'total' }, M21: { id: 'M21', mode: 'chosen' }, M21t: { id: 'M21', mode: 'total' }, M23: { id: 'M23', mode: 'chosen' },
}
const DIRECTOR: Record<string, Ask> = {
  M22: { id: 'M22', mode: 'chosen' }, M22t: { id: 'M22', mode: 'total' }, M17: { id: 'M17', mode: 'chosen' }, M3: { id: 'M3', mode: 'plain' }, M9: { id: 'M9', mode: 'plain' },
  M20: { id: 'M20', mode: 'chosen' }, M20t: { id: 'M20', mode: 'total' }, M19: { id: 'M19', mode: 'chosen' }, M24: { id: 'M24', mode: 'chosen' }, M24t: { id: 'M24', mode: 'total' },
}

export default function AnalyticsExecutive({ view, choice, groupBy, from, to, type, onWindow }: {
  view: Exclude<AnalyticsView, 'ops'>; choice: WorkspaceChoice; groupBy: string; from: string; to: string; type: string
  onWindow: (w: { from: string; to: string; asOf: string }) => void
}) {
  const [data, setData] = useState<Loaded | null>(null)
  const [busy, setBusy] = useState(false)
  const seq = useRef(0)
  const asks = view === 'vp' ? VP : DIRECTOR

  const load = useCallback(async () => {
    const my = ++seq.current
    setBusy(true)
    const keys = Object.keys(asks)
    const q = (a: Ask) => execQuery({ from, to, choice, type, grouped: a.mode !== 'plain', groupBy: a.mode === 'chosen' ? groupBy : a.mode === 'quarter' ? 'quarter' : '' })
    const res = await Promise.allSettled(keys.map(k => get<MetricResponse>(`/api/v1/analytics/${asks[k].id}${q(asks[k])}`)))
    if (my !== seq.current) return
    const out: Loaded = {}
    res.forEach((r, i) => { out[keys[i]] = r.status === 'fulfilled' ? { metric: normalize(r.value) } : { error: r.reason instanceof Error ? r.reason.message : String(r.reason) } })
    setData(out); setBusy(false)
    const any = Object.values(out).find(e => e.metric)?.metric
    if (any) onWindow({ from: any.from, to: any.to, asOf: any.asOf })
  }, [asks, from, to, choice, groupBy, type, onWindow])
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { void load() }, [view, choice, groupBy, from, to, type])

  if (!data) return <LoadingState what={view === 'vp' ? 'the VP view' : 'the Director view'} />
  const recs = (k: string) => data[k]?.metric?.recs
  const failed = Object.values(data).filter(e => e.error).length
  const everything = failed === Object.keys(data).length
  const cards: ExecHeadline[] = view === 'vp'
    ? vpHeadlines({ M17: recs('M17t'), M16: recs('M16t'), M18: recs('M18t'), M21: recs('M21t') })
    : directorHeadlines({ M22: recs('M22t'), M9: recs('M9'), M20: recs('M20t'), M24: recs('M24t') })
  const gb = groupList(groupBy)
  const scopeQ = { from: '', to: '', choice, groupBy, type }
  const asOf = Object.values(data).find(e => e.metric)?.metric?.asOf ?? ''

  return <>
    {everything && <ErrorState what="Analytics" message={Object.values(data)[0].error ?? 'Analytics could not be loaded'} onRetry={busy ? undefined : () => void load()} />}
    {!everything && failed > 0 && <ErrorState what={`${failed} ${failed === 1 ? 'panel' : 'panels'}`} message={`${failed === 1 ? 'It' : 'They'} did not load; the others are current.`} onRetry={busy ? undefined : () => void load()} />}

    <section className="os-metrics an-metrics" aria-label="Headline figures">
      {cards.map(k => <div key={k.key} data-testid="kpi" data-kpi={k.key}>
        <MetricCard label={k.label} value={k.value} tone={k.tone === 'none' ? 'neutral' : CARD_TONE[k.tone]} detail={<>{k.tone !== 'none' && <>{TONE[k.tone]} · </>}{k.sub}</>} />
      </div>)}
    </section>

    {view === 'vp' ? <section className="an-exec" aria-label="VP panels">
      <OnTimeChart entry={data.M17q} />
      <MetricPanel id="M17" entry={data.M17} groupBy={gb} q={scopeQ} asOf={asOf} sub="Releases completed in the period, CERT and Production measured separately against the baseline; change failure is release-level, not DORA" />
      <MetricPanel id="M16" entry={data.M16} groupBy={gb} q={scopeQ} asOf={asOf} sub="First failed step to the end of a rolled-back live run" />
      <MetricPanel id="M18" entry={data.M18} groupBy={gb} q={scopeQ} asOf={asOf} sub="Waivers and freeze overrides granted, Go conditions left open past expiry, gates decertified, Compliance gates certified late, reviews overdue" />
      <MetricPanel id="M21" entry={data.M21} groupBy={gb} q={scopeQ} asOf={asOf} sub="Open releases by risk tier, as of now (ignores the period)" />
      <MetricPanel id="M23" entry={data.M23} groupBy={gb} q={scopeQ} asOf={asOf} sub="Deployments linked to a release that finished in the period, and confirmed threshold breaches on them" />
    </section> : <section className="an-exec" aria-label="Director panels">
      <MetricPanel id="M22" entry={data.M22} groupBy={gb} q={scopeQ} asOf={asOf} sub="Releases completed and lead time, created to complete" />
      <MetricPanel id="M17" entry={data.M17} groupBy={gb} q={scopeQ} asOf={asOf} sub="On time, slip and change failure by the chosen grouping, CERT and Production separately" />
      <section className="an-charts" aria-label="Gate cycle time and blocker ageing">
        {(['M3', 'M9'] as const).map(id => <ChartCard key={id} spec={SPECS.find(s => s.id === id)!} entry={data[id]} type={type} extraQuery={execQuery({ choice }).replace(/^\?/, '&')} />)}
      </section>
      <MetricPanel id="M20" entry={data.M20} groupBy={gb} q={scopeQ} asOf={asOf} sub="Target-date moves after the baseline, and the days they moved by"
        note="A move has a recorded reason only when a No-Go decision set that date. Manual moves keep no reason code yet, so they show as Not recorded." />
      <MetricPanel id="M19" entry={data.M19} groupBy={gb} q={scopeQ} asOf={asOf} sub="Waiver requested to approved, freeze override requested to granted, a gate's last task done to certified" />
      <MetricPanel id="M24" entry={data.M24} groupBy={gb} q={scopeQ} asOf={asOf} sub="Reviews of releases that ended in the period: held by their due date, overdue, and their actions" />
    </section>}
  </>
}

function OnTimeChart({ entry }: { entry?: Entry }) {
  const m = entry?.metric
  const pts = m ? onTimeByQuarter(m.recs) : []
  const finding = m ? onTimeFinding(pts) : 'Could not load this chart'
  const svgRef = useRef<(() => string) | null>(null)
  return <div className="an-card" data-testid="exec-panel" data-metric="M17-quarter">
    <Panel title={<span data-testid="chart-title">{finding}</span>} subtitle="On time % by quarter, CERT and Production, against the baseline · M17">
      {entry?.error && <ErrorState what="On time by quarter" message={entry.error} />}
      {m && pts.length === 0 && <p className="an-note" data-testid="chart-empty">No data in this window</p>}
      {m && pts.length > 0 && <>
        <Suspense fallback={<div className="an-canvas" style={{ height: 220 }}><LoadingState what="the chart" /></div>}>
          <AnalyticsChart id="VP_ONTIME" recs={pts as unknown as Record<string, unknown>[]} finding={finding} height={220} svgRef={svgRef} />
        </Suspense>
        <ScrollRegion label="On time by quarter">
          <table className="os-table" data-testid="exec-table" aria-label="On time by quarter: data">
            <thead><tr><th scope="col">Quarter</th><th scope="col" className="end">CERT on time %</th><th scope="col" className="end">Production on time %</th></tr></thead>
            <tbody>{pts.map(p => <tr key={p.label}><td>{p.label}</td><td className="end mono">{p.CERT === null ? '—' : `${p.CERT}%`}</td><td className="end mono">{p.Production === null ? '—' : `${p.Production}%`}</td></tr>)}</tbody>
          </table>
        </ScrollRegion>
      </>}
    </Panel>
  </div>
}

function MetricPanel({ id, entry, groupBy, q, asOf, sub, note }: { id: string; entry?: Entry; groupBy: string[]; q: { choice: WorkspaceChoice; groupBy: string; type: string }; asOf: string; sub: string; note?: string }) {
  const m = entry?.metric
  const { run: act, pending } = useAction()
  const [err, setErr] = useState<string | null>(null)
  const table = m ? execTable(m, groupBy) : null
  const name = (ext: string) => `analytics-${id.toLowerCase()}-${m!.from}_${m!.to}.${ext}`
  const csv = () => {
    setErr(null)
    try { download(name('csv'), new Blob([toCsv(m!.recs.map(r => table!.csvHeaders.map(h => r[h])), table!.csvHeaders, { bom: true })], { type: 'text/csv;charset=utf-8' })) }
    catch (e) { setErr(`CSV export failed: ${(e as Error).message}`) }
  }
  const xlsx = () => act('xlsx', async () => {
    setErr(null)
    try {
      const r = await fetch(`/api/v1/analytics/${id}/xlsx${execQuery({ from: m!.from, to: m!.to, now: asOf, choice: q.choice, groupBy: q.groupBy, grouped: true, type: q.type })}`)
      if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
      download(name('xlsx'), await r.blob())
    } catch (e) { setErr(`XLSX export failed: ${(e as Error).message}`) }
  })
  const exports = m && m.recs.length > 0 && <span className="an-export" role="group" aria-label={`Export ${m.title}`}>
    <Button onClick={csv} aria-label={`Export ${m.title} as CSV`}>CSV</Button>
    <Button disabled={!!pending} onClick={() => void xlsx()} aria-label={`Export ${m.title} as XLSX`}>{pending ? 'XLSX…' : 'XLSX'}</Button>
  </span>
  return <div className="an-card" data-testid="exec-panel" data-metric={id}>
    <Panel title={m?.title ?? id} subtitle={`${sub} · ${id}`} meta={exports || undefined}>
      {entry?.error && <ErrorState what={id} message={entry.error} />}
      {m && m.recs.length === 0 && <p className="an-note" data-testid="chart-empty">No data in this window</p>}
      {m && table && m.recs.length > 0 && <ScrollRegion label={m.title}>
        <table className="os-table" data-testid="exec-table" aria-label={`${m.title}: data`}>
          <thead><tr>{table.headers.map((h, i) => <th key={h + i} scope="col" className={table.numeric[i] ? 'end' : undefined}>{h}</th>)}</tr></thead>
          <tbody>{table.rows.map((r, ri) => <tr key={ri}>{r.map((c, ci) => <td key={ci} className={table.numeric[ci] ? 'end mono' : undefined}>{c === '—' ? <><span aria-hidden="true">{c}</span><span className="sr-only">none</span></> : c}</td>)}</tr>)}</tbody>
        </table>
      </ScrollRegion>}
      {note && <p className="an-note">{note}</p>}
      {err && <div role="alert"><Notice tone="danger" title={err.split(': ')[0]}>{err.includes(': ') ? err.slice(err.indexOf(': ') + 2) : undefined}</Notice></div>}
    </Panel>
  </div>
}
