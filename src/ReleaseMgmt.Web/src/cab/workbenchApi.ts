import { get, patch, post, type NamedRef } from '../api.ts'
import type { ChangeRow, DecisionRow } from './cabApi.ts'

/** The CAB workbench's calls (REOS-179). Every route answers 404 while the module is off. Writes carry If-Match; 409 returns the current row. */
export interface AgendaItem { id: string; changeRequestId: string; changeKey: string; changeTitle: string; changeStatus: string; sequenceOrder: number; notes: string | null; version: number }
export interface Attendee { person: NamedRef; attendeeRole: string; attendance: string; isChair: boolean; version: number }
export interface Meeting {
  id: string; title: string; startsAt: string; endsAt: string; chair: NamedRef; status: string; location: string | null; minutes: string | null
  createdAt: string; lastChangedAt: string | null; version: number; agenda: AgendaItem[]; attendees: Attendee[]; decisions: DecisionRow[]; completedAt: string | null
}

const base = '/api/v1/cab/meetings'
const id = encodeURIComponent
export const listMeetings = () => get<Meeting[]>(base)
export const getMeeting = (meetingId: string) => get<Meeting>(`${base}/${id(meetingId)}`)
export const getPresence = (meetingId: string) => get<{ userIds: string[] }>(`${base}/${id(meetingId)}/presence`)
export const moveMeeting = (meetingId: string, action: 'start' | 'complete', version: number) => post<Meeting>(`${base}/${id(meetingId)}:${action}`, {}, version)
export const moveAgendaItem = (meetingId: string, itemId: string, position: number, version: number) => post<Meeting>(`${base}/${id(meetingId)}/agenda/${id(itemId)}:move`, { position }, version)
export const setAttendance = (meetingId: string, userId: string, attendance: string, version: number) => patch<Attendee>(`${base}/${id(meetingId)}/attendees/${id(userId)}`, { attendance }, version)
/** One person's decision on a change, saved under this meeting. The change's version is the If-Match: a second person deciding from the same screen gets 409 with the current row. */
export const decideInMeeting = (changeId: string, meetingId: string, decision: string, rationale: string | null, version: number) =>
  post<ChangeRow>(`/api/v1/cab/changes/${id(changeId)}/decisions`, { decision, rationale, meetingId, retroactive: false }, version)
