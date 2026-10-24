import { get, patch, post } from '../api.ts'
import type { ReleaseTypeRow } from './releaseTypesLogic.ts'

/** The workspace's release types (REOS-116). Archived ones are listed only when asked for, as the management screen does. */
export const listReleaseTypes = (includeArchived = false) => get<ReleaseTypeRow[]>(`/api/v1/release-types${includeArchived ? '?includeArchived=true' : ''}`)
export const createReleaseType = (b: { name: string; defaultTemplateId?: string | null; isBeta?: boolean }) => post<ReleaseTypeRow>('/api/v1/release-types', b)
/** An empty `defaultTemplateId` clears the default template; a missing field leaves it alone. */
export const updateReleaseType = (id: string, b: { name?: string; defaultTemplateId?: string; sortOrder?: number; isBeta?: boolean }, version: number) => patch<ReleaseTypeRow>(`/api/v1/release-types/${id}`, b, version)
export const archiveReleaseType = (id: string, archived: boolean, version: number) => post<ReleaseTypeRow>(`/api/v1/release-types/${id}:${archived ? 'archive' : 'unarchive'}`, {}, version)
