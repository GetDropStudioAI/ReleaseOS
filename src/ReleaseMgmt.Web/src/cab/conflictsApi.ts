import { get } from '../api.ts'

/** REOS-180: what a change collides with, derived by the server on every read (never stored). Every route answers 404 while the CAB module is off. */
export interface Conflict { type: ConflictType; rule: string; ruleLabel: string; summary: string; refId: string | null; refLabel: string | null; startsAt: string | null; endsAt: string | null }
export type ConflictType = 'Freeze' | 'Blackout' | 'ChangeOverlap' | 'ReleaseWindow'
export interface ConflictReport {
  changeId: string; key: string; status: string; plannedStartAt: string | null; plannedEndAt: string | null
  checked: boolean; reason: string | null; bufferMinutes: number; conflicts: Conflict[]
}
export interface AgendaConflictItem { changeId: string; key: string; title: string; status: string; checked: boolean; reason: string | null; conflicts: Conflict[] }
export interface MeetingConflicts { meetingId: string; items: AgendaConflictItem[] }
export interface CabCalendarChange { id: string; key: string; title: string; changeType: string; status: string; riskLevel: string; startsAt: string; endsAt: string; checked: boolean; conflicts: Conflict[] }
export interface CabCalendar { from: string; to: string; changes: CabCalendarChange[]; blackouts: { day: string; name: string }[] }
export interface ConflictRule { type: ConflictType; rule: string; label: string; description: string }
export interface ConflictRules { rules: ConflictRule[]; bufferMinutes: number; minReasonLength: number; approvalRule: string }

const base = '/api/v1/cab'
export const getConflicts = (changeId: string) => get<ConflictReport>(`${base}/changes/${encodeURIComponent(changeId)}/conflicts`)
export const getMeetingConflicts = (meetingId: string) => get<MeetingConflicts>(`${base}/meetings/${encodeURIComponent(meetingId)}/conflicts`)
export const getCabCalendar = (from: string, to: string) => get<CabCalendar>(`${base}/calendar?from=${from}&to=${to}`)
export const getConflictRules = () => get<ConflictRules>(`${base}/conflict-rules`)
