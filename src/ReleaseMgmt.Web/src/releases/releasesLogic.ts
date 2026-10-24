import type { StreamRow } from '../api.ts'
import { typeLabel } from '../mgmt/releaseTypesLogic.ts'

/** The pure parts of the Releases table (REOS-125, D50): the filters, their address-bar form, the order, and the gate rollup. Tested without a browser. */

export const ALL = ''
/** The filter value for "this release has none recorded" (no team, no type, no environment). */
export const NONE = '~none'

/** Left to right as a release moves; Aborted last. A status the list does not know sorts after them rather than disappearing. */
export const STATUSES = ['Executing', 'Gated', 'Planning', 'Complete', 'Aborted']

export interface Filters { query: string; status: string; team: string; environment: string; type: string }
export const NO_FILTERS: Filters = { query: '', status: ALL, team: ALL, environment: ALL, type: ALL }
export const isFiltered = (f: Filters) => f.query.trim() !== '' || f.status !== ALL || f.team !== ALL || f.environment !== ALL || f.type !== ALL

const PARAMS: Record<keyof Filters, string> = { query: 'q', status: 'status', team: 'team', environment: 'env', type: 'type' }

/** The filters as a query string, so a refresh and the browser's Back button keep them. Empty filters leave no trace. */
export function filtersToSearch(f: Filters): string {
  const p = new URLSearchParams()
  for (const k of Object.keys(PARAMS) as (keyof Filters)[]) if (f[k].trim() !== '') p.set(PARAMS[k], f[k])
  const s = p.toString()
  return s ? `?${s}` : ''
}
export function filtersFromSearch(search: string): Filters {
  const p = new URLSearchParams(search)
  const get = (k: keyof Filters) => p.get(PARAMS[k]) ?? ''
  return { query: get('query'), status: get('status'), team: get('team'), environment: get('environment'), type: get('type') }
}

const matchesId = (value: string, ids: string[]) => (value === ALL ? true : value === NONE ? ids.length === 0 : ids.includes(value))

export function filterReleases(rows: StreamRow[], f: Filters): StreamRow[] {
  const q = f.query.trim().toLowerCase()
  return rows.filter(r =>
    (f.status === ALL || r.status === f.status)
    && matchesId(f.team, r.ownerTeam ? [r.ownerTeam.id] : [])
    && matchesId(f.type, r.type ? [r.type.id] : [])
    && matchesId(f.environment, r.environments.map(e => e.id))
    && (q === '' || [r.key ?? '', r.title, r.owner?.name ?? '', r.ownerTeam?.handle ?? '', r.ownerTeam?.name ?? '', (r.type ? typeLabel(r.type, r.betaNumber) : ''), ...r.environments.map(e => e.name)].some(t => t.toLowerCase().includes(q))))
}

/** Open work first (Executing, Gated, Planning), then what finished; inside a status the nearest target date first, then the key, so the order never jitters. */
export function sortReleases(rows: StreamRow[]): StreamRow[] {
  const rank = (s: string) => { const i = STATUSES.indexOf(s); return i < 0 ? STATUSES.length : i }
  return [...rows].sort((a, b) => rank(a.status) - rank(b.status) || a.targetReleaseDate.localeCompare(b.targetReleaseDate) || (a.key ?? a.title).localeCompare(b.key ?? b.title, undefined, { numeric: true }))
}

export interface Option { value: string; label: string }
/** The choices for one filter, from the releases listed: distinct by id, by label, with "Not set" last when some release has none. */
export function optionsOf(rows: StreamRow[], pick: (r: StreamRow) => { id: string; label: string }[], all: string, notSet: string): Option[] {
  const seen = new Map<string, string>()
  let none = false
  for (const r of rows) { const items = pick(r); if (items.length === 0) none = true; for (const i of items) seen.set(i.id, i.label) }
  const list = [...seen].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))
  return [{ value: ALL, label: all }, ...list, ...(none ? [{ value: NONE, label: notSet }] : [])]
}
export const teamLabel = (r: StreamRow) => (r.ownerTeam ? `@${r.ownerTeam.handle}` : null)

/** "3 of 5 passed" and what is holding the rest: Certified and Waived count as passed, as on the release page. */
export function gateRollup(gates: string[]): { passed: number; total: number; failed: number; words: string } {
  const passed = gates.filter(g => g === 'Certified' || g === 'Waived').length
  const failed = gates.filter(g => g === 'Failed').length
  return { passed, total: gates.length, failed, words: gates.length === 0 ? 'No gates' : `${passed} of ${gates.length} passed` }
}
