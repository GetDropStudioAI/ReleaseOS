export interface Me { email: string; name: string; roles: string[] }
export interface UserRow { id: string; email: string; displayName: string; role: string; handle: string | null; isActive: boolean; version: number }
export interface TeamRow { id: string; handle: string; name: string; version: number; memberIds: string[] }
export interface HolidayRow { day: string; name: string; version: number }

export class ApiError extends Error {
  status: number
  body: { guard?: string; message?: string; current?: unknown } | null
  constructor(status: number, body: { guard?: string; message?: string; current?: unknown } | null) {
    super(body?.message ?? `Request failed (${status})`)
    this.status = status
    this.body = body
  }
}

async function call<T>(method: string, url: string, body?: unknown, ifMatch?: number): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (ifMatch !== undefined) headers['If-Match'] = String(ifMatch)
  const r = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
  return (r.status === 204 ? undefined : await r.json().catch(() => undefined)) as T
}

export const get = <T,>(url: string) => call<T>('GET', url)
export const post = <T,>(url: string, body?: unknown, ifMatch?: number) => call<T>('POST', url, body ?? {}, ifMatch)
export const patch = <T,>(url: string, body: unknown, ifMatch?: number) => call<T>('PATCH', url, body, ifMatch)
export const del = <T,>(url: string) => call<T>('DELETE', url)

export async function getMe(): Promise<Me | null> {
  const r = await fetch('/api/v1/me')
  if (r.status === 401) return null
  if (!r.ok) throw new Error(`GET /api/v1/me failed: ${r.status}`)
  return r.json()
}

export async function devLogin(email: string, name: string, role: string): Promise<boolean> {
  const r = await fetch('/auth/dev-login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, name, role }),
  })
  return r.ok
}

export async function logout() {
  await fetch('/auth/logout', { method: 'POST' })
}

export interface AuthConfig { organisationSignIn: boolean; passwordResetUrl: string | null }
export async function getAuthConfig(): Promise<AuthConfig> {
  try { return await get<AuthConfig>('/auth/config') } catch { return { organisationSignIn: true, passwordResetUrl: null } }
}

export interface StreamRow { id: string; title: string; status: string; riskTier: string; targetReleaseDate: string; daysToTarget: number; blockers: number; gates: string[]; closeCode: string | null; endedOn: string | null; version: number }
export interface GateRow { id: string; name: string; class: string; status: string; dueOn: string; requiredBeforeStatus: string; tasksDone: number; tasksTotal: number; version: number }
export interface TrainDetail { id: string; title: string; status: string; riskTier: string; targetReleaseDate: string; daysToTarget: number; changeTicketNumber: string | null; closeCode: string | null; rollbackRehearsed: boolean; nextStatus: string | null; version: number; gates: GateRow[] }
export interface Blocker { hop: string; failure: { guard: string; message: string; items?: string[] | null } }
export interface Readiness { trainId: string; version: number; status: string; target: string; ready: boolean; blockers: Blocker[] }
export const getStream = () => get<StreamRow[]>('/api/v1/trains')
export const getTrain = (id: string) => get<TrainDetail>(`/api/v1/trains/${id}`)
export const getReadiness = (id: string, target = 'Executing') => get<Readiness>(`/api/v1/trains/${id}/readiness?target=${target}`)
export const advanceTrain = (id: string, to: string, version: number) => post<unknown>(`/api/v1/trains/${id}:advance`, { to }, version)
