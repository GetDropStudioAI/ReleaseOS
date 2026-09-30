import { useEffect, useRef, useState, type RefObject } from 'react'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, ScatterChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { SVGRenderer } from 'echarts/renderers'
import { BUILDERS, readPalette } from './analyticsCharts.ts'
import type { Rec } from './analyticsFindings.ts'

// Selective imports only (CLAUDE.md): three chart types, three components, the SVG renderer. No canvas, no full bundle.
echarts.use([BarChart, LineChart, ScatterChart, GridComponent, LegendComponent, TooltipComponent, SVGRenderer])

/** Bumps whenever the theme can have changed: the data-theme attribute on <html> (user menu) or the OS scheme (prefers-color-scheme). */
function useThemeRev(): number {
  const [rev, setRev] = useState(0)
  useEffect(() => {
    const bump = () => setRev(r => r + 1)
    const mo = new MutationObserver(bump)
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    mq.addEventListener('change', bump)
    return () => { mo.disconnect(); mq.removeEventListener('change', bump) }
  }, [])
  return rev
}

/**
 * One ECharts chart on the SVG renderer, themed from the token custom properties. `svgRef.current()` returns the current drawing as an SVG string
 * (used by the SVG export). The wrapper is role="img" with the finding as its name; the numbers are in the "View data" table next to it.
 */
export default function AnalyticsChart({ id, recs, finding, height, svgRef }: { id: string; recs: Rec[]; finding: string; height: number; svgRef: RefObject<(() => string) | null> }) {
  const host = useRef<HTMLDivElement>(null)
  const chart = useRef<echarts.ECharts | null>(null)
  const themeRev = useThemeRev()

  useEffect(() => {
    const el = host.current!
    const c = echarts.init(el, null, { renderer: 'svg', height })
    chart.current = c
    svgRef.current = () => c.renderToSVGString({ useViewBox: false })
    const ro = new ResizeObserver(() => c.resize())
    ro.observe(el)
    return () => { ro.disconnect(); svgRef.current = null; c.dispose(); chart.current = null }
  }, [height, svgRef])

  // declared after the init effect, so on first mount the chart exists when this runs; on a later theme/data change it just redraws
  useEffect(() => {
    const build = BUILDERS[id]
    if (chart.current && build) chart.current.setOption(build(recs, readPalette()), true)
  }, [id, recs, themeRev, height])

  return <div ref={host} role="img" aria-label={finding} className="an-canvas" style={{ height }} />
}
