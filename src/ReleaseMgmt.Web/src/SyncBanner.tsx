import { useCallback, useEffect, useState } from 'react'
import { get } from './api'
import { fmtDayTime } from './time'

/**
 * REOS-42: the connector-wide failure banner and the sync state it reads (GET /api/v1/sync/state).
 * The banner is one full-width text line under the toolbar on EVERY screen while `connectorWide` is true: coloured words with a glyph and a text link
 * to Sync health, never a box or a pill. It refetches on SyncAlertRaised (raised, repeated or resolved), on reconnect, and once a minute as a backstop.
 */
export interface FailingEntry { scope: 'connector' | 'engine'; source: string; kind: string; reasons: string[]; message: string | null; since: string | null; alertId: string | null; links: number }
export interface LinkCounts { total: number; inSync: number; mismatch: number; broken: number; unsynced: number; stale: number }
export interface ConnectorView {
  source: string; baseUrl: string; isEnabled: boolean; lastCycleStartedAt: string | null; lastCycleCompletedAt: string | null; lastSuccessAt: string | null
  consecutiveFailures: number; version: number; links: LinkCounts; openAlerts: number; state: 'Healthy' | 'Degraded' | 'Failing' | 'NotRun' | 'Disabled'
}
export interface SyncStateDto {
  connectorWide: boolean; failing: FailingEntry[]; connectors: ConnectorView[]; openAlerts: number
  watchdog: { stalled: boolean; alertId: string | null; lastCycleCompletedAt: string | null }; asOf: string
}

/** Something about sync changed (a push arrived, or an alert was resolved here): every subscriber refetches. */
const EVENT = 'reos:sync-changed'
export const notifySyncChanged = () => window.dispatchEvent(new Event(EVENT))
export const onSyncChanged = (fn: () => void) => { window.addEventListener(EVENT, fn); return () => window.removeEventListener(EVENT, fn) }

export const SYSTEM_NAMES: Record<string, string> = { Jira: 'Jira', ServiceNow: 'ServiceNow', SyncEngine: 'The sync engine' }

export function useSyncState(enabled = true) {
  const [state, setState] = useState<SyncStateDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    get<SyncStateDto>('/api/v1/sync/state')
      .then(s => { setState(s); setError(null) })
      .catch((e: Error) => { console.error('Sync state could not be read', e); setError(e.message) })   // shown by the banner: a hidden failure would hide a hidden outage
  }, [])
  useEffect(() => {
    if (!enabled) return
    load()
    const off = onSyncChanged(load)
    const timer = window.setInterval(load, 60_000)
    return () => { off(); window.clearInterval(timer) }
  }, [enabled, load])
  return { state, error }
}

function headline(e: FailingEntry, c: ConnectorView | undefined): string {
  const name = SYSTEM_NAMES[e.source] ?? e.source
  if (e.scope === 'engine') return 'The sync engine has stalled: no cycle has completed for too long'
  if (e.kind === 'AuthFailed') return `${name} rejected our credentials`
  if (e.kind === 'Unreachable') return `${name} is unreachable`
  return `${name} has failed ${c?.consecutiveFailures ?? 'several'} cycles in a row`
}

function effect(e: FailingEntry): string {
  if (e.scope === 'engine') return 'Nothing from Jira or ServiceNow on any screen is current.'
  const name = SYSTEM_NAMES[e.source] ?? e.source
  return e.links > 0 ? `${e.links} link${e.links === 1 ? '' : 's'} to ${name} ${e.links === 1 ? 'is' : 'are'} not current; nothing from ${name} on any screen is current.` : `Nothing from ${name} on any screen is current.`
}

export function SyncBanner({ sync, onOpen }: { sync: { state: SyncStateDto | null; error: string | null }; onOpen: () => void }) {
  const { state, error } = sync
  const failing = state?.connectorWide ? state.failing : []
  // The row is always in the DOM (the shell grid places the other regions around it) and has no height while there is nothing to say;
  // only the messages are alerts, so a page with nothing wrong has no empty alert region.
  return (
    <div className="sync-banner-row" data-testid="sync-banner">
      {failing.map(e => (
        <p key={`${e.source}-${e.kind}`} className="sync-banner bad" role="alert">
          <strong>{e.scope === 'engine' ? '▲' : '✗'} {headline(e, state?.connectors.find(c => c.source === e.source))}{e.since ? ` since ${fmtDayTime(e.since)}` : ''}.</strong>
          <span className="sync-banner-effect">{effect(e)}</span>
          <a href="/sync" onClick={ev => { ev.preventDefault(); onOpen() }}>Sync health</a>
        </p>
      ))}
      {error && <p className="sync-banner warn" role="alert">▲ The sync status could not be read ({error}). A connector failure would not show here until it can.</p>}
    </div>
  )
}
