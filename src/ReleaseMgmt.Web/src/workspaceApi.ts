import { del, get, patch, post } from './api'
import type { WorkspaceChoice } from './workspace'

/** The people of the current workspace with the role held here (not the account's sign-in role) and the membership's Version for If-Match (REOS-106). */
export interface MemberRow { userId: string; email: string; displayName: string; handle: string | null; isActive: boolean; role: string; addedAt: string; version: number }

export const ROLE_OPTIONS = [
  { value: 'Viewer', label: 'Viewer' }, { value: 'RTE', label: 'RTE' }, { value: 'ReleaseManager', label: 'Release Manager' }, { value: 'GovernanceOfficer', label: 'Governance Officer' },
]
export const roleLabel = (role: string) => ROLE_OPTIONS.find(r => r.value === role)?.label ?? role

export const getWorkspaces = () => get<WorkspaceChoice[]>('/api/v1/workspaces')
export const createWorkspace = (body: { name: string; slug: string; region: string }) => post<WorkspaceChoice>('/api/v1/workspaces', body)
export const getMembers = () => get<MemberRow[]>('/api/v1/workspace/members')
export const addMember = (body: { email: string; displayName: string; role: string }) => post<MemberRow>('/api/v1/workspace/members', body)
export const setMemberRole = (m: MemberRow, role: string) => patch<MemberRow>(`/api/v1/workspace/members/${encodeURIComponent(m.userId)}`, { role }, m.version)
export const removeMember = (m: MemberRow) => del<MemberRow>(`/api/v1/workspace/members/${encodeURIComponent(m.userId)}`, m.version)
