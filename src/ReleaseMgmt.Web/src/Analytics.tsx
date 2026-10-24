import { lazy, Suspense } from 'react'
import type { Me } from './api'
import { LoadingState, PageHeading } from './ui'

// REOS-47: the screen (and ECharts with it) is a separate chunk, fetched when the route is first opened.
const AnalyticsScreen = lazy(() => import('./AnalyticsScreen'))

export function Analytics(props: { me: Me }) {
  return <Suspense fallback={<div className="os-page"><PageHeading trail={['Workspace', 'Analytics']} title="Analytics" /><LoadingState what="analytics" /></div>}><AnalyticsScreen {...props} /></Suspense>
}
