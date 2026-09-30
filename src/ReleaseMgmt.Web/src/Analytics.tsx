import { lazy, Suspense } from 'react'
import type { Me } from './api'

// REOS-47: the screen (and ECharts with it) is a separate chunk, fetched when the route is first opened.
const AnalyticsScreen = lazy(() => import('./AnalyticsScreen'))

export function Analytics(props: { me: Me }) {
  return <Suspense fallback={<><h1>Analytics</h1><p className="muted">Loading…</p></>}><AnalyticsScreen {...props} /></Suspense>
}
