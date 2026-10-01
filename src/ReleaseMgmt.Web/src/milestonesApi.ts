import { ApiError, get, patch, post } from './api'

/** Train milestones (Q-0840..): typed calls, kept out of api.ts so the shared file stays untouched. */
export interface Milestone {
  id: string; trainId: string; name: string; dueOn: string; tMinus: number
  ownerUserId: string | null; ownerTeamId: string | null; ownerName: string | null; note: string | null
  done: boolean; doneAt: string | null; doneByUserId: string | null; doneBy: string | null
  lastChangedByUserId: string | null; lastChangedAt: string | null; version: number
}
export interface MilestoneInput { name: string; dueOn: string; ownerUserId: string | null; ownerTeamId: string | null; note: string }

export const getMilestones = (trainId: string) => get<Milestone[]>(`/api/v1/trains/${encodeURIComponent(trainId)}/milestones`)
export const addMilestone = (trainId: string, m: MilestoneInput) =>
  post<unknown>(`/api/v1/trains/${encodeURIComponent(trainId)}/milestones`, { ...m, note: m.note.trim() || null })
/** Sends every field: the owner is replaced, or cleared when none is chosen; an empty note clears it. */
export const editMilestone = (id: string, m: MilestoneInput, version: number) =>
  patch<unknown>(`/api/v1/milestones/${encodeURIComponent(id)}`,
    { name: m.name, dueOn: m.dueOn, ownerUserId: m.ownerUserId, ownerTeamId: m.ownerTeamId, clearOwner: !m.ownerUserId && !m.ownerTeamId, note: m.note }, version)
export const milestoneAction = (id: string, action: 'done' | 'undone', version: number) => post<unknown>(`/api/v1/milestones/${encodeURIComponent(id)}:${action}`, {}, version)

/** DELETE with If-Match (api.ts's `del` sends none). */
export async function removeMilestone(id: string, version: number): Promise<void> {
  const r = await fetch(`/api/v1/milestones/${encodeURIComponent(id)}`, { method: 'DELETE', headers: { 'If-Match': String(version) } })
  if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
}
