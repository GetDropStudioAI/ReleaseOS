import type { Tone } from '../ui/tone.ts'

/** GET /healthz (Health.cs): 200 Healthy or 503 Unhealthy, with the database and the two background loops. Anonymous, states and times only. */
export interface HostHealth { status: 'Healthy' | 'Unhealthy'; db: string; poller: { state: string }; watchdog: { state: string } }
/** The part of GET /sync/state the card needs (SyncBanner.tsx has the full shape). */
export interface SyncSummary { connectorWide: boolean; openAlerts: number; watchdog: { stalled: boolean } }
export type LiveState = 'connecting' | 'live' | 'reconnecting' | 'offline'
export interface ControlPlane { tone: Tone; label: string; detail: string | null }

/**
 * The sidebar's control-plane card (REOS-94: "reflects real health; define degraded and down states"). It reads the host check, the sync state and the
 * live connection, worst first:
 * - **Service disruption** (danger): the server does not answer, /healthz says Unhealthy (database, poller or watchdog), or live updates are offline.
 * - **Degraded** (warning): the host is healthy but sync alerts are open or a connector fails as a whole, or live updates are reconnecting.
 * - **All systems operational** (success): none of the above.
 * - **Checking status** (neutral) until the first answers arrive.
 */
export function controlPlane(host: HostHealth | 'unreachable' | null, sync: SyncSummary | null, live: LiveState): ControlPlane {
  if (host === 'unreachable') return { tone: 'danger', label: 'Service disruption', detail: 'The server is not answering' }
  if (host?.status === 'Unhealthy') {
    const parts = [host.db !== 'ok' && 'database check failed', host.poller.state === 'stalled' && 'sync poller stalled', host.watchdog.state === 'stalled' && 'sync watchdog stalled']
    return { tone: 'danger', label: 'Service disruption', detail: cap(parts.filter(Boolean).join(', ') || 'health check failed') }
  }
  if (live === 'offline') return { tone: 'danger', label: 'Service disruption', detail: 'Live updates are offline: reload to reconnect' }
  if (host === null || sync === null) return { tone: 'neutral', label: 'Checking status', detail: null }
  if (sync.watchdog.stalled) return { tone: 'warning', label: 'Degraded', detail: 'Sync has stalled' }
  if (sync.connectorWide) return { tone: 'warning', label: 'Degraded', detail: 'A connector is failing' }
  if (sync.openAlerts > 0) return { tone: 'warning', label: 'Degraded', detail: `${sync.openAlerts} sync alert${sync.openAlerts === 1 ? '' : 's'} open` }
  if (live === 'reconnecting') return { tone: 'warning', label: 'Degraded', detail: 'Live updates are reconnecting' }
  return { tone: 'success', label: 'All systems operational', detail: null }
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

/** "Release manager" for "ReleaseManager": the user card's role line. */
export function roleName(role: string | undefined): string {
  switch (role) {
    case 'ReleaseManager': return 'Release manager'
    case 'RTE': return 'Release train engineer'
    case 'GovernanceOfficer': return 'Governance officer'
    case 'Viewer': return 'Viewer'
    default: return role ?? ''
  }
}

/** "JL" for "Jordan Lee"; one letter for one name; from the email's local part when there is no name. */
export function initials(name: string): string {
  const words = name.replace(/@.*/, '').split(/[\s._-]+/).filter(Boolean)
  if (words.length === 0) return '?'
  return (words.length === 1 ? words[0].slice(0, 1) : words[0].slice(0, 1) + words[words.length - 1].slice(0, 1)).toUpperCase()
}
