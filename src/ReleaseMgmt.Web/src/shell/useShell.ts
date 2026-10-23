import { useCallback, useEffect, useState } from 'react'
import { get } from '../api'
import { onSyncChanged } from '../SyncBanner'
import { getApprovals } from './approvals.ts'
import type { HostHealth } from './health.ts'
import type { WorkspaceInfo } from './Sidebar'

/** The current workspace for the sidebar and the breadcrumb (GET /workspace), read once. */
export function useWorkspace() {
  const [w, setW] = useState<WorkspaceInfo | null>(null)
  useEffect(() => { get<WorkspaceInfo>('/api/v1/workspace').then(setW).catch((e: Error) => console.error('The workspace could not be read', e)) }, [])
  return w
}

/** The Approvals badge: the total of GET /approvals, read again whenever `refreshKey` changes (a live event or a reconnect). */
export function useApprovalCount(refreshKey: number) {
  const [n, setN] = useState(0)
  useEffect(() => { getApprovals().then(q => setN(q.counts.total)).catch((e: Error) => console.error('The approval count could not be read', e)) }, [refreshKey])
  return n
}

/** GET /healthz for the control-plane card: every minute, when sync changes, and when `refreshKey` changes. 'unreachable' when there is no answer. */
export function useHostHealth(refreshKey: number) {
  const [h, setH] = useState<HostHealth | 'unreachable' | null>(null)
  const load = useCallback(() => {
    fetch('/healthz', { cache: 'no-store' })
      .then(r => r.json() as Promise<HostHealth>).then(setH)
      .catch(() => setH('unreachable'))
  }, [])
  useEffect(() => { load() }, [load, refreshKey])
  useEffect(() => { const t = window.setInterval(load, 60_000); const off = onSyncChanged(load); return () => { window.clearInterval(t); off() } }, [load])
  return h
}
