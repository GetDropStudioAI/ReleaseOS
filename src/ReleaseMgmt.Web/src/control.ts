// Talks to the start.py supervisor through the Vite dev proxy (/control). Everything here is optional:
// when the app is not run by start.py, /control does not answer and the toolbar simply hides Reset and Exit.

export type ControlState = 'starting' | 'ready' | 'resetting' | 'stopping' | 'error'
export interface ControlStatus { state: ControlState; message: string; revision: string; running?: boolean }

const token = import.meta.env.VITE_CONTROL_TOKEN as string | undefined

export async function controlStatus(): Promise<ControlStatus | null> {
  try {
    const r = await fetch('/control/status', { cache: 'no-store' })
    return r.ok ? await r.json() : null
  } catch {
    return null // supervisor not there (or restarting): callers treat null as "unavailable right now"
  }
}

async function post(path: string): Promise<{ ok: boolean; message: string }> {
  try {
    const r = await fetch(path, { method: 'POST', headers: { 'X-Control-Token': token ?? '' } })
    const body = await r.json().catch(() => ({}))
    return { ok: r.ok, message: body.message ?? `Request failed (${r.status})` }
  } catch (e) {
    return { ok: false, message: (e as Error).message }
  }
}

export const requestReset = () => post('/control/reset')
export const requestExit = () => post('/control/exit')

/** Waits until the supervisor reports ready again after a reset (it goes down and up with the frontend), then resolves. */
export async function waitUntilReady(onProgress: (s: ControlStatus | null) => void, timeoutMs = 240_000): Promise<'ready' | 'error' | 'timeout'> {
  const start = Date.now()
  let sawBusy = false
  while (Date.now() - start < timeoutMs) {
    const s = await controlStatus()
    onProgress(s)
    if (s?.state === 'error') return 'error'
    if (s === null || s.state !== 'ready') sawBusy = true
    if (s?.state === 'ready' && sawBusy) return 'ready'
    await new Promise(r => setTimeout(r, 1000))
  }
  return 'timeout'
}
