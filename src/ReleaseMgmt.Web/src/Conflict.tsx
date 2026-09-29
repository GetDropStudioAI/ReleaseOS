import { useEffect, useState } from 'react'
import { ApiError, getOwners, type Owner } from './api'
import { day } from './format'
import { fmtDayTime, zoneAbbr } from './time'

/** A 409 from the API: somebody else saved first. The body carries the row as it is now (`current`). */
export const isConflict = (e: unknown): e is ApiError => e instanceof ApiError && e.status === 409

let people: Promise<Owner[]> | undefined
const whoIs = async (id: unknown) => (typeof id === 'string' ? (await (people ??= getOwners().catch(() => []))).find(o => o.id === id)?.name : undefined) ?? 'someone else'

const zoneTime = (iso: string) => `${fmtDayTime(iso)} ${zoneAbbr(iso)}`

/** Plain words for what the row looks like now, whatever kind of row it is. */
function describe(current: Record<string, unknown> | undefined): string {
  if (!current) return 'It has changed.'
  if (typeof current.startsAt === 'string' && typeof current.endsAt === 'string') return `The window is now ${zoneTime(current.startsAt)} to ${zoneTime(current.endsAt)} (version ${current.version}).`
  if (typeof current.status === 'string') return `It is now ${current.status} (version ${current.version}).`
  if (typeof current.isCompleted === 'boolean') return `The task is now ${current.isCompleted ? 'done' : 'open'} (version ${current.version}).`
  if (typeof current.targetReleaseDate === 'string') return `The target is now ${day(current.targetReleaseDate.slice(0, 10))} (version ${current.version}).`
  return `It is now at version ${current.version}.`
}

/**
 * Inline conflict notice (no modal): says who changed it and what it is now, and that the user's change was NOT applied.
 * The caller keeps its draft and offers the next step through <c>children</c> (for example "Overwrite with mine").
 */
export function Conflict({ error, what, children }: { error: ApiError; what: string; children?: React.ReactNode }) {
  const current = (error.body?.current ?? undefined) as Record<string, unknown> | undefined
  const [who, setWho] = useState('someone else')
  useEffect(() => { void whoIs(current?.lastChangedByUserId).then(setWho) }, [current?.lastChangedByUserId])
  return (
    <div className="conflict bad" role="alert">
      <p>✗ <strong>{who}</strong> changed this {what} before your change was saved. Your change was not applied. {describe(current)}</p>
      {children && <p className="conflict-actions">{children}</p>}
    </div>
  )
}
