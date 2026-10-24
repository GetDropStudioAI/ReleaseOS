import type { NamedRef } from '../api.ts'

/** The pure parts of release types (REOS-116, D46, D48): the row the API returns, how a type reads beside a Beta number, and what a new release asks for. Tested without a browser. */
export interface ReleaseTypeRow { id: string; name: string; sortOrder: number; defaultTemplate: NamedRef | null; archived: boolean; releases: number; version: number; isBeta: boolean }

export const BETA_NUMBERS = [1, 2, 3]

/** "Beta 2" for a numbered release, the type's name otherwise, an empty string when no type is recorded. */
export const typeLabel = (type: { name: string } | null | undefined, betaNumber?: number | null): string =>
  !type ? '' : betaNumber ? `${type.name} ${betaNumber}` : type.name

/** The types a new release can take: not archived, in the workspace's order. */
export const activeTypes = (rows: ReleaseTypeRow[]): ReleaseTypeRow[] =>
  rows.filter(r => !r.archived).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))

/** The reason Create is off for the type part of the New train form, in words; null when it is fine. A workspace with no types asks for none. */
export function typeProblem(types: ReleaseTypeRow[], typeId: string, betaNumber: string): string | null {
  const live = activeTypes(types)
  if (live.length === 0) return null
  if (!typeId) return 'Choose a release type.'
  const type = live.find(t => t.id === typeId)
  if (!type) return 'Choose a release type.'
  if (type.isBeta && !BETA_NUMBERS.includes(Number(betaNumber))) return `Choose the number of this ${type.name} release, 1 to 3.`
  return null
}

/** What a release of this type starts from when no template is chosen: the type's default template, or nothing. */
export const startsFrom = (type: ReleaseTypeRow | undefined): string | null => type?.defaultTemplate?.name ?? null

/** The table's sort: workspace order, archived last. */
export const sortTypes = (rows: ReleaseTypeRow[]): ReleaseTypeRow[] =>
  [...rows].sort((a, b) => Number(a.archived) - Number(b.archived) || a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))

/** The one reason a type cannot be archived or have its Beta setting changed, from what the table already knows; null when it can. */
export function betaLockReason(row: ReleaseTypeRow): string | null {
  return row.releases > 0 ? `${row.releases} ${row.releases === 1 ? 'release uses' : 'releases use'} this type, so it cannot become or stop being a Beta type.` : null
}
