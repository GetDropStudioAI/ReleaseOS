import { useCallback, useEffect, useState } from 'react'
import type { Selection } from './Planning'

/** The URL carries the train and the selected node, so a deep link or a refresh reopens the same Inspector (PROJECT_SCOPE 5.4). */
export type Mode = 'plan' | 'rehearsal' | 'live'
export type View = 'trains' | 'admin' | 'work' | 'inbox' | 'audit' | 'templates' | 'sync' | 'connectors' | 'library' | 'analytics' | 'calendar' | 'importexport' | 'gallery'
  | 'overview' | 'orchestration' | 'approvals' | 'deployments' | 'services' | 'environments'   // REOS-94: the redesign's destinations (shell/nav.ts)
export interface Route { view: View; trainId: string | null; selection: Selection; mode: Mode }

export const ROOT: Route = { view: 'trains', trainId: null, selection: null, mode: 'plan' }

/** Every view but the train workspace is one path segment: /calendar, /approvals, ... */
const PAGES = new Set<View>(['admin', 'work', 'inbox', 'audit', 'templates', 'sync', 'connectors', 'library', 'analytics', 'calendar', 'importexport', 'gallery',
  'overview', 'approvals', 'deployments', 'services', 'environments'])

/**
 * Rehearsal and Live runs belong to Orchestration (REOS-128): `/orchestration/{trainId}/{live|rehearsal}[/steps/{stepId}]`. A route that asks for a run mode of a
 * release (an old link, a mode switch on the release page, My work) is turned into that route; the selection is kept only when it is a step.
 */
export function normalize(r: Route): Route {
  if (r.view !== 'trains' || !r.trainId || !r.mode || r.mode === 'plan') return r
  return { view: 'orchestration', trainId: r.trainId, mode: r.mode, selection: r.selection?.kind === 'step' ? r.selection : null }
}

export function parsePath(path: string): Route {
  const seg = path.split('/').filter(Boolean).map(decodeURIComponent)
  if (seg[0] === 'orchestration') {
    const mode: Mode = seg[2] === 'live' || seg[2] === 'rehearsal' ? seg[2] : 'plan'
    return { view: 'orchestration', trainId: seg[1] ?? null, mode, selection: mode !== 'plan' && seg[3] === 'steps' && seg[4] ? { kind: 'step', id: seg[4] } : null }
  }
  if (PAGES.has(seg[0] as View)) return { view: seg[0] as View, trainId: null, selection: null, mode: 'plan' }
  if (seg[0] !== 'trains' || !seg[1]) return ROOT
  const trainId = seg[1]
  let mode: Mode = 'plan'
  if (seg[2] === 'live' || seg[2] === 'rehearsal') { mode = seg[2]; seg.splice(2, 1) }
  if (seg[2] === 'gates' && seg[3]) {
    if (seg[4] === 'tasks' && seg[5]) return { view: 'trains', trainId, mode, selection: { kind: 'task', gateId: seg[3], id: seg[5] } }
    return { view: 'trains', trainId, mode, selection: { kind: 'gate', id: seg[3] } }
  }
  if (seg[2] === 'steps' && seg[3]) return normalize({ view: 'trains', trainId, mode, selection: { kind: 'step', id: seg[3] } })
  if (seg[2] === 'products' && seg[3]) return { view: 'trains', trainId, mode, selection: { kind: 'product', id: seg[3] } }
  return normalize({ view: 'trains', trainId, selection: null, mode })
}

export function buildPath(r: Route): string {
  if (r.view === 'orchestration') {
    if (!r.trainId) return '/orchestration'
    const e = encodeURIComponent, base = `/orchestration/${e(r.trainId)}${r.mode === 'live' || r.mode === 'rehearsal' ? '/' + r.mode : ''}`
    return r.selection?.kind === 'step' && r.mode !== 'plan' ? `${base}/steps/${e(r.selection.id)}` : base
  }
  if (r.view !== 'trains') return `/${r.view}`
  if (!r.trainId) return '/'
  const e = encodeURIComponent, base = `/trains/${e(r.trainId)}${!r.mode || r.mode === 'plan' ? '' : '/' + r.mode}`
  const s = r.selection
  if (!s) return base
  if (s.kind === 'gate') return `${base}/gates/${e(s.id)}`
  if (s.kind === 'task') return `${base}/gates/${e(s.gateId)}/tasks/${e(s.id)}`
  if (s.kind === 'step') return `${base}/steps/${e(s.id)}`
  return `${base}/products/${e(s.id)}`
}

export function useRoute() {
  const [route, setRoute] = useState<Route>(() => parsePath(window.location.pathname))
  // A link that names a run mode of a release under /trains (an old bookmark) is shown at its Orchestration address.
  useEffect(() => { const path = buildPath(parsePath(window.location.pathname)); if (path !== window.location.pathname && window.location.pathname.startsWith('/trains/')) window.history.replaceState(null, '', path) }, [])
  useEffect(() => {
    const onPop = () => setRoute(parsePath(window.location.pathname))
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])
  const go = useCallback((requested: Route, replace = false) => {
    const r = normalize(requested)
    const path = buildPath(r)
    if (path !== window.location.pathname) window.history[replace ? 'replaceState' : 'pushState'](null, '', path)
    setRoute(r)
  }, [])
  return { route, go }
}
