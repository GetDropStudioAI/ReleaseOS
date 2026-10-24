import type { NamedRef } from '../api.ts'

/** The pure parts of clients (REOS-118, RT3, D47): the row the API returns, how a segment reads, and what a client form asks for. Tested without a browser. */
export type Segment = 'Standard' | 'OnDemand'
export interface ClientRow { id: string; name: string; segment: Segment; certEnvironment: NamedRef | null; archived: boolean; version: number; lastChangedAt?: string | null }
export interface EnvironmentChoice { id: string; name: string; archived: boolean }

export const SEGMENTS: { value: Segment; label: string }[] = [{ value: 'Standard', label: 'Standard' }, { value: 'OnDemand', label: 'On-Demand' }]
export const NO_ENVIRONMENT = ''

/** "On-Demand" for the stored "OnDemand"; the stored word is never shown. */
export const segmentLabel = (segment: string): string => (segment === 'OnDemand' ? 'On-Demand' : segment)

/** Active clients first, then by name; archived ones last. */
export const sortClients = (rows: ClientRow[]): ClientRow[] =>
  [...rows].sort((a, b) => Number(a.archived) - Number(b.archived) || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))

/** The reason Add or Save is off, in words; null when the form is complete. Only an On-Demand client has a CERT environment, and it must have one. */
export function clientProblem(name: string, segment: Segment, environmentId: string): string | null {
  if (!name.trim()) return 'Enter a name for the client.'
  if (segment === 'OnDemand' && !environmentId) return 'Choose the CERT environment of this On-Demand client.'
  return null
}

/** The environments a CERT picker offers: active ones, plus the one the client already has when it has been archived since (shown with its state in words). */
export function environmentOptions(envs: EnvironmentChoice[], currentId: string | null | undefined): { value: string; label: string }[] {
  const keep = envs.filter(e => !e.archived || e.id === currentId)
  return [{ value: NO_ENVIRONMENT, label: 'Choose an environment' }, ...keep.map(e => ({ value: e.id, label: e.archived ? `${e.name} (archived)` : e.name }))]
}

/** The request body for a save: an environment only for an On-Demand client; a Standard client sends none, which also clears the one it had. */
export function saveBody(name: string, segment: Segment, environmentId: string): { name: string; segment: Segment; certEnvironmentId: string } {
  return { name: name.trim(), segment, certEnvironmentId: segment === 'OnDemand' ? environmentId : '' }
}

/** Counts shown above the table: how many are active, and how they split between the segments. */
export function summary(rows: ClientRow[]): { active: number; standard: number; onDemand: number } {
  const live = rows.filter(r => !r.archived)
  return { active: live.length, standard: live.filter(r => r.segment === 'Standard').length, onDemand: live.filter(r => r.segment === 'OnDemand').length }
}
