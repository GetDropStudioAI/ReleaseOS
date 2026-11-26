import { useCallback, useEffect, useState } from 'react'
import { get } from '../api'
import { onSyncChanged } from '../SyncBanner'
import { enabledKeys } from '../mgmt/modulesLogic.ts'
import { listModules, onModulesChanged } from '../mgmt/modulesApi.ts'
import { getApprovals } from './approvalsApi.ts'
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

/**
 * The add-on modules that are on in this workspace (REOS-174), for the navigation and the pages of a module. Null until read (the navigation shows no module entry meanwhile). Read again whenever
 * `refreshKey` changes (a live ModulesChanged push, a reconnect) and when this session's own Modules tab changes one.
 */
export function useEnabledModules(refreshKey: number) {
  const [keys, setKeys] = useState<ReadonlySet<string> | null>(null)
  const load = useCallback(() => { listModules().then(r => setKeys(enabledKeys(r))).catch((e: Error) => console.error('The modules could not be read', e)) }, [])
  useEffect(() => { load() }, [load, refreshKey])
  useEffect(() => onModulesChanged(load), [load])
  return keys
}
