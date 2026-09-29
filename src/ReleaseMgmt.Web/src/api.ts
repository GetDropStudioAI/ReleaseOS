export interface Me { id: string; email: string; name: string; roles: string[] }
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
export interface GateRow { id: string; name: string; class: string; status: string; dueOn: string; tMinus: number; requiredBeforeStatus: string; ownerName: string | null; certifiedBy: string | null; certifiedOn: string | null; tasksDone: number; tasksTotal: number; version: number }
export interface TrainDetail { id: string; title: string; status: string; riskTier: string; targetReleaseDate: string; daysToTarget: number; changeTicketNumber: string | null; closeCode: string | null; rollbackRehearsed: boolean; nextStatus: string | null; version: number; gates: GateRow[] }
export interface Blocker { hop: string; failure: { guard: string; message: string; items?: string[] | null } }
export interface Readiness { trainId: string; version: number; status: string; target: string; ready: boolean; blockers: Blocker[] }
export const getStream = () => get<StreamRow[]>('/api/v1/trains')
export const getTrain = (id: string) => get<TrainDetail>(`/api/v1/trains/${id}`)
export const getReadiness = (id: string, target = 'Executing') => get<Readiness>(`/api/v1/trains/${id}/readiness?target=${target}`)
export const advanceTrain = (id: string, to: string, version: number) => post<unknown>(`/api/v1/trains/${id}:advance`, { to }, version)

export interface ProductRow { id: string; name: string; versionTag: string; projectCode: string; tasksDone: number; tasksTotal: number; openBlockers: string[]; health: string }
export interface ProductsResponse { tasksDone: number; tasksTotal: number; products: ProductRow[] }
export interface TaskRow { id: string; description: string; owner: string | null; product: string | null; done: boolean; completedAt: string | null; completedBy: string | null; version: number }
export interface GateDetail { id: string; trainId: string; trainStatus: string; name: string; class: string; status: string; dueOn: string; tMinus: number; ownerName: string | null; version: number; tasks: TaskRow[]; certify: { eligible: boolean; reasons: string[] } }
export interface WindowRow { startsAt: string; endsAt: string; version: number }
export interface Owner { id: string; name: string; kind: 'user' | 'team' }
export const getProducts = (id: string) => get<ProductsResponse>(`/api/v1/trains/${id}/products`)
export const getGate = (id: string) => get<GateDetail>(`/api/v1/gates/${id}`)
export const getOwners = () => get<Owner[]>('/api/v1/owners')
export const getWindow = async (id: string): Promise<WindowRow | null> => {
  try { return await get<WindowRow>(`/api/v1/trains/${id}/window`) } catch (e) { if (e instanceof ApiError && e.status === 404) return null; throw e }
}
export const putWindow = (id: string, startsAt: string, endsAt: string, version?: number) =>
  call<WindowRow>('PUT', `/api/v1/trains/${id}/window`, { startsAt, endsAt }, version)
export const gateAction = (id: string, action: 'start' | 'certify' | 'fail' | 'reopen', version: number) => post<unknown>(`/api/v1/gates/${id}:${action}`, {}, version)
export const taskAction = (id: string, action: 'complete' | 'reopen', version: number) => post<unknown>(`/api/v1/tasks/${id}:${action}`, {}, version)
export const addTask = (gateId: string, description: string, owner: Owner) =>
  post<unknown>(`/api/v1/gates/${gateId}/tasks`, { description, ownerUserId: owner.kind === 'user' ? owner.id : null, ownerTeamId: owner.kind === 'team' ? owner.id : null })
