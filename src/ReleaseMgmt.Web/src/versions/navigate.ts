import { ROOT, buildPath } from '../route.ts'

/** Opens a release the way the Calendar does when the app has not wired a handler: the URL changes and the router follows. */
export function openRelease(trainId: string) {
  window.history.pushState(null, '', buildPath({ ...ROOT, view: 'trains', trainId }))
  window.dispatchEvent(new PopStateEvent('popstate'))
}
