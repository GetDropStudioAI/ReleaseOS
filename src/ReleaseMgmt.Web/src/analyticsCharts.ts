import type { EChartsCoreOption } from 'echarts/core'
import { aggregateSteps, fmt, lateShare, monthShort, type Rec } from './analyticsFindings.ts'

/** Colours are read from the CSS custom properties (docs/ui/tokens.css) every time a chart is drawn, so a light/dark switch just redraws. */
export interface Palette { bg: string; label: string; secondary: string; tertiary: string; grid: string; rule: string; accent: string; ok: string; warn: string; bad: string; sans: string; mono: string }
const TOKENS: [keyof Palette, string][] = [['bg', '--bg'], ['label', '--label'], ['secondary', '--secondary'], ['tertiary', '--tertiary'], ['grid', '--grid'], ['rule', '--separator-strong'],
  ['accent', '--accent'], ['ok', '--ok'], ['warn', '--warn'], ['bad', '--bad'], ['sans', '--font-sans'], ['mono', '--font-mono']]
export function readPalette(el: Element = document.documentElement): Palette {
  const cs = getComputedStyle(el)
  const p = {} as Palette
  for (const [k, v] of TOKENS) p[k] = cs.getPropertyValue(v).trim()
  return p
}

const n = (v: unknown) => (typeof v === 'number' ? v : null)
const s = (v: unknown) => String(v ?? '')

function base(p: Palette, extra: EChartsCoreOption = {}): EChartsCoreOption {
  return {
    animation: false, backgroundColor: p.bg, textStyle: { fontFamily: p.sans, color: p.secondary, fontSize: 11 },
    tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, backgroundColor: p.bg, borderColor: p.rule, textStyle: { color: p.label, fontFamily: p.sans, fontSize: 12 }, confine: true },
    ...extra,
  }
}
const valueAxis = (p: Palette, name: string, extra: Record<string, unknown> = {}) => ({
  type: 'value', name, nameTextStyle: { color: p.secondary, fontSize: 11, align: 'left' }, axisLabel: { color: p.secondary, fontFamily: p.mono, fontSize: 10.5 },
  splitLine: { lineStyle: { color: p.grid } }, axisLine: { show: false }, ...extra,
})
const catAxis = (p: Palette, data: string[], extra: Record<string, unknown> = {}) => ({
  type: 'category', data, axisLine: { lineStyle: { color: p.label } }, axisTick: { show: false }, axisLabel: { color: p.secondary, fontSize: 10.5, ...((extra.axisLabel as object) ?? {}) }, ...extra,
})
const legend = (p: Palette) => ({ top: 0, right: 0, itemWidth: 10, itemHeight: 10, textStyle: { color: p.secondary, fontSize: 11, fontFamily: p.sans } })
const barLabel = (p: Palette, formatter: string | ((x: { value: unknown; dataIndex: number }) => string), position: string = 'top') => ({ show: true, position, color: p.label, fontFamily: p.mono, fontSize: 10.5, formatter })

/** Shared prefix ("R26.") stripped from train titles so the x labels stay short; the full title is in the tooltip and the data table. */
export function shortTitles(titles: string[]): string[] {
  if (titles.length < 2) return titles
  const cut = titles[0].lastIndexOf('.') + 1
  return cut > 0 && titles.every(t => t.slice(0, cut) === titles[0].slice(0, cut)) ? titles.map(t => t.slice(cut)) : titles
}

export type OptionBuilder = (recs: Rec[], p: Palette) => EChartsCoreOption

export const BUILDERS: Record<string, OptionBuilder> = {
  // M2 slip per train: late = bar in warn, on time = dot in ok
  M2: (recs, p) => {
    const titles = recs.map(r => s(r.Title))
    const slip = recs.map(r => n(r.slip_days) ?? 0)
    return base(p, {
      legend: { ...legend(p), data: ['On time', 'Late'] },
      grid: { left: 40, right: 8, top: 28, bottom: 42 },
      xAxis: catAxis(p, shortTitles(titles), { name: 'Train, in release order', nameLocation: 'middle', nameGap: 26, nameTextStyle: { color: p.secondary, fontSize: 11 }, axisLabel: { interval: 'auto' } }),
      yAxis: valueAxis(p, 'days', { minInterval: 1, min: 0 }),
      tooltip: { ...(base(p).tooltip as object), formatter: (a: unknown) => { const i = (Array.isArray(a) ? a[0] : a as { dataIndex: number }).dataIndex; return `${titles[i]}<br/>slip ${fmt(slip[i])} d` } },
      series: [
        { name: 'On time', type: 'scatter', symbolSize: 7, itemStyle: { color: p.ok }, data: slip.map(v => (v <= 0 ? 0 : null)) },
        { name: 'Late', type: 'bar', barMaxWidth: 16, itemStyle: { color: p.warn }, label: barLabel(p, (x: { value: unknown }) => `+${fmt(Number(x.value))}`), data: slip.map(v => (v > 0 ? v : null)) },
      ],
    })
  },
  // M3 gate cycle time: median and p90 per gate, days
  M3: (recs, p) => {
    const rows = [...recs].sort((a, b) => (n(b.median_h) ?? 0) - (n(a.median_h) ?? 0))
    const d = (v: unknown) => (n(v) === null ? null : Number(((v as number) / 24).toFixed(2)))
    return base(p, {
      legend: { ...legend(p), data: ['Median', 'p90'] },
      grid: { left: 8, right: 44, top: 28, bottom: 8, containLabel: true },
      xAxis: valueAxis(p, 'days', { min: 0 }),
      yAxis: catAxis(p, rows.map(r => s(r.GateName)), { inverse: true, axisLabel: { color: p.label, fontSize: 12 } }),
      series: [
        { name: 'Median', type: 'bar', barMaxWidth: 12, itemStyle: { color: p.accent }, label: barLabel(p, (x: { value: unknown }) => fmt(Number(x.value)), 'right'), data: rows.map(r => d(r.median_h)) },
        { name: 'p90', type: 'bar', barMaxWidth: 12, itemStyle: { color: p.tertiary }, label: barLabel(p, (x: { value: unknown }) => fmt(Number(x.value)), 'right'), data: rows.map(r => d(r.p90_h)) },
      ],
    })
  },
  // M4 first-pass % per gate
  M4: (recs, p) => base(p, {
    grid: { left: 8, right: 44, top: 8, bottom: 8, containLabel: true },
    xAxis: valueAxis(p, '', { min: 0, max: 100, axisLabel: { color: p.secondary, fontFamily: p.mono, fontSize: 10.5, formatter: '{value}%' } }),
    yAxis: catAxis(p, recs.map(r => s(r.GateName)), { inverse: true, axisLabel: { color: p.label, fontSize: 12 } }),
    series: [{ name: 'First-pass rate', type: 'bar', barMaxWidth: 14, itemStyle: { color: p.accent }, label: barLabel(p, (x: { value: unknown }) => `${fmt(Number(x.value))}%`, 'right'), data: recs.map(r => n(r.first_pass_pct)) }],
  }),
  // M5 late certification share per gate; the worst gate in warn
  M5: (recs, p) => {
    const rows = [...recs].sort((a, b) => (lateShare(b) ?? 0) - (lateShare(a) ?? 0))
    return base(p, {
      grid: { left: 8, right: 70, top: 8, bottom: 8, containLabel: true },
      xAxis: valueAxis(p, '', { min: 0, axisLabel: { color: p.secondary, fontFamily: p.mono, fontSize: 10.5, formatter: '{value}%' } }),
      yAxis: catAxis(p, rows.map(r => s(r.GateName)), { inverse: true, axisLabel: { color: p.label, fontSize: 12 } }),
      series: [{ name: 'Certified late', type: 'bar', barMaxWidth: 14, label: barLabel(p, (x) => { const r = rows[x.dataIndex]; return `${n(r.late) ?? 0} of ${n(r.gates) ?? 0}` }, 'right'),
        data: rows.map((r, i) => ({ value: Number(((lateShare(r) ?? 0) * 100).toFixed(1)), itemStyle: { color: i === 0 && (n(r.late) ?? 0) > 0 ? p.warn : p.tertiary } })) }],
    })
  },
  // M6 scope churn per train: added up, removed down
  M6: (recs, p) => {
    const titles = recs.map(r => s(r.Title))
    return base(p, {
      legend: { ...legend(p), data: ['Added', 'Removed'] },
      grid: { left: 40, right: 8, top: 28, bottom: 42 },
      xAxis: catAxis(p, shortTitles(titles), { name: 'Train, in release order', nameLocation: 'middle', nameGap: 26, nameTextStyle: { color: p.secondary, fontSize: 11 }, axisLabel: { interval: 'auto' } }),
      yAxis: valueAxis(p, 'products after freeze', { minInterval: 1 }),
      tooltip: { ...(base(p).tooltip as object), formatter: (a: unknown) => { const i = (Array.isArray(a) ? a[0] : a as { dataIndex: number }).dataIndex; const r = recs[i]; return `${titles[i]}<br/>${n(r.at_freeze)} at freeze, +${n(r.added)} added, -${n(r.removed)} removed` } },
      series: [
        { name: 'Added', type: 'bar', stack: 'churn', barMaxWidth: 16, itemStyle: { color: p.accent }, data: recs.map(r => n(r.added)) },
        { name: 'Removed', type: 'bar', stack: 'churn', barMaxWidth: 16, itemStyle: { color: p.warn }, data: recs.map(r => -(n(r.removed) ?? 0)) },
      ],
    })
  },
  // M7 per-step overrun: bar = average, dash = worst run
  M7: (recs, p) => {
    const agg = aggregateSteps(recs)
    const top = agg.reduce((a, b) => (b.avg > a.avg ? b : a), agg[0])
    return base(p, {
      legend: { ...legend(p), data: ['Average', 'Worst run'] },
      grid: { left: 40, right: 8, top: 28, bottom: 28 },
      xAxis: catAxis(p, agg.map(a => a.step), { axisLabel: { color: p.secondary, fontFamily: p.mono, fontSize: 10.5, interval: 'auto' } }),
      yAxis: valueAxis(p, 'min over plan'),
      series: [
        { name: 'Average', type: 'bar', barMaxWidth: 26, label: barLabel(p, (x: { value: unknown }) => fmt(Number(x.value))),
          data: agg.map(a => ({ value: Number(a.avg.toFixed(2)), itemStyle: { color: a === top && a.avg > 0 ? p.accent : p.tertiary } })) },
        { name: 'Worst run', type: 'scatter', symbol: 'rect', symbolSize: [30, 3], itemStyle: { color: p.label }, data: agg.map(a => Number(a.worst.toFixed(2))) },
      ],
    })
  },
  // M8 total overrun per live run
  M8: (recs, p) => {
    const titles = recs.map(r => s(r.Title))
    const top = recs.reduce((a, b) => ((n(b.total_overrun_min) ?? 0) > (n(a.total_overrun_min) ?? 0) ? b : a), recs[0])
    return base(p, {
      grid: { left: 40, right: 8, top: 28, bottom: 42 },
      xAxis: catAxis(p, shortTitles(titles), { name: 'Train, in release order', nameLocation: 'middle', nameGap: 26, nameTextStyle: { color: p.secondary, fontSize: 11 }, axisLabel: { interval: 'auto' } }),
      yAxis: valueAxis(p, 'min over plan', { min: 0 }),
      tooltip: { ...(base(p).tooltip as object), formatter: (a: unknown) => { const i = (Array.isArray(a) ? a[0] : a as { dataIndex: number }).dataIndex; const r = recs[i]; return `${titles[i]}<br/>${fmt(n(r.total_overrun_min) ?? 0)} min in total over ${n(r.steps)} steps<br/>worst ${s(r.worst_step)} ${fmt(n(r.worst_overrun_min) ?? 0)} min` } },
      series: [{ name: 'Total overrun', type: 'bar', barMaxWidth: 16, data: recs.map(r => ({ value: n(r.total_overrun_min), itemStyle: { color: r === top && (n(r.total_overrun_min) ?? 0) > 0 ? p.accent : p.tertiary } })) }],
    })
  },
  // M10 outcomes: trains by close code
  M10: (recs, p) => {
    const r = recs[0] ?? {}
    const rows: [string, number, string][] = [['Successful', n(r.successful) ?? 0, p.ok], ['With issues', n(r.with_issues) ?? 0, p.warn], ['Unsuccessful', n(r.unsuccessful) ?? 0, p.bad]]
    return base(p, {
      grid: { left: 8, right: 36, top: 8, bottom: 8, containLabel: true },
      xAxis: valueAxis(p, 'trains', { min: 0, minInterval: 1 }),
      yAxis: catAxis(p, rows.map(x => x[0]), { inverse: true, axisLabel: { color: p.label, fontSize: 12 } }),
      series: [{ name: 'Trains', type: 'bar', barMaxWidth: 16, label: barLabel(p, (x: { value: unknown }) => String(x.value), 'right'), data: rows.map(x => ({ value: x[1], itemStyle: { color: x[2] } })) }],
    })
  },
  // M13 waived share of Compliance gates per month
  M13: (recs, p) => base(p, {
    grid: { left: 40, right: 8, top: 28, bottom: 28 },
    xAxis: catAxis(p, recs.map(r => monthShort(s(r.month)))),
    yAxis: valueAxis(p, 'waived', { min: 0, axisLabel: { color: p.secondary, fontFamily: p.mono, fontSize: 10.5, formatter: '{value}%' } }),
    tooltip: { ...(base(p).tooltip as object), formatter: (a: unknown) => { const i = (Array.isArray(a) ? a[0] : a as { dataIndex: number }).dataIndex; const r = recs[i]; return `${s(r.month)}<br/>${n(r.waived)} of ${n(r.compliance_gates)} Compliance gates waived` } },
    series: [{ name: 'Waived', type: 'bar', barMaxWidth: 34, itemStyle: { color: p.warn }, label: barLabel(p, (x: { value: unknown }) => `${fmt(Number(x.value))}%`), data: recs.map(r => n(r.waived_pct)) }],
  }),
  // M15 throughput bars plus median lead time line (right axis)
  M15: (recs, p) => {
    const rows = recs.filter(r => r.month != null)
    return base(p, {
      legend: { ...legend(p), data: ['Trains completed', 'Median lead time'] },
      grid: { left: 40, right: 44, top: 28, bottom: 28 },
      xAxis: catAxis(p, rows.map(r => monthShort(s(r.month)))),
      yAxis: [valueAxis(p, 'trains', { min: 0, minInterval: 1 }), valueAxis(p, 'days', { min: 0, position: 'right', splitLine: { show: false }, nameTextStyle: { color: p.secondary, fontSize: 11, align: 'right' } })],
      series: [
        { name: 'Trains completed', type: 'bar', barMaxWidth: 34, itemStyle: { color: p.tertiary }, label: barLabel(p, (x: { value: unknown }) => String(x.value)), data: rows.map(r => n(r.completed)) },
        { name: 'Median lead time', type: 'line', yAxisIndex: 1, symbolSize: 6, lineStyle: { color: p.accent, width: 2 }, itemStyle: { color: p.accent }, data: rows.map(r => n(r.median_lead_days)) },
      ],
    })
  },
}
