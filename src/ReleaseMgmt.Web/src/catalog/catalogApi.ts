import { get, patch, post } from '../api.ts'
import type { ServiceRow, TeamChoice } from './catalogLogic.ts'

/** The service catalog (REOS-109 API, REOS-155 screen). Archived services are listed too; the screen filters them. */
export const listServices = () => get<ServiceRow[]>('/api/v1/services?includeArchived=true')
export const createService = (b: { serviceKey: string; name: string; ownerTeamId: string | null; repository: string | null; projectCode: string | null }) => post<ServiceRow>('/api/v1/services', b)
export const updateService = (id: string, b: { name: string; ownerTeamId: string; repository: string; projectCode: string }, version: number) => patch<ServiceRow>(`/api/v1/services/${id}`, b, version)
export const archiveService = (id: string, archived: boolean, version: number) => post<ServiceRow>(`/api/v1/services/${id}:${archived ? 'archive' : 'unarchive'}`, {}, version)
export const listTeams = () => get<TeamChoice[]>('/api/v1/teams')
