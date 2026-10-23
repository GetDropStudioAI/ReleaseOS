import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { setServerTime } from './time'
import { hubQuery } from './workspace'

export type LiveState = 'connecting' | 'live' | 'reconnecting' | 'offline'

/** Deployment pages (REOS-100) listen to the same connection: a DeploymentChanged push is a hint to refetch that deployment (payloads are never trusted). */
const deploymentListeners = new Set<(deploymentId: string) => void>()
export function onDeploymentChanged(fn: (deploymentId: string) => void): () => void {
  deploymentListeners.add(fn)
  return () => { deploymentListeners.delete(fn) }
}

interface Handlers {
  /** A train changed somewhere: refetch (payloads are hints, never trusted). */
  onTrainChanged: (trainId: string, version: number) => void
  onNotification?: (notificationId: string) => void
  /** A run's forecast changed (a step event): refetch the forecast. */
  onForecastChanged?: (trainId: string, runId: string) => void
  onSyncAlert?: (alertId: string) => void
  /** Fired after a reconnect: events may have been missed, so refetch everything on screen. */
  onResync: () => void
}

/** One SignalR connection per signed-in session; reconnects on its own and resyncs afterwards. */
export function useLive(enabled: boolean, handlers: Handlers) {
  const [state, setState] = useState<LiveState>('connecting')
  const [serverTime, setServerTimeState] = useState<string | null>(null)
  const h = useRef(handlers)
  h.current = handlers

  useEffect(() => {
    if (!enabled) return
    const hub = new HubConnectionBuilder()
      .withUrl(`/hub/trains${hubQuery()}`)   // REOS-106: the hub joins the workspace's group (a browser cannot set a header on a WebSocket)
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()
    hub.on('TrainChanged', (id: string, v: number) => h.current.onTrainChanged(id, v))
    hub.on('NotificationCreated', (id: string) => h.current.onNotification?.(id))
    hub.on('ServerTime', (t: string) => { setServerTime(t); setServerTimeState(t) })
    hub.on('ForecastChanged', (tid: string, rid: string) => h.current.onForecastChanged?.(tid, rid))
    hub.on('SyncAlertRaised', (id: string) => h.current.onSyncAlert?.(id))
    hub.on('DeploymentChanged', (id: string) => deploymentListeners.forEach(fn => fn(id)))
    hub.onreconnecting(() => setState('reconnecting'))
    hub.onreconnected(() => { setState('live'); h.current.onResync() })
    hub.onclose(() => setState('offline'))
    let stopped = false
    hub.start().then(() => { if (!stopped) setState('live') }).catch(() => { if (!stopped) setState('offline') })
    return () => { stopped = true; if (hub.state !== HubConnectionState.Disconnected) void hub.stop() }
  }, [enabled])

  return { state, serverTime }
}
