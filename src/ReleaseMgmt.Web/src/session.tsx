import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ApiError, get } from './api'
import type { Route } from './route'

/**
 * Session engine (PROJECT_SCOPE 5.4): UI state and unsent drafts are saved per (user, tab) to the server, so closing a tab
 * mid-draft loses nothing. clientId lives in sessionStorage (one per tab); saves are debounced 1 s and flushed when the tab is hidden.
 */
export const SCHEMA_VERSION = 1
const CLIENT_KEY = 'reos.clientId'
const DEBOUNCE_MS = 1000

interface Ui { layout: { filter?: string }; drafts: Record<string, unknown>; route?: Route }
const empty = (): Ui => ({ layout: {}, drafts: {} })

function clientId(): string {
  try {
    let id = window.sessionStorage.getItem(CLIENT_KEY)
    if (!id) { id = crypto.randomUUID(); window.sessionStorage.setItem(CLIENT_KEY, id) }
    return id
  } catch { return (memoryId ??= crypto.randomUUID()) }   // storage blocked: still works for this page load, just cannot survive a reload
}
let memoryId: string | undefined

/** An unknown SchemaVersion resets the layout but keeps the drafts as raw text, so nobody loses what they typed (5.4). */
export function migrate(version: number, ui: unknown): Ui {
  const drafts = (ui && typeof ui === 'object' ? (ui as { drafts?: unknown }).drafts : undefined)
  const d = drafts && typeof drafts === 'object' ? drafts as Record<string, unknown> : {}
  if (version === SCHEMA_VERSION) return { layout: (ui as Ui).layout ?? {}, drafts: d, route: (ui as Ui).route }
  return { layout: {}, drafts: Object.fromEntries(Object.entries(d).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)])) }
}

interface Ctx {
  ready: boolean
  /** Route saved by this or the most recent tab: used once, to restore when the URL itself names nothing. */
  restoredRoute: Route | undefined
  filter: string
  setFilter: (f: string) => void
  draft: <T,>(key: string) => T | undefined
  setDraft: (key: string, value: unknown) => void
  saveRoute: (r: Route) => void
  saveError: string | null
}
const SessionCtx = createContext<Ctx | null>(null)
export const useSession = () => { const c = useContext(SessionCtx); if (!c) throw new Error('useSession outside SessionProvider'); return c }

/** [value, set] for one named draft; set(undefined) discards it. */
export function useDraft<T>(key: string): [T | undefined, (v: T | undefined) => void] {
  const s = useSession()
  return [s.draft<T>(key), (v: T | undefined) => s.setDraft(key, v)]
}

export function SessionProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  const id = useMemo(clientId, [])
  const ui = useRef<Ui>(empty())
  const activeTrainId = useRef<string | null>(null)
  const dirty = useRef(false)
  const timer = useRef<number | undefined>(undefined)
  const [ready, setReady] = useState(false)
  const [, bump] = useState(0)
  const [restoredRoute, setRestored] = useState<Route | undefined>()
  const [saveError, setSaveError] = useState<string | null>(null)

  useEffect(() => {
    if (!enabled) { setReady(false); return }
    let live = true
    get<{ schemaVersion: number; activeTrainId: string | null; ui: unknown }>(`/api/v1/me/session/${id}`)
      .then(s => { if (!live) return; ui.current = migrate(s.schemaVersion, s.ui); activeTrainId.current = s.activeTrainId; setRestored(ui.current.route) })
      .catch(e => { if (!(e instanceof ApiError && e.status === 404)) console.warn('Could not load saved session state', e) })
      .finally(() => { if (live) { bump(n => n + 1); setReady(true) } })
    return () => { live = false }
  }, [enabled, id])

  const save = useCallback((keepalive = false) => {
    if (!dirty.current) return
    dirty.current = false
    fetch(`/api/v1/me/session/${id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, keepalive,
      body: JSON.stringify({ schemaVersion: SCHEMA_VERSION, activeTrainId: activeTrainId.current, ui: ui.current }),
    }).then(async r => {
      if (r.ok) { setSaveError(null); return }
      dirty.current = true   // keep the change; the next edit retries. Never drop a draft silently.
      const b = await r.json().catch(() => null) as { message?: string } | null
      setSaveError(b?.message ?? `Could not save your draft (${r.status})`)
    }).catch(() => { dirty.current = true; setSaveError('Could not save your draft: check your connection') })
  }, [id])

  const touch = useCallback(() => {
    dirty.current = true
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => save(), DEBOUNCE_MS)
    bump(n => n + 1)
  }, [save])

  useEffect(() => {
    const flush = () => { window.clearTimeout(timer.current); save(true) }
    const onVis = () => { if (document.visibilityState === 'hidden') flush() }
    document.addEventListener('visibilitychange', onVis)
    window.addEventListener('pagehide', flush)
    return () => { document.removeEventListener('visibilitychange', onVis); window.removeEventListener('pagehide', flush) }
  }, [save])

  const value: Ctx = {
    ready, restoredRoute, saveError,
    filter: ui.current.layout.filter ?? '',
    setFilter: f => { ui.current = { ...ui.current, layout: { ...ui.current.layout, filter: f || undefined } }; touch() },
    draft: <T,>(key: string) => ui.current.drafts[key] as T | undefined,
    setDraft: (key, v) => {
      const drafts = { ...ui.current.drafts }
      if (v === undefined) delete drafts[key]; else drafts[key] = v
      ui.current = { ...ui.current, drafts }; touch()
    },
    saveRoute: r => {
      const same = JSON.stringify(ui.current.route) === JSON.stringify(r)
      if (same && activeTrainId.current === r.trainId) return
      ui.current = { ...ui.current, route: r }; activeTrainId.current = r.trainId; touch()
    },
  }
  return <SessionCtx.Provider value={value}>{children}</SessionCtx.Provider>
}
