import type { TeamRef } from '../api.ts'

/** The pure parts of the service catalog screen (REOS-155, B8, D42): the row the API returns, search and filter, what the form asks for, and what a save sends. Tested without a browser. */
export interface ServiceRow {
  id: string; serviceKey: string; name: string; ownerTeam: TeamRef | null; repository: string | null; projectCode: string | null
  archived: boolean; products: number; deployments: number; version: number
}
export interface TeamChoice { id: string; handle: string; name: string }
export interface ServiceDraft { key: string; name: string; ownerTeamId: string; repository: string; projectCode: string }
export type StateFilter = 'active' | 'archived' | 'all'

/** The server's limits (ReleaseModelLimits) and key rule, repeated so the form can say what is wrong before the request. */
export const MAX_KEY = 60, MAX_NAME = 200, MAX_REPOSITORY = 300, MAX_PROJECT_CODE = 100
export const KEY_PATTERN = /^[A-Za-z0-9._-]{1,60}$/
export const NO_TEAM = ''

export const STATE_OPTIONS: { value: StateFilter; label: string }[] = [
  { value: 'active', label: 'Active services' }, { value: 'archived', label: 'Archived services' }, { value: 'all', label: 'All services' },
]

export const emptyDraft = (): ServiceDraft => ({ key: '', name: '', ownerTeamId: NO_TEAM, repository: '', projectCode: '' })
export const draftOf = (r: ServiceRow): ServiceDraft => ({ key: r.serviceKey, name: r.name, ownerTeamId: r.ownerTeam?.id ?? NO_TEAM, repository: r.repository ?? '', projectCode: r.projectCode ?? '' })

/** Active services first, then by key; archived ones last. */
export const sortServices = (rows: ServiceRow[]): ServiceRow[] =>
  [...rows].sort((a, b) => Number(a.archived) - Number(b.archived) || a.serviceKey.localeCompare(b.serviceKey, undefined, { sensitivity: 'base' }))

/** Rows in the chosen state whose key, name, owner team, repository or project code contain every word typed (any case). */
export function filterServices(rows: ServiceRow[], query: string, state: StateFilter): ServiceRow[] {
  const words = query.toLowerCase().split(/\s+/).map(w => w.replace(/^@/, '')).filter(Boolean)   // "@pay" finds the team handle
  return rows.filter(r => {
    if (state === 'active' && r.archived) return false
    if (state === 'archived' && !r.archived) return false
    if (words.length === 0) return true
    const hay = [r.serviceKey, r.name, r.ownerTeam?.name, r.ownerTeam?.handle, r.repository, r.projectCode].filter(Boolean).join('\n').toLowerCase()
    return words.every(w => hay.includes(w))
  })
}

/** The reason Add or Save is off, in words; null when the form is complete. The key is asked for only when adding: it never changes afterwards. */
export function serviceProblem(d: ServiceDraft, isNew: boolean): string | null {
  if (isNew) {
    if (!d.key.trim()) return 'Enter a key for the service.'
    if (!KEY_PATTERN.test(d.key.trim())) return `A key is 1 to ${MAX_KEY} letters, digits, ".", "_" or "-".`
  }
  if (!d.name.trim()) return 'Enter a name for the service.'
  if (d.name.trim().length > MAX_NAME) return `A name is at most ${MAX_NAME} characters.`
  if (d.repository.trim().length > MAX_REPOSITORY) return `A repository is at most ${MAX_REPOSITORY} characters.`
  if (d.projectCode.trim().length > MAX_PROJECT_CODE) return `A project code is at most ${MAX_PROJECT_CODE} characters.`
  return null
}

/** Whether the draft differs from the saved row (trimmed, as the server stores it). */
export const isDirty = (d: ServiceDraft, r: ServiceRow): boolean => {
  const o = draftOf(r)
  return d.name.trim() !== o.name || d.ownerTeamId !== o.ownerTeamId || d.repository.trim() !== o.repository || d.projectCode.trim() !== o.projectCode
}

/** The PATCH body: an empty string clears the owner team, repository or project code (a missing field would leave it). The key is never sent. */
export const patchBody = (d: ServiceDraft) => ({ name: d.name.trim(), ownerTeamId: d.ownerTeamId, repository: d.repository.trim(), projectCode: d.projectCode.trim() })

/** The POST body: optional fields are left out when empty. */
export const createBody = (d: ServiceDraft) => ({
  serviceKey: d.key.trim(), name: d.name.trim(), ownerTeamId: d.ownerTeamId || null, repository: d.repository.trim() || null, projectCode: d.projectCode.trim() || null,
})

/** The owner-team picker: "No owner team", then the workspace's teams by name, plus the current owner when it is not in the list. */
export function teamOptions(teams: TeamChoice[], current: TeamRef | null): { value: string; label: string }[] {
  const list = current && !teams.some(t => t.id === current.id) ? [...teams, current] : teams
  return [{ value: NO_TEAM, label: 'No owner team' }, ...[...list].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })).map(t => ({ value: t.id, label: `${t.name} (@${t.handle})` }))]
}

/** Counts above the table. */
export const summary = (rows: ServiceRow[]) => ({ active: rows.filter(r => !r.archived).length, archived: rows.filter(r => r.archived).length })

/** What archiving does to the things that use the service, in words for the confirmation. */
export function archiveImpact(r: Pick<ServiceRow, 'products' | 'deployments'>): string {
  const n = (c: number, one: string, many: string) => `${c} ${c === 1 ? one : many}`
  if (r.products === 0 && r.deployments === 0) return 'Nothing uses it yet.'
  return `It stays on the ${[r.products ? n(r.products, 'product', 'products') : '', r.deployments ? n(r.deployments, 'deployment', 'deployments') : ''].filter(Boolean).join(' and ')} that use it, but cannot be linked to new products or deployed.`
}
