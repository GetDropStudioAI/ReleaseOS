import { get, patch, post } from '../api.ts'
import type { ClientRow, EnvironmentChoice, Segment } from './clientsLogic.ts'

/** The workspace's clients (REOS-118). Archived ones are listed only when asked for, as the management screen does. */
export const listClients = (includeArchived = false) => get<ClientRow[]>(`/api/v1/clients${includeArchived ? '?includeArchived=true' : ''}`)
export const createClient = (b: { name: string; segment: Segment; certEnvironmentId?: string | null }) => post<ClientRow>('/api/v1/clients', b)
/** An empty `certEnvironmentId` clears the environment; a missing field leaves it alone. */
export const updateClient = (id: string, b: { name?: string; segment?: Segment; certEnvironmentId?: string }, version: number) => patch<ClientRow>(`/api/v1/clients/${id}`, b, version)
export const archiveClient = (id: string, archived: boolean, version: number) => post<ClientRow>(`/api/v1/clients/${id}:${archived ? 'archive' : 'unarchive'}`, {}, version)
/** The environments a CERT client can name (archived ones too, so one already chosen can be shown). */
export const listEnvironmentChoices = () => get<EnvironmentChoice[]>('/api/v1/environments?includeArchived=true')
