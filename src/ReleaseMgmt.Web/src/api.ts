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

export interface StepRow { id: string; stepCode: string; section: string; title: string; instructions: string | null; ownerUserId: string | null; ownerTeamId: string | null; ownerName: string | null; productId: string | null; productName: string | null; plannedStartAt: string; plannedEndAt: string; plannedDurationMin: number; dependsOn: { id: string; code: string }[]; version: number }
export const SECTIONS = ['PreCheck', 'Deploy', 'Verify', 'Rollback', 'Hypercare'] as const
export const getSteps = (trainId: string) => get<StepRow[]>(`/api/v1/trains/${trainId}/steps`)
export interface NewStepBody { title: string; section: string; ownerUserId?: string | null; ownerTeamId?: string | null; plannedStartAt: string; plannedDurationMin: number }
export const createStep = (trainId: string, b: NewStepBody) => post<StepRow>(`/api/v1/trains/${trainId}/steps`, b)
export const patchStep = (id: string, b: Partial<NewStepBody> & { instructions?: string }, version: number) => patch<StepRow>(`/api/v1/steps/${id}`, b, version)
export const setStepDependencies = (id: string, dependsOn: string[], version: number) => call<StepRow>('PUT', `/api/v1/steps/${id}/dependencies`, { dependsOn }, version)

export interface ParsedTask { line: number; gateId: string; gateName: string; description: string; ownerKind: string; ownerId: string | null; ownerName: string | null; productId: string | null; productName: string | null; warnings: string[] }
export interface ParseIssue { line: number; severity: string; code: string; message: string; suggestions: string[] }
export interface ParsePreview { previewId: string; trainVersion: number; expiresAt: string; errorCount: number; warningCount: number; tasks: ParsedTask[]; issues: ParseIssue[]; decertifiesGates: string[] }
export const parseTasks = (trainId: string, text: string, defaultGateId: string | null) => post<ParsePreview>(`/api/v1/trains/${trainId}/tasks:parse`, { text, defaultGateId })
export const commitTasks = (trainId: string, previewId: string, acknowledgeDecertify: boolean) => post<{ inserted: number; decertifiedGates: string[] }>(`/api/v1/trains/${trainId}/tasks:commit`, { previewId, acknowledgeDecertify })

export interface FreezeAhead { id: string; name: string; kind: string; startsAt: string; endsAt: string; scope: string; active: boolean; overridesGranted: number }
export const getFreezesAhead = () => get<FreezeAhead[]>('/api/v1/freeze-windows/ahead')

export const getConfig = () => get<{ displayTimeZone: string }>('/api/v1/config')

export interface RunSummary { id: string; mode: 'Rehearsal' | 'Live'; startedAt: string; endedAt: string | null; outcome: string | null; version: number }
export interface RunStepRow { stepId: string; stepCode: string; section: string; title: string; ownerName: string | null; plannedStartAt: string; plannedEndAt: string; plannedDurationMin: number; status: string; actualStartAt: string | null; actualEndAt: string | null; actorName: string | null; note: string | null; dependsOn: string[]; blocks: string[]; instructions: string | null; canAct: boolean; version: number }
export interface RunDetail { id: string; trainId: string; mode: 'Rehearsal' | 'Live'; startedAt: string; startedBy: string | null; endedAt: string | null; outcome: string | null; shiftMinutes: number; version: number; steps: RunStepRow[] }
export interface ForecastStepRow { step: string; state: string; start: string | null; end: string | null; endVarianceMin: number | null }
export interface RunForecast { runId: string; mode: string; asOf: string; windowStart: string | null; windowEnd: string | null; forecastFinish: string | null; plannedFinish: string; rollbackPlannedMin: number; rollbackDeadline: string | null; crossesDeadlineByMin: number | null; alertRaised: boolean; windowClosesInSec: number | null; blockedByFailed: string[]; steps: ForecastStepRow[] }
export const getRuns = (trainId: string) => get<RunSummary[]>(`/api/v1/trains/${trainId}/runs`)
export const getRun = (id: string) => get<RunDetail>(`/api/v1/runs/${id}`)
export const getForecast = (id: string) => get<RunForecast>(`/api/v1/runs/${id}/forecast`)
export const startRun = (trainId: string, mode: 'Rehearsal' | 'Live') => post<RunSummary>(`/api/v1/trains/${trainId}/runs`, { mode })
export const runStepAction = (runId: string, stepId: string, action: 'start' | 'done' | 'fail' | 'skip', note: string | null, version: number) =>
  post<unknown>(`/api/v1/runs/${runId}/steps/${stepId}:${action}`, { note }, version)
export const endRun = (runId: string, outcome: 'Completed' | 'RolledBack' | 'Aborted', version: number) => post<unknown>(`/api/v1/runs/${runId}:end`, { outcome }, version)

export interface ChangeRecord { releaseTrainId: string; justification: string | null; implementationPlan: string | null; riskImpactAnalysis: string | null; backoutPlan: string | null; testPlan: string | null; communicationPlan: string | null; cabDate: string | null; version: number }
export interface AffectedCi { id: string; ciName: string; ciExternalId: string | null }
export const getChangeRecord = (id: string) => get<ChangeRecord>(`/api/v1/trains/${id}/change-record`)
export const putChangeRecord = (id: string, b: Omit<ChangeRecord, 'releaseTrainId' | 'version'>, version: number) => call<ChangeRecord>('PUT', `/api/v1/trains/${id}/change-record`, b, version)
export const getCis = (id: string) => get<AffectedCi[]>(`/api/v1/trains/${id}/cis`)
export const addCi = (id: string, ciName: string, ciExternalId: string | null) => post<AffectedCi>(`/api/v1/trains/${id}/cis`, { ciName, ciExternalId })
export const removeCi = (id: string, ciId: string) => del<unknown>(`/api/v1/trains/${id}/cis/${ciId}`)

export interface Condition { id: string; decisionId: string; text: string; ownerUserId: string; expiresAt: string; closedAt: string | null; closedByUserId: string | null; version: number }
export interface Decision { id: string; decision: 'Go' | 'NoGo' | 'GoWithConditions'; decidedByUserId: string; decidedAt: string; notes: string | null; newTargetReleaseDate: string | null }
export interface DecisionView { decision: Decision; conditions: Condition[] }
export interface NewConditionBody { text: string; ownerUserId: string; expiresAt: string }
export const getDecisions = (trainId: string) => get<DecisionView[]>(`/api/v1/trains/${trainId}/gonogo`)
export const recordDecision = (trainId: string, b: { decision: string; notes: string | null; newTargetReleaseDate: string | null; conditions: NewConditionBody[] }, version: number) => post<unknown>(`/api/v1/trains/${trainId}/gonogo`, b, version)
export const closeCondition = (id: string, version: number) => post<unknown>(`/api/v1/conditions/${id}:close`, {}, version)

// REOS-34: freeze windows, immutable overrides (renew = new row) and waivers
export interface FreezeWindow { id: string; name: string; kind: 'Freeze' | 'Chill'; startsAt: string; endsAt: string; productPattern: string | null; createdByUserId: string; version: number }
export interface FreezeOverride { id: string; freezeWindowId: string; releaseTrainId: string; reason: string; requestedByUserId: string; approvedByUserId: string; approvedAt: string; expiresAt: string }
export interface FreezeRow { window: FreezeWindow; overrides: FreezeOverride[]; active: boolean; coversTrain: boolean | null; trainHasValidOverride: boolean | null }
export interface Waiver { id: string; stageGateId: string; reason: string; requestedByUserId: string; approvedByUserId: string | null; requestedAt: string; approvedAt: string | null; version: number }
export const getFreezes = (trainId?: string) => get<FreezeRow[]>(`/api/v1/freezes${trainId ? `?trainId=${encodeURIComponent(trainId)}` : ''}`)
export const createFreeze = (b: { name: string; kind: string; startsAt: string; endsAt: string; productPattern: string | null }) => post<FreezeWindow>('/api/v1/freezes', b)
export const grantOverride = (windowId: string, b: { trainId: string; requestedByUserId: string; reason: string; expiresAt: string }) => post<FreezeOverride>(`/api/v1/freezes/${windowId}/overrides`, b)
export const requestOverride = (windowId: string, b: { trainId: string; reason: string; expiresAt: string | null }) => post<{ notified: number }>(`/api/v1/freezes/${windowId}/override-requests`, b)
export const getWaivers = (gateId: string) => get<Waiver[]>(`/api/v1/gates/${gateId}/waivers`)
export const requestWaiver = (gateId: string, reason: string) => post<Waiver>(`/api/v1/gates/${gateId}/waivers`, { reason })
export const waiveGate = (gateId: string, version: number) => post<unknown>(`/api/v1/gates/${gateId}:waive`, {}, version)
export const approveWaiver =(id: string, version: number) => post<Waiver>(`/api/v1/waivers/${id}:approve`, {}, version)
// REOS-36: rollback attestation, PIR, PIR actions, known issues, hypercare (Closeout.tsx)
export interface RehearsalRun { id: string; startedAt: string; endedAt: string | null; outcome: string | null }
export interface RollbackAttestation { required: boolean; rehearsedAt: string | null; rehearsedByUserId: string | null; rehearsedByName: string | null; rehearsalRuns: RehearsalRun[] }
export interface Pir { id: string; releaseTrainId: string; requiredReason: string; status: 'Required' | 'Scheduled' | 'Held' | 'Closed'; heldAt: string | null; summary: string | null; version: number }
export interface PirAction { id: string; pirId: string; text: string; ownerUserId: string; dueOn: string; doneAt: string | null; version: number }
export interface PirViewData { pir: Pir | null; actions: PirAction[] }
export interface KnownIssue { id: string; title: string; severity: 'Critical' | 'High' | 'Medium' | 'Low'; workaround: string | null; status: 'Open' | 'Accepted' | 'Resolved'; externalKey: string | null; raisedAt: string; resolvedAt: string | null; version: number }
export interface KnownIssuesData { hypercareExitAt: string | null; hypercareExitByUserId: string | null; issues: KnownIssue[] }
export const getAttestation = (id: string) => get<RollbackAttestation>(`/api/v1/trains/${id}/rollback-attestation`)
export const attestRollback = (id: string, runId: string | null, note: string | null, version: number) => post<unknown>(`/api/v1/trains/${id}:rehearsed-rollback`, { runId, note }, version)
export const getPir = (id: string) => get<PirViewData>(`/api/v1/trains/${id}/pir`)
export const createPir = (id: string) => post<Pir>(`/api/v1/trains/${id}/pir`)
export const savePirSummary = (id: string, summary: string, version: number) => patch<Pir>(`/api/v1/trains/${id}/pir`, { summary }, version)
export const pirAction = (id: string, action: 'schedule' | 'hold' | 'close', version: number, summary?: string) => post<Pir>(`/api/v1/trains/${id}/pir:${action}`, action === 'hold' ? { summary: summary ?? null } : {}, version)
export const addPirAction = (id: string, b: { text: string; ownerUserId: string; dueOn: string }) => post<PirAction>(`/api/v1/trains/${id}/pir/actions`, b)
export const patchPirAction = (id: string, b: { text?: string; ownerUserId?: string; dueOn?: string }, version: number) => patch<PirAction>(`/api/v1/pir-actions/${id}`, b, version)
export const pirActionState = (id: string, action: 'complete' | 'reopen', version: number) => post<PirAction>(`/api/v1/pir-actions/${id}:${action}`, {}, version)
export const getKnownIssues = (id: string) => get<KnownIssuesData>(`/api/v1/trains/${id}/known-issues`)
export const addKnownIssue = (id: string, b: { title: string; severity: string; workaround: string | null; externalKey: string | null }) => post<KnownIssue>(`/api/v1/trains/${id}/known-issues`, b)
export const patchKnownIssue = (id: string, issueId: string, b: { title?: string; severity?: string; workaround?: string; externalKey?: string }, version: number) => patch<KnownIssue>(`/api/v1/trains/${id}/known-issues/${issueId}`, b, version)
export const knownIssueAction = (id: string, issueId: string, action: 'accept' | 'resolve' | 'reopen', version: number) => post<KnownIssue>(`/api/v1/trains/${id}/known-issues/${issueId}:${action}`, {}, version)
export const exitHypercare = (id: string, version: number) => post<unknown>(`/api/v1/trains/${id}:exit-hypercare`, {}, version)
