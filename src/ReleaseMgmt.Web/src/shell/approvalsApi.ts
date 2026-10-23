import { get } from '../api.ts'
import { browserZone, localClock } from './clock.ts'

/** GET /approvals (REOS-94): the queue behind the Approvals page and the sidebar badge. */
export interface ApprovalItem {
  kind: 'CertifyGate' | 'ApproveWaiver' | 'GrantOverride' | 'RecordGoNoGo' | 'CloseCondition'
  id: string; title: string; detail: string | null; trainId: string; trainTitle: string; dueOn: string | null; dueAt: string | null; overdue: boolean; gateId: string | null
}
export interface ApprovalQueue { counts: { certify: number; waivers: number; overrides: number; goNoGo: number; conditions: number; total: number }; items: ApprovalItem[] }

export const getApprovals = () => get<ApprovalQueue>('/api/v1/approvals')

export const KIND: Record<ApprovalItem['kind'], string> = {
  CertifyGate: 'Certify', ApproveWaiver: 'Approve waiver', GrantOverride: 'Grant override', RecordGoNoGo: 'Go/No-Go', CloseCondition: 'Close condition',
}
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "28 Oct 2026" for a due date; "30 Oct 2026, 02:00 UTC" plus the person's local time for a due time (D44). */
export function dueText(i: Pick<ApprovalItem, 'dueOn' | 'dueAt'>, zone = browserZone()): { utc: string; local: string | null } {
  if (i.dueAt) {
    const d = new Date(i.dueAt)
    const pad = (n: number) => String(n).padStart(2, '0')
    return { utc: `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}, ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`, local: localClock(d.getTime(), zone) }
  }
  if (i.dueOn) { const [y, m, day] = i.dueOn.split('-').map(Number); return { utc: `${day} ${MONTHS[m - 1]} ${y}`, local: null } }
  return { utc: '', local: null }
}

