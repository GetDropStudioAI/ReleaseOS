/**
 * Client-side exports for the Analytics screen (REOS-47, Q-047c): RFC 4180 CSV with OWASP CSV-injection escaping, SVG with the finding as <title>,
 * and the browser download. Pure functions are unit-tested (tests-unit/); the DOM part is only `download`. XLSX comes from the server
 * (GET /api/v1/analytics/{m}/xlsx) because a workbook is a zip and a dependency-free writer is not trivial.
 */

/** OWASP: a text cell that a spreadsheet could read as a formula (= + - @, TAB, CR) is prefixed with an apostrophe. Numbers are never touched (a real -3 stays -3). */
export function csvCell(v: unknown): string {
  if (v === null || v === undefined) return ''
  let s: string
  if (typeof v === 'number') s = Number.isFinite(v) ? String(v) : ''
  else if (typeof v === 'boolean') s = v ? 'true' : 'false'
  else {
    s = String(v)
    if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`
  }
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

/** Header row then data rows, CRLF line ends, no trailing newline issue (one CRLF at the end). `bom` prepends U+FEFF so Excel reads UTF-8. */
export function toCsv(rows: unknown[][], columns: string[], opts: { bom?: boolean } = {}): string {
  const lines = [columns.map(csvCell).join(','), ...rows.map(r => columns.map((_, i) => csvCell(r[i])).join(','))]
  return (opts.bom ? '﻿' : '') + lines.join('\r\n') + '\r\n'
}

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
/** Adds <title> (the finding) and <desc> as the first children of the root <svg>, so the file names itself when opened in a viewer or read by a screen reader. */
export function svgWithTitle(svg: string, title: string, desc = ''): string {
  return svg.replace(/<svg\b([^>]*)>/, (_m, attrs: string) => `<svg${attrs} role="img"><title>${esc(title)}</title>${desc ? `<desc>${esc(desc)}</desc>` : ''}`)
}

/** File name: analytics-m2-2026-04-01_2026-09-28.csv (as-of snapshots carry no window). */
export function exportName(id: string, from: string, to: string, ext: 'csv' | 'xlsx' | 'svg', asOfSnapshot = false): string {
  const safe = (s: string) => s.replace(/[^0-9A-Za-z-]/g, '')
  return `analytics-${id.toLowerCase()}${asOfSnapshot ? `-asof-${safe(to)}` : `-${safe(from)}_${safe(to)}`}.${ext}`
}

/** The one-workbook file name (REOS-141): analytics-workbook-2026-04-01_2026-09-28.xlsx. It covers all 15 metrics, so there is no as-of variant. */
export function workbookName(from: string, to: string): string {
  const safe = (s: string) => s.replace(/[^0-9A-Za-z-]/g, '')
  return `analytics-workbook-${safe(from)}_${safe(to)}.xlsx`
}

/** The workbook request: the window and as-of of the figures on screen, so the file holds the numbers the person is looking at. */
export function workbookUrl(from: string, to: string, asOf: string, type = ''): string {
  return `/api/v1/analytics/xlsx?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&now=${encodeURIComponent(asOf)}${typeParam(type)}`
}

/** REOS-116: the release type the figures are limited to, as the query parameter every analytics endpoint takes; nothing for all types. */
export const typeParam = (type: string): string => (type ? `&releaseTypeId=${encodeURIComponent(type)}` : '')

/** The query string of the metric requests: the period when both ends are set, and the release type when one is chosen. */
export function analyticsQuery(from: string, to: string, type = ''): string {
  const q = `${from && to ? `from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}` : ''}${typeParam(type)}`.replace(/^&/, '')
  return q ? `?${q}` : ''
}

/** Browser download of a blob through a temporary <a download>. */
export function download(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url; a.download = name; a.rel = 'noopener'
  document.body.appendChild(a); a.click(); a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/** A quick period (REOS-132): the `days` before the as-of UTC day, up to that day; only fills the two dates, the query is the same as typed dates. */
export function presetRange(asOf: string, days: number): { from: string; to: string } {
  const end = new Date(`${asOf.slice(0, 10)}T00:00:00Z`)
  const start = new Date(end.getTime() - days * 86_400_000)
  return { from: start.toISOString().slice(0, 10), to: end.toISOString().slice(0, 10) }
}
