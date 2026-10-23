import type { ReactNode } from 'react'
import { CircleAlert, CircleCheck, CircleX, Info, type LucideIcon } from 'lucide-react'
import { chipTone, type Tone } from './tone.ts'

/** Status chip: a dot and the status word on a tinted ground. `tone` defaults to the tone of the word itself. */
export function StatusChip({ children, tone }: { children: string; tone?: Tone }) {
  return <span className={`os-chip ${tone ?? chipTone(children)}`}><span className="os-chip-dot" aria-hidden="true" />{children}</span>
}

/** A count next to a navigation item (Approvals 4). Nothing at zero; the accessible name says what is counted. */
export function CountBadge({ count, label }: { count: number; label: string }) {
  if (count <= 0) return null
  return <span className="os-badge" aria-label={`${count} ${label}`}>{count}</span>
}

const NOTICE_ICON: Record<Tone, LucideIcon> = { success: CircleCheck, info: Info, warning: CircleAlert, danger: CircleX, neutral: Info }

/**
 * Operational notice: an icon, a title, a description and an optional action ("Review blockers →"). `live` makes it a polite live region, for
 * notices that appear while the person is on the page; a notice that is there on arrival stays silent.
 */
export function Notice({ tone, title, children, action, live }: { tone: Tone; title: string; children?: ReactNode; action?: ReactNode; live?: boolean }) {
  const Icon = NOTICE_ICON[tone]
  return (
    <div className={`os-notice ${tone}`} role={live ? 'status' : undefined}>
      <Icon className="os-notice-icon" aria-hidden="true" size={18} strokeWidth={1.6} />
      <div className="os-notice-text"><p className="os-notice-title">{title}</p>{children && <div className="os-notice-body">{children}</div>}</div>
      {action && <div className="os-notice-action">{action}</div>}
    </div>
  )
}

/** Progress bar (readiness, canary traffic). `value` is 0 to 1; the label names what is measured and the percentage is announced. */
export function ProgressBar({ value, label, tone = 'success' }: { value: number; label: string; tone?: Tone }) {
  const pct = Math.round(Math.min(1, Math.max(0, value)) * 100)
  return (
    <div className="os-progress" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct}>
      <div className={`os-progress-fill ${tone}`} style={{ width: `${pct}%` }} />
    </div>
  )
}
