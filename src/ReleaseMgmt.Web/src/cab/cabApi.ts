import { get, patch, post, type NamedRef, type UserRow } from '../api.ts'

/** The CAB module's change requests (REOS-176). Every route answers 404 while the module is off in the workspace; writes carry If-Match, 409 returns the current row. */
export interface RiskFactor { rule: string; label: string; applied: boolean; points: number; why: string }
export interface Risk { level: string; score: number; levelRule: string; factors: RiskFactor[] }
export interface DecisionRow { id: string; decision: string; retroactive: boolean; meetingId: string | null; rationale: string | null; decidedBy: NamedRef; decidedAt: string }
export interface GoNoGoRef { id: string; decision: string; decidedAt: string }
export interface ChangeRow {
  id: string; key: string; title: string; description: string | null; changeType: string; status: string; riskLevel: string; riskFactors: string | null
  requestedBy: NamedRef; createdBy: NamedRef; release: { id: string; title: string } | null; goNoGoDecisionId: string | null
  plannedStartAt: string | null; plannedEndAt: string | null; rollbackNote: string | null
  submittedAt: string | null; decidedBy: NamedRef | null; decidedAt: string | null; implementedAt: string | null; closedAt: string | null
  createdAt: string; lastChangedAt: string | null; version: number
  decisions: DecisionRow[]; needsReview: boolean; risk: Risk | null; goNoGo: GoNoGoRef | null
}
export interface NewChangeBody {
  title: string; changeType: string; description: string | null; requestedByUserId: string | null; releaseTrainId: string | null; goNoGoDecisionId: string | null
  plannedStartAt: string | null; plannedEndAt: string | null; rollbackNote: string | null
}
export interface ChangePatchBody extends Partial<NewChangeBody> { clear?: string[] }
export interface RiskRule { rule: string; label: string; points: string; description: string }
export interface RiskRules { rules: RiskRule[]; levelRule: string; thresholds: { nearFreezeHours: number; largeScopeProducts: number; priorFailureLookbackDays: number } }

const base = '/api/v1/cab'
export const listChanges = (f: { status?: string; type?: string } = {}) => {
  const q = new URLSearchParams()
  if (f.status) q.set('status', f.status)
  if (f.type) q.set('type', f.type)
  return get<ChangeRow[]>(`${base}/changes${q.size ? `?${q}` : ''}`)
}
export const getChange = (id: string) => get<ChangeRow>(`${base}/changes/${encodeURIComponent(id)}`)
export const getRiskRules = () => get<RiskRules>(`${base}/risk-rules`)
export const createChange = (b: NewChangeBody) => post<ChangeRow>(`${base}/changes`, b)
export const updateChange = (id: string, b: ChangePatchBody, version: number) => patch<ChangeRow>(`${base}/changes/${encodeURIComponent(id)}`, b, version)
export const moveChange = (id: string, action: 'submit' | 'cancel' | 'implement' | 'close', version: number) => post<ChangeRow>(`${base}/changes/${encodeURIComponent(id)}:${action}`, {}, version)
export const decideChange = (id: string, b: { decision: string; rationale: string | null; retroactive: boolean }, version: number) => post<ChangeRow>(`${base}/changes/${encodeURIComponent(id)}/decisions`, b, version)
export const getUsers = () => get<UserRow[]>('/api/v1/users')
