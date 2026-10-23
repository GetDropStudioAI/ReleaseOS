/** The pure parts of the Audit history screen (REOS-101): the filter query, the derived outcome, event ids, the change diff and the copied record. */

export interface AuditItem {
  id: number; occurredAt: string; actorUserId: string | null; actorName: string | null; releaseTrainId: string | null; trainTitle: string | null
  entityType: string; entityId: string; action: string; beforeJson: string | null; afterJson: string | null
}
/** `search` is matched against the event name (the server's `action` filter, a case-insensitive contains); `from` and `to` are UTC calendar days (D44). */
export interface Filters { train: string; entity: string; actor: string; search: string; from: string; to: string }
export const EMPTY_FILTERS: Filters = { train: '', entity: '', actor: '', search: '', from: '', to: '' }

export function auditQuery(f: Filters): URLSearchParams {
  const q = new URLSearchParams()
  if (f.train) q.set('train', f.train)
  if (f.entity) q.set('entity', f.entity)
  if (f.actor) q.set('actor', f.actor)
  if (f.search.trim()) q.set('action', f.search.trim())
  if (f.from) q.set('from', f.from)
  if (f.to) q.set('to', f.to)
  return q
}
export const anyFilter = (f: Filters) => Object.values(f).some(v => v.trim() !== '')

/** "EVT-000123", as the design writes an event id. */
export const eventId = (id: number) => `EVT-${String(id).padStart(6, '0')}`

/** "Certify stage gate": the action, then the entity type in words. */
export function eventTitle(entityType: string, action: string): string {
  const words = entityType.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase()
  return `${action} ${words}`
}

export type Outcome = 'Failed' | 'Pending' | 'Approved' | 'Recorded'
/**
 * The design's Outcome chip. The log stores what happened, not whether it "passed", so the outcome is read from the action's name: a failure, abort or
 * decertification is Failed, a request is Pending, an approval, certification, grant or waiver is Approved, and everything else is Recorded.
 */
export function outcomeOf(action: string): Outcome {
  if (/^(fail|abort|decertify|reject|rollback|cancel|revoke|remove|delete)/i.test(action)) return 'Failed'
  if (/^(request|raise|open)/i.test(action)) return 'Pending'
  if (/^(approve|certify|grant|renew|waive|accept)/i.test(action)) return 'Approved'
  return 'Recorded'
}

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
export function parseJson(json: string | null): unknown { if (json === null) return undefined; try { return JSON.parse(json) } catch { return json } }
const show = (v: unknown) => (typeof v === 'string' ? JSON.stringify(v) : JSON.stringify(v) ?? 'undefined')

export interface DiffLine { kind: 'removed' | 'added' | 'same'; key: string; text: string }
/**
 * The change as lines: for each top-level key, a removed line and an added line when the value differs, one plain line when it does not. A side that is
 * not an object (a string, or nothing recorded) is shown whole. Each line has a sign and a word, so it reads the same without colour.
 */
export function diffLines(before: unknown, after: unknown): DiffLine[] {
  const lines: DiffLine[] = []
  const b = isObj(before) ? before : undefined, a = isObj(after) ? after : undefined
  if (b === undefined && a === undefined) {
    if (before !== undefined) lines.push({ kind: 'removed', key: '', text: show(before) })
    if (after !== undefined) lines.push({ kind: 'added', key: '', text: show(after) })
    return lines
  }
  for (const k of new Set([...Object.keys(b ?? {}), ...Object.keys(a ?? {})])) {
    const inB = b !== undefined && k in b, inA = a !== undefined && k in a
    if (inB && inA && show(b![k]) === show(a![k])) lines.push({ kind: 'same', key: k, text: `${k}: ${show(a![k])}` })
    else {
      if (inB) lines.push({ kind: 'removed', key: k, text: `${k}: ${show(b![k])}` })
      if (inA) lines.push({ kind: 'added', key: k, text: `${k}: ${show(a![k])}` })
    }
  }
  return lines
}

/** The machine-readable record "Copy event record" puts on the clipboard: every column of the event, the JSON sides parsed, and the derived outcome. */
export function eventRecord(e: AuditItem) {
  return {
    id: e.id, eventId: eventId(e.id), occurredAt: e.occurredAt, actor: { userId: e.actorUserId, name: e.actorName }, release: { id: e.releaseTrainId, title: e.trainTitle },
    entity: { type: e.entityType, id: e.entityId }, action: e.action, outcome: outcomeOf(e.action), before: parseJson(e.beforeJson) ?? null, after: parseJson(e.afterJson) ?? null,
  }
}

/** The event id named by a URL's query string (`?event=123`), or null. */
export function eventFromSearch(search: string): number | null {
  const v = new URLSearchParams(search).get('event')
  return v !== null && /^\d{1,15}$/.test(v) ? Number(v) : null
}
export const auditPath = (event: number | null) => (event === null ? '/audit' : `/audit?event=${event}`)

export const PAGE_SIZE = 25
/** Above this many matching events the export asks first; the server cuts a file at 50 000 rows (Audit:CsvMaxRows) and says so in the file. */
export const LARGE_EXPORT = 10_000
export const SERVER_EXPORT_MAX = 50_000
