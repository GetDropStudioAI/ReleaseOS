import type { Me } from '../api.ts'
import type { Tone } from '../ui/tone.ts'
import type { ChangeRow, NewChangeBody, ChangePatchBody } from './cabApi.ts'

/**
 * The pure parts of the change request screens (REOS-176; D56, D63): words, tones, who may do what, form checks and the request bodies. Tested without a browser.
 * Status and risk are always shown as a word (a chip has a dot and a label); colour only repeats it.
 */
export const TYPES = ['Standard', 'Normal', 'Emergency'] as const
export const STATUSES = ['Draft', 'Submitted', 'Approved', 'Rejected', 'Implemented', 'Closed', 'Cancelled'] as const

export const TYPE_HINT: Record<string, string> = {
  Standard: 'A pre-agreed, repeatable change.',
  Normal: 'Goes to the CAB for a decision before it is implemented.',
  Emergency: 'May be implemented before approval; a second person reviews it afterwards.',
}

/** Status tones: approved and closed read as settled, rejected and cancelled as stopped. */
export function statusTone(status: string): Tone {
  switch (status) {
    case 'Approved': case 'Closed': return 'success'
    case 'Implemented': return 'info'
    case 'Submitted': return 'warning'
    case 'Rejected': return 'danger'
    default: return 'neutral'
  }
}
export function riskTone(level: string): Tone {
  switch (level) { case 'Low': return 'success'; case 'Medium': return 'warning'; case 'High': case 'Critical': return 'danger'; default: return 'neutral' }
}
/** "High risk": the level and what it is, so a chip alone says more than a colour. */
export const riskWord = (level: string) => `${level} risk`

/** Reading is for everyone; raising, editing and moving a change is the Plan policy; deciding is CabDecide (the requester rule applies whatever the role). */
export const canPlan = (me: Pick<Me, 'roles'>) => me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
export const canDecide = (me: Pick<Me, 'roles'>) => canPlan(me) || me.roles.includes('GovernanceOfficer')

export interface Step { action: 'submit' | 'cancel' | 'implement' | 'close'; label: string }

/** The moves offered for a change in its status. A refusal by a rule (rollback note missing, Emergency not yet reviewed) comes back from the service in words. */
export function stepsFor(c: Pick<ChangeRow, 'status' | 'changeType'>): Step[] {
  switch (c.status) {
    case 'Draft': return [{ action: 'submit', label: 'Submit to the CAB' }, { action: 'cancel', label: 'Cancel change' }]
    case 'Submitted': return [...(c.changeType === 'Emergency' ? [{ action: 'implement', label: 'Implement now' } as Step] : []), { action: 'cancel', label: 'Cancel change' }]
    case 'Approved': return [{ action: 'implement', label: 'Mark implemented' }, { action: 'cancel', label: 'Cancel change' }]
    case 'Implemented': return [{ action: 'close', label: 'Close change' }]
    default: return []
  }
}

export const canDecideNow = (c: Pick<ChangeRow, 'status' | 'changeType'>) => c.status === 'Submitted' || (c.changeType === 'Emergency' && c.status === 'Implemented')
export const isRetroactive = (c: Pick<ChangeRow, 'status' | 'changeType'>) => c.changeType === 'Emergency' && c.status === 'Implemented'

/** The three separation-of-duties rules, in the words the service and the database use, so the screen can state them before anyone is refused. */
export const SEPARATION_RULES = [
  'You cannot approve or reject a change you requested. Deferring is allowed.',
  'The CAB chair cannot be the only approver of a change they raised; a second approver is needed.',
  'An Emergency change needs a review by a second person before it can be closed.',
]

/** Why this person cannot approve or reject this change right now, in words; null when nothing known to the screen stops them. The server still checks (the chair rule needs the meetings). */
export function selfApprovalReason(c: Pick<ChangeRow, 'requestedBy'>, me: Pick<Me, 'id'>): string | null {
  return c.requestedBy.id === me.id ? 'You requested this change, so you cannot approve or reject it. You can still defer.' : null
}

export function decisionTone(decision: string): Tone { return decision === 'Approved' ? 'success' : decision === 'Rejected' ? 'danger' : 'neutral' }

/** What the person typed. Dates are the wall-clock text of a datetime-local input in the display zone; `toUtc` converts. */
export interface ChangeForm { title: string; changeType: string; description: string; releaseId: string; goNoGoId: string; requesterId: string; start: string; end: string; rollbackNote: string }
export const NONE = ''
export const emptyForm = (requesterId: string): ChangeForm => ({ title: '', changeType: 'Normal', description: '', releaseId: NONE, goNoGoId: NONE, requesterId, start: '', end: '', rollbackNote: '' })
export const formOf = (c: ChangeRow, toLocal: (iso: string) => string): ChangeForm => ({
  title: c.title, changeType: c.changeType, description: c.description ?? '', releaseId: c.release?.id ?? NONE, goNoGoId: c.goNoGoDecisionId ?? NONE, requesterId: c.requestedBy.id,
  start: c.plannedStartAt ? toLocal(c.plannedStartAt) : '', end: c.plannedEndAt ? toLocal(c.plannedEndAt) : '', rollbackNote: c.rollbackNote ?? '',
})

/** The first thing wrong with the form, in words, or null. Mirrors the service's checks so the button can say why it is off; the service still has the last word. */
export function formProblem(f: ChangeForm): string | null {
  const t = f.title.trim()
  if (t.length === 0) return 'Give the change a title.'
  if (t.length > 200) return 'A title is at most 200 characters.'
  if (!!f.start !== !!f.end) return 'A planned window needs both a start and an end.'
  if (f.start && f.end && f.end <= f.start) return 'The planned window must end after it starts.'
  if (f.description.length > 4000 || f.rollbackNote.length > 4000) return 'The description and the rollback note are at most 4000 characters.'
  return null
}

const orNull = (s: string) => (s.trim() ? s.trim() : null)

export function newBody(f: ChangeForm, me: Pick<Me, 'id'>, toUtc: (local: string) => string): NewChangeBody {
  return {
    title: f.title.trim(), changeType: f.changeType, description: orNull(f.description), requestedByUserId: f.requesterId && f.requesterId !== me.id ? f.requesterId : null,
    releaseTrainId: orNull(f.releaseId), goNoGoDecisionId: orNull(f.goNoGoId), plannedStartAt: f.start ? toUtc(f.start) : null, plannedEndAt: f.end ? toUtc(f.end) : null, rollbackNote: orNull(f.rollbackNote),
  }
}

/** A patch carries only what changed; a field emptied is named in `clear` (null in a patch means "leave it"). */
export function patchBody(f: ChangeForm, c: ChangeRow, toUtc: (local: string) => string, toLocal: (iso: string) => string): ChangePatchBody {
  const was = formOf(c, toLocal), out: ChangePatchBody = {}, clear: string[] = []
  if (f.title.trim() !== was.title) out.title = f.title.trim()
  if (f.changeType !== was.changeType) out.changeType = f.changeType
  if (f.description !== was.description) { if (f.description.trim()) out.description = f.description; else clear.push('description') }
  if (f.releaseId !== was.releaseId) { if (f.releaseId) out.releaseTrainId = f.releaseId; else clear.push('releaseTrainId') }
  if (f.goNoGoId !== was.goNoGoId) { if (f.goNoGoId) out.goNoGoDecisionId = f.goNoGoId; else clear.push('goNoGoDecisionId') }
  if (f.rollbackNote !== was.rollbackNote) { if (f.rollbackNote.trim()) out.rollbackNote = f.rollbackNote; else clear.push('rollbackNote') }
  if (f.start !== was.start || f.end !== was.end) { if (f.start && f.end) { out.plannedStartAt = toUtc(f.start); out.plannedEndAt = toUtc(f.end) } else if (!f.start && !f.end) clear.push('window') }
  if (clear.length) out.clear = clear
  return out
}
export const isDirty = (f: ChangeForm, c: ChangeRow, toLocal: (iso: string) => string) => Object.keys(patchBody(f, c, x => x, toLocal)).length > 0

/** Counts for the list footer. */
export function summary(rows: ChangeRow[]): string {
  const waiting = rows.filter(r => r.status === 'Submitted').length
  return `${rows.length} change${rows.length === 1 ? '' : 's'}, ${waiting} waiting for the CAB`
}

/** The refusal's name for a guard that is a separation-of-duties rule, or null for any other. */
export const SEPARATION_GUARDS = ['ChangeSelfApproval', 'ChangeChairSoleApprover', 'ChangeEmergencyNeedsReview'] as const
export const isSeparationGuard = (g: string | undefined) => !!g && (SEPARATION_GUARDS as readonly string[]).includes(g)

/** The "Why this level" sentence: which rules applied and their sum, so the list explains the level in one line. */
export function riskSentence(r: NonNullable<ChangeRow['risk']>): string {
  const on = r.factors.filter(f => f.applied)
  if (on.length === 0) return `${r.level} risk: no rule applied (score 0).`
  return `${r.level} risk: ${on.map(f => `${f.label} (+${f.points})`).join(', ')}. Score ${r.score}.`
}
