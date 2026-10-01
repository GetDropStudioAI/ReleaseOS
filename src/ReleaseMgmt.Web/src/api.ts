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

// ---- REOS-38: inbox and My work (own rows only) ----
export interface InboxItem { id: string; kind: string; entityType: string; entityId: string; escalationLevel: number; message: string; createdAt: string; readAt: string | null; version: number; trainId: string | null; trainTitle: string | null }
export interface InboxPage { items: InboxItem[]; total: number; unread: number; limit: number; offset: number }
export const getInbox = (unreadOnly: boolean, limit: number, offset: number) => get<InboxPage>(`/api/v1/me/notifications?unread=${unreadOnly}&limit=${limit}&offset=${offset}`)
export const getUnreadCount = () => get<{ unread: number; total: number }>('/api/v1/me/notifications/count')
export const markNotificationRead = (id: string) => post<{ id: string; readAt: string; version: number }>(`/api/v1/notifications/${id}:read`)
export const markAllNotificationsRead = () => post<{ marked: number }>('/api/v1/me/notifications:read-all')

export interface WorkVia { kind: 'me' | 'team'; teamId: string | null; teamName: string | null }
export interface MyWorkData {
  counts: { tasks: number; gates: number; steps: number; conditions: number; pirActions: number; total: number; overdue: number }
  tasks: { id: string; description: string; gateId: string; gateName: string; trainId: string; trainTitle: string; dueOn: string; overdue: boolean; via: WorkVia }[]
  gates: { id: string; name: string; status: string; dueOn: string; overdue: boolean; openTasks: number; trainId: string; trainTitle: string; via: WorkVia }[]
  steps: { executionId: string; runId: string; runMode: string; stepId: string; stepCode: string; title: string; section: string; status: string; plannedStartAt: string; late: boolean; trainId: string; trainTitle: string; via: WorkVia }[]
  conditions: { id: string; text: string; expiresAt: string; expired: boolean; decisionId: string; trainId: string; trainTitle: string }[]
  pirActions: { id: string; text: string; dueOn: string; overdue: boolean; pirId: string; trainId: string; trainTitle: string }[]
}
export const getMyWork = () => get<MyWorkData>('/api/v1/me/work')
// REOS-38 train templates
export interface TemplateRow { id: string; name: string; status: string; defaultRiskTier: string; approvedByUserId: string | null; approvedByName: string | null; approvedAt: string | null; reviewDueOn: string | null; reviewOverdue: boolean; gateCount: number; stepCount: number; scheduleCount: number; version: number }
export interface TemplateGate { id?: string; gateName: string; gateClass: string; sequenceOrder?: number; offsetDays: number; requiredBeforeStatus: string; ownerTeamId: string | null; ownerTeamName?: string | null }
export interface TemplateStep { id?: string; stepCode: string; section: string; title: string; offsetMinutes: number; plannedDurationMin: number; ownerTeamId: string | null; ownerTeamName?: string | null }
export interface TemplateSchedule { id?: string; libraryTemplateId: string; libraryName?: string; templateType?: string; offsetDays: number }
export interface TemplateDetail { template: TemplateRow; gates: TemplateGate[]; steps: TemplateStep[]; schedule: TemplateSchedule[] }
export interface TemplateInput { name: string; defaultRiskTier: string; gates: TemplateGate[]; steps: TemplateStep[]; schedule: TemplateSchedule[] }
export interface LibraryOption { id: string; name: string; templateType: string }
export const getTemplates = () => get<TemplateRow[]>('/api/v1/templates')
export const getTemplate = (id: string) => get<TemplateDetail>(`/api/v1/templates/${id}`)
export const getLibraryOptions = () => get<LibraryOption[]>('/api/v1/templates/library-options')
export const createTemplate = (b: TemplateInput) => post<TemplateDetail>('/api/v1/templates', b)
export const updateTemplate = (id: string, b: TemplateInput, version: number) => call<TemplateDetail>('PUT', `/api/v1/templates/${id}`, b, version)
export const templateAction = (id: string, action: 'approve' | 'retire', version: number) => post<TemplateDetail>(`/api/v1/templates/${id}:${action}`, {}, version)
// REOS-80 new train: blank, from an Approved template, or a copy of a prior train
export interface NewTrainBody { title: string; targetReleaseDate: string; riskTier?: string | null; templateId?: string | null; windowStartsAt?: string | null; windowEndsAt?: string | null; products?: { productName: string; versionTag: string; projectCode: string }[] }
export interface CreatedTrain { id: string; title: string; status: string; riskTier: string; targetReleaseDate: string; source: 'Blank' | 'Template' | 'Clone'; templateId: string | null; clonedFromTrainId: string | null; version: number
  created: { products: number; gates: number; tasks: number; steps: number; dependencies: number; commTemplates: number; commSchedule: number; window: boolean }; notes: string[] }
export const createTrain = (b: NewTrainBody) => post<CreatedTrain>('/api/v1/trains', b)
export const cloneTrain = (sourceId: string, b: { title: string; targetReleaseDate: string; riskTier?: string | null }) => post<CreatedTrain>(`/api/v1/trains/${encodeURIComponent(sourceId)}:clone`, b)

// REOS-42: DELETE with If-Match (webhook allowlist rows carry a Version)
export const delIfMatch = <T,>(url: string, ifMatch: number) => call<T>('DELETE', url, undefined, ifMatch)
// ---- REOS-43/44: comm library, per-train messages, T-minus schedule, preview (CommLibrary.tsx, CommSchedule.tsx) ----
export interface TokenError { kind: string; token: string; part: 'subject' | 'body'; line: number; column: number; message: string; suggestion: string | null }
export interface CommToken { name: string; description: string }
export interface LibraryTemplate { id: string; templateType: string; name: string; audience: string; subjectLine: string; markdownBody: string; version: number; tokensUsed: string[]; tokenErrors: TokenError[]; trainCopies: number; valid: boolean }
export interface LibraryInput { name: string; templateType: string; audience: string; subjectLine: string; markdownBody: string }
export interface TrainMessage { id: string; releaseTrainId: string; libraryTemplateId: string | null; libraryName: string | null; templateType: string; audience: string; subjectLine: string; markdownBody: string; version: number; dispatched: boolean; dispatchCount: number; tokensUsed: string[]; tokenErrors: TokenError[]; valid: boolean }
export interface ScheduleItem { id: string; releaseTrainId: string; commTemplateId: string; name: string; templateType: string; audience: string; subjectLine: string; dueAt: string; sentAt: string | null; dispatchId: string | null; tMinus: number; label: string; state: 'Sent' | 'SentLate' | 'Overdue' | 'Ready' | 'Scheduled'; late: boolean; lateMinutes: number; version: number }
export type CommTarget = 'PlainText' | 'Markdown' | 'Html' | 'JsonString'
export interface CommPreview { subject: string | null; text: string; tokenErrors: TokenError[]; asOf: string; trainVersion: number; canDispatch: boolean; target: CommTarget; tokensUsed: string[]; templateId: string | null; templateVersion: number | null }
export const COMM_AUDIENCES = ['Exec', 'Ops', 'SupportDesk', 'Clients', 'All'] as const
export const getCommTokens = () => get<CommToken[]>('/api/v1/comm-library/tokens')
export const getCommLibrary = () => get<LibraryTemplate[]>('/api/v1/comm-library')
export const createLibraryTemplate = (b: LibraryInput) => post<LibraryTemplate>('/api/v1/comm-library', b)
export const updateLibraryTemplate = (id: string, b: LibraryInput, version: number) => call<LibraryTemplate>('PUT', `/api/v1/comm-library/${id}`, b, version)
export const getTrainMessages = (trainId: string) => get<TrainMessage[]>(`/api/v1/trains/${trainId}/comms`)
export const copyToTrain = (trainId: string, libraryTemplateId: string) => post<TrainMessage>(`/api/v1/trains/${trainId}/comms`, { libraryTemplateId })
export const updateTrainMessage = (id: string, b: { audience: string; subjectLine: string; markdownBody: string }, version: number) => call<TrainMessage>('PUT', `/api/v1/comm-templates/${id}`, b, version)
export const getCommSchedule = (trainId: string) => get<ScheduleItem[]>(`/api/v1/trains/${trainId}/comm-schedule`)
export const seedCommSchedule = (trainId: string, templateId: string) => post<ScheduleItem[]>(`/api/v1/trains/${trainId}/comm-schedule:seed`, { templateId })
export const markCommSent = (id: string, version: number) => post<ScheduleItem>(`/api/v1/comm-schedule/${id}:mark-sent`, {}, version)
export const previewComm = (trainId: string, b: { templateId?: string; libraryTemplateId?: string; subject?: string; text?: string; target?: CommTarget }) =>
  post<CommPreview>(`/api/v1/trains/${trainId}/comms:preview`, b)
// REOS-39 connectors (credentials are write-only: no response carries them, only whether some are stored and of what kind)
export interface ConnectorRow {
  source: 'Jira' | 'ServiceNow'; baseUrl: string; isEnabled: boolean; hasCredentials: boolean; credentialKind: string | null; status: string
  lastCycleStartedAt: string | null; lastCycleCompletedAt: string | null; lastSuccessAt: string | null; consecutiveFailures: number
  openAlerts: number; linkCount: number; staleLinks: number; mismatchLinks: number; version: number | null
}
export const getConnectors = () => get<ConnectorRow[]>('/api/v1/connectors')
export const saveConnector = (source: string, body: { baseUrl?: string; isEnabled?: boolean }, version?: number | null) =>
  call<ConnectorRow>('PUT', `/api/v1/connectors/${source}`, body, version ?? undefined)
export const setConnectorCredentials = (source: string, body: { kind: string; username: string; secret: string }) =>
  call<ConnectorRow>('PUT', `/api/v1/connectors/${source}/credentials`, body)
export const clearConnectorCredentials = (source: string) => call<ConnectorRow>('DELETE', `/api/v1/connectors/${source}/credentials`)
export const testConnector = (source: string) => post<{ ok: boolean; source: string; elapsedMs: number }>(`/api/v1/connectors/${source}:test`)
export const syncConnector = (source: string) => post<{ source: string; outcome: string; linksChecked: number; errorKind: string | null; message: string | null }>(`/api/v1/connectors/${source}:sync`)

// REOS-50 PDF export jobs (release report, run sheet, evidence pack + ZIP, scorecard). The file itself is fetched from exportJobFileUrl (a plain link: attachment, private, no-store).
export type ExportKind = 'ReleaseReport' | 'RunSheet' | 'EvidencePack' | 'Scorecard'
export interface ExportJob {
  id: string; kind: string; label: string; format: 'pdf' | 'zip'; trainId: string | null; trainTitle: string | null; status: 'Queued' | 'Running' | 'Done' | 'Failed'
  fileName: string; sha256: string | null; sizeBytes: number | null; contentType: string | null; error: string | null; attempts: number; ref: string
  requestedByUserId: string; requestedByName: string | null; createdAt: string; startedAt: string | null; completedAt: string | null; version: number
  /** REOS-72: set when the file was deleted after Exports:RetentionDays; the record and its SHA-256 stay, the download answers 410. */
  expiredAt?: string | null
}
export const listExportJobs = (trainId: string) => get<ExportJob[]>(`/api/v1/export-jobs?trainId=${encodeURIComponent(trainId)}`)
export const createExportJob = (trainId: string, kind: ExportKind, format: 'pdf' | 'zip' = 'pdf') => post<ExportJob>(`/api/v1/trains/${trainId}/export-jobs`, { kind, format })
export const exportJobFileUrl = (id: string) => `/api/v1/export-jobs/${id}/file`

// ---- REOS-81: add, edit and remove a train's products and gates (Planning.tsx). Edits and removals carry If-Match. ------------------------------
export type ProductRowV = ProductRow & { version: number }
export interface ProductInput { name?: string; versionTag?: string; projectCode?: string }
export interface ProductSaved { id: string; releaseTrainId: string; productName: string; versionTag: string; projectCode: string; version: number }
export const addProduct = (trainId: string, p: ProductInput) => post<ProductSaved>(`/api/v1/trains/${trainId}/products`, p)
export const patchProduct = (id: string, p: ProductInput, version: number) => patch<ProductSaved>(`/api/v1/products/${id}`, p, version)
export const removeProduct = (id: string, version: number) => delIfMatch<ProductSaved>(`/api/v1/products/${id}`, version)
export const GATE_CLASSES = ['Standard', 'Compliance'] as const
export const REQUIRED_BEFORE = ['Gated', 'Executing', 'Complete'] as const
/** Exactly one of offsetDays / dueOn and one of ownerUserId / ownerTeamId. A gate's status is never part of this: it moves only through its actions. */
export interface GateInput {
  gateName?: string; gateClass?: string; offsetDays?: number; dueOn?: string; requiredBeforeStatus?: string
  ownerUserId?: string | null; ownerTeamId?: string | null; sequenceOrder?: number
}
export interface GateSaved {
  id: string; releaseTrainId: string; gateName: string; gateClass: string; sequenceOrder: number; offsetDays: number; dueOn: string; requiredBeforeStatus: string
  ownerUserId: string | null; ownerTeamId: string | null; status: string; version: number
}
export type GateDefinition = GateDetail & { sequenceOrder: number; offsetDays: number; requiredBeforeStatus: string; ownerUserId: string | null; ownerTeamId: string | null; targetReleaseDate: string }
export const getGateDefinition = (id: string) => get<GateDefinition>(`/api/v1/gates/${id}`)
export const addGate = (trainId: string, g: GateInput) => post<GateSaved>(`/api/v1/trains/${trainId}/gates`, g)
export const patchGate = (id: string, g: GateInput, version: number) => patch<GateSaved>(`/api/v1/gates/${id}`, g, version)
export const removeGate = (id: string, version: number) => delIfMatch<GateSaved>(`/api/v1/gates/${id}`, version)

// REOS-82 external links on a train (keys, states and dates only, OI-12). Remove sends the link's Version (If-Match).
export type LinkEntityType = 'Train' | 'Product' | 'Gate' | 'RunbookStep' | 'Blocker' | 'KnownIssue'
export interface LinkWarning { rule: string; message: string; since: string }
export interface ExternalLinkRow {
  id: string; trainId: string; entityType: LinkEntityType; entityId: string; sourceSystem: 'Jira' | 'ServiceNow'; externalKey: string
  expectedStatus: string | null; lastSyncedStatus: string | null; lastSyncedAt: string | null; syncState: string; stale: boolean; version: number; warnings: LinkWarning[]
}
export const getLinks = (trainId: string) => get<ExternalLinkRow[]>(`/api/v1/trains/${trainId}/links`)
export const addLink = (trainId: string, body: { entityType: LinkEntityType; entityId: string; sourceSystem: string; externalKey: string }) =>
  post<ExternalLinkRow>(`/api/v1/trains/${trainId}/links`, body)
export const removeLink = (id: string, version: number) => call<boolean>('DELETE', `/api/v1/links/${id}`, undefined, version)

// REOS-83 add a user ahead of their first sign-in (admin). The role is provisional: sign-in replaces it with the identity provider's.
export const createUser = (body: { email: string; displayName: string; role: string; handle?: string }) => post<UserRow>('/api/v1/users', body)
