import { get, patch, post, put, type ChainWarning, type CreatedTrain } from '../api.ts'

/** Versions (REOS-117, RT2, D48): one product version's releases in a chain. The shapes the API returns. */
export interface ChainItem { trainId: string; key: string | null; title: string; label: string; typeId: string; typeName: string; betaNumber: number | null; status: string; target: string; position: number }
export interface VersionView { id: string; name: string; archived: boolean; version: number; createdAt: string; chain: ChainItem[]; warnings: ChainWarning[] }
export interface NextOption { typeId: string; typeName: string; betaNumber: number | null; label: string }
export interface TrainVersionView { trainId: string; version: VersionView | null; next: NextOption[]; trainVersion: number; defaultTargetDate: string }
/** One step of a chain as the calendar and the overview draw it, and a release's place in it. */
export interface ChainStep { trainId: string; label: string; status: string; target: string }
export interface ChainBrief { versionId: string; versionName: string; label: string; position: number; count: number; steps: ChainStep[]; warning: string | null }

export const listVersions = (includeArchived = false) => get<VersionView[]>(`/api/v1/versions${includeArchived ? '?includeArchived=true' : ''}`)
export const createVersion = (name: string) => post<VersionView>('/api/v1/versions', { name })
export const renameVersion = (id: string, name: string, version: number) => patch<VersionView>(`/api/v1/versions/${id}`, { name }, version)
export const archiveVersion = (id: string, archived: boolean, version: number) => post<VersionView>(`/api/v1/versions/${id}:${archived ? 'archive' : 'unarchive'}`, {}, version)
export const getTrainVersion = (trainId: string) => get<TrainVersionView>(`/api/v1/trains/${trainId}/version`)
/** `versionId` null takes the release out of its version. `trainVersion` is the release's own Version (the If-Match). */
export const setTrainVersion = (trainId: string, versionId: string | null, trainVersion: number) => put<TrainVersionView>(`/api/v1/trains/${trainId}/version`, { versionId }, trainVersion)
export const createNextRelease = (trainId: string, b: { releaseTypeId?: string; betaNumber?: number | null; title?: string; targetReleaseDate?: string }) => post<CreatedTrain>(`/api/v1/trains/${trainId}:create-next`, b)
