import { get, patch, post } from '../api.ts'
import type { ClientSchedule } from './clientDatesLogic.ts'

/** The clients' dates on a release (REOS-119). The version of a client's row is its If-Match; 0 means the client has no row yet. */
export const getClientSchedule = (trainId: string) => get<ClientSchedule>(`/api/v1/trains/${trainId}/client-schedule`)
export const setClientDates = (trainId: string, clientId: string, body: { chosenDate?: string; offeredDate?: string }, version: number) =>
  patch<ClientSchedule>(`/api/v1/trains/${trainId}/client-schedule/${clientId}`, body, version)
export const scheduleClients = (trainId: string) => post<ClientSchedule>(`/api/v1/trains/${trainId}/client-schedule:schedule`, {})
