import type { NamedRef } from '../api.ts'
import { segmentLabel } from '../mgmt/clientsLogic.ts'

/** The pure parts of client dates (REOS-119, RT4, D47): what the API returns for a release, how a row reads, and what an edit sends. Tested without a browser. */
export interface ClientFreezeNote { id: string; name: string; kind: string; startsAt: string; endsAt: string }
export interface ClientDateRow {
  clientId: string; name: string; segment: 'Standard' | 'OnDemand'; date: string; wave: 'Standard' | 'On-Demand'
  offeredDate: string | null; chosenDate: string | null; chosenBy: NamedRef | null; chosenAt: string | null
  scheduled: boolean; archived: boolean; version: number; beforeStandardDate: boolean; freezes: ClientFreezeNote[]
}
export interface ClientSchedule {
  trainId: string; kind: 'Cert' | 'Production' | null; typeName: string | null; standardDate: string; offsetDays: number; open: boolean; note: string | null
  rows: ClientDateRow[]; unscheduled: number; warnings: number
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** A calendar date as "Fri 5 Mar 2027". It is a date, not an instant: no zone is applied (D44 labels instants; a calendar date is the same everywhere). */
export function dateText(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number)
  const day = new Date(Date.UTC(y, m - 1, d))
  return `${WEEKDAYS[day.getUTCDay()]} ${d} ${MONTHS[m - 1]} ${y}`
}

export const kindLabel = (kind: ClientSchedule['kind']): string => (kind === 'Cert' ? 'CERT' : kind ?? '')

/** Days from one calendar date to another (positive when `to` is later). */
export function daysBetween(from: string, to: string): number {
  const t = (s: string) => Date.UTC(Number(s.slice(0, 4)), Number(s.slice(5, 7)) - 1, Number(s.slice(8, 10)))
  return Math.round((t(to) - t(from)) / 86_400_000)
}

/** "+7 days" / "Standard date" / "2 days before": how a date sits against the Standard date, in words. */
export function againstStandard(date: string, standard: string): string {
  const n = daysBetween(standard, date)
  if (n === 0) return 'Standard date'
  return n > 0 ? `+${n} ${n === 1 ? 'day' : 'days'}` : `${-n} ${n === -1 ? 'day' : 'days'} before`
}

/** The advisories for a row, in words (the Advisories style: shown beside the date, never blocking). */
export function advisories(r: Pick<ClientDateRow, 'freezes' | 'beforeStandardDate'>, standard: string): string[] {
  return [
    ...r.freezes.map(f => `${f.kind === 'Chill' ? 'Chill' : 'Freeze'} window "${f.name}" covers this date`),
    ...(r.beforeStandardDate ? [`Earlier than the Standard date (${dateText(standard)})`] : []),
  ]
}

/** Where a row's date comes from, for people who cannot see the colour: the chosen date, the offered one, or the Standard date itself. */
export function dateSource(r: Pick<ClientDateRow, 'segment' | 'chosenDate' | 'scheduled'>): string {
  if (r.segment === 'Standard') return 'Standard date'
  if (r.chosenDate) return 'Chosen by the client'
  return r.scheduled ? 'Offered' : 'Offered (default)'
}

/** The reason Save is off, in words; null when the dates can be sent. A blank chosen date means "none". */
export function dateProblem(chosen: string, offered: string, standard: string): string | null {
  const valid = (s: string) => /^\d{4}-\d{2}-\d{2}$/.test(s) && !Number.isNaN(Date.parse(s + 'T00:00:00Z')) && new Date(s + 'T00:00:00Z').toISOString().startsWith(s)
  if (chosen && !valid(chosen)) return 'Enter the chosen date as a calendar date.'
  if (offered && !valid(offered)) return 'Enter the offered date as a calendar date.'
  if (chosen && chosen < standard) return `A chosen date cannot be earlier than the Standard date (${dateText(standard)}).`
  if (offered && offered < standard) return `An offered date cannot be earlier than the Standard date (${dateText(standard)}).`
  return null
}

/** What a save sends: only the dates that changed. A cleared chosen date is the empty string (the API's word for "none"). Null when nothing changed. */
export function datePatch(row: Pick<ClientDateRow, 'chosenDate' | 'offeredDate'>, chosen: string, offered: string): { chosenDate?: string; offeredDate?: string } | null {
  const body: { chosenDate?: string; offeredDate?: string } = {}
  if (chosen !== (row.chosenDate ?? '')) body.chosenDate = chosen
  if (offered && offered !== row.offeredDate) body.offeredDate = offered
  return Object.keys(body).length ? body : null
}

/** The line above the table: how many clients, split by segment, and what the offset is. */
export function summaryText(s: ClientSchedule): string {
  const std = s.rows.filter(r => r.segment === 'Standard').length
  const od = s.rows.length - std
  const parts = [std > 0 ? `${std} Standard` : null, `${od} On-Demand`].filter(Boolean)
  return `${s.rows.length} ${s.rows.length === 1 ? 'client' : 'clients'}: ${parts.join(', ')}`
}

/** How a segment is written in the table, for the chip. */
export const segmentWord = (segment: string): string => segmentLabel(segment)
