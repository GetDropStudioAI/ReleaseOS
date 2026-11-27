import type { Conflict, ConflictType } from './conflictsApi.ts'

/**
 * The pure parts of the conflict views (REOS-180; D56, D9): words, counts, and the reason rule. A conflict warns and never blocks; approving over one needs a reason. Tested without a browser.
 * A conflict is always shown as words (the rule that fired and what it found) with a shape marker; colour only repeats it.
 */
export const TYPE_NAME: Record<ConflictType, string> = {
  Freeze: 'Freeze or chill window', Blackout: 'Blackout day', ChangeOverlap: 'Another change', ReleaseWindow: 'Another release window',
}
/** The fixed order the panel groups conflicts in, the same as the server's. */
export const TYPE_ORDER: ConflictType[] = ['Freeze', 'Blackout', 'ChangeOverlap', 'ReleaseWindow']

/** "No conflicts", "1 conflict", "3 conflicts": the count in words (a chip also carries it). */
export const conflictCount = (n: number) => (n === 0 ? 'No conflicts' : n === 1 ? '1 conflict' : `${n} conflicts`)

/** The marker beside a change that has conflicts: a diamond shape and the count in words, never a colour on its own. */
export const marker = (n: number) => (n === 0 ? '◆' : '◆!')

export function groupByType(conflicts: Conflict[]): { type: ConflictType; label: string; items: Conflict[] }[] {
  return TYPE_ORDER.map(type => ({ type, label: TYPE_NAME[type], items: conflicts.filter(c => c.type === type) })).filter(g => g.items.length > 0)
}

/** The same length the server asks for (ChangeConflictRules.MinReasonLength); the server's number arrives with the rules, this is only the fallback before they load. */
export const DEFAULT_MIN_REASON = 20

/** True when a verdict of Approved over these conflicts still needs a (longer) reason. Deferring and rejecting never do. */
export const needsReason = (decision: string, conflicts: Conflict[], rationale: string, min = DEFAULT_MIN_REASON) =>
  decision === 'Approved' && conflicts.length > 0 && rationale.trim().length < min

export const reasonHint = (conflicts: Conflict[], min = DEFAULT_MIN_REASON) =>
  `This change has ${conflictCount(conflicts.length).toLowerCase()}. A conflict does not block approval, but approving over one needs a reason of at least ${min} characters.`

/** The UTC days (yyyy-MM-dd) a window touches; a window that ends at midnight does not touch the next day. */
export function daysOf(startsAt: string, endsAt: string): string[] {
  const first = new Date(startsAt); const last = Date.parse(endsAt) - 1
  const out: string[] = []
  for (let t = Date.UTC(first.getUTCFullYear(), first.getUTCMonth(), first.getUTCDate()); t <= last; t += 86400000) out.push(new Date(t).toISOString().slice(0, 10))
  return out
}
