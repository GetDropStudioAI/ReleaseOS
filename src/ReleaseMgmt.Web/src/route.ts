import { useCallback, useEffect, useState } from 'react'
import type { Selection } from './Planning'

/** The URL carries the train and the selected node, so a deep link or a refresh reopens the same Inspector (PROJECT_SCOPE 5.4). */
export type Mode = 'plan' | 'rehearsal' | 'live'
export interface Route { view: 'trains' | 'admin' | 'work' | 'inbox' | 'audit' | 'templates'; trainId: string | null; selection: Selection; mode: Mode }

export const ROOT: Route = { view: 'trains', trainId: null, selection: null, mode: 'plan' }

export function parsePath(path: string): Route {
  const seg = path.split('/').filter(Boolean).map(decodeURIComponent)
  if (seg[0] === 'admin' || seg[0] === 'work' || seg[0] === 'inbox' || seg[0] === 'audit' || seg[0] === 'templates') return { view: seg[0], trainId: null, selection: null, mode: 'plan' }
  if (seg[0] !== 'trains' || !seg[1]) return ROOT
  const trainId = seg[1]
  let mode: Mode = 'plan'
  if (seg[2] === 'live' || seg[2] === 'rehearsal') { mode = seg[2]; seg.splice(2, 1) }
  if (seg[2] === 'gates' && seg[3]) {
    if (seg[4] === 'tasks' && seg[5]) return { view: 'trains', trainId, mode, selection: { kind: 'task', gateId: seg[3], id: seg[5] } }
    return { view: 'trains', trainId, mode, selection: { kind: 'gate', id: seg[3] } }
  }
  if (seg[2] === 'steps' && seg[3]) return { view: 'trains', trainId, mode, selection: { kind: 'step', id: seg[3] } }
  if (seg[2] === 'products' && seg[3]) return { view: 'trains', trainId, mode, selection: { kind: 'product', id: seg[3] } }
  return { view: 'trains', trainId, selection: null, mode }
}

export function buildPath(r: Route): string {
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
  useEffect(() => {
    const onPop = () => setRoute(parsePath(window.location.pathname))
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])
  const go = useCallback((r: Route, replace = false) => {
    const path = buildPath(r)
    if (path !== window.location.pathname) window.history[replace ? 'replaceState' : 'pushState'](null, '', path)
    setRoute(r)
  }, [])
  return { route, go }
}
