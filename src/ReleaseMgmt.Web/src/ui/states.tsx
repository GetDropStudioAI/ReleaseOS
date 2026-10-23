import { useId, type ReactNode } from 'react'
import { Inbox, LoaderCircle, Lock, type LucideIcon } from 'lucide-react'
import { Button } from './controls'
import { Notice } from './status'

/**
 * The states every screen shares (REOS-123, G1): loading, empty, error with Retry, no permission, read-only, 409 conflict, a disabled action with its reason,
 * stale or offline data, and a failed session save. A state is always words (never colour or an icon alone), and one that appears while the person is on
 * the page is a polite live region. Screens use these instead of their own "Loading…" and "✗ message" lines.
 */

/** Something is loading. `what` names it ("the release list") so the wording is specific and a screen reader hears what is coming. */
export function LoadingState({ what }: { what: string }) {
  return (
    <div className="os-state" role="status" aria-busy="true">
      <LoaderCircle className="os-state-icon os-spin" aria-hidden="true" size={20} strokeWidth={1.6} />
      <p className="os-state-title">Loading {what}…</p>
    </div>
  )
}

function Centered({ icon: Icon, title, children, action, alert }: { icon: LucideIcon; title: string; children?: ReactNode; action?: ReactNode; alert?: boolean }) {
  return (
    <div className="os-state" role={alert ? 'alert' : undefined}>
      <Icon className="os-state-icon" aria-hidden="true" size={20} strokeWidth={1.6} />
      <p className="os-state-title">{title}</p>
      {children && <p className="os-state-body">{children}</p>}
      {action && <div className="os-state-action">{action}</div>}
    </div>
  )
}

/** Nothing to show yet, and what to do about it ("No releases yet. Import releases from a CSV."). */
export function EmptyState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return <Centered icon={Inbox} title={title} action={action}>{children}</Centered>
}

/** The request failed: what failed, the server's reason, and Retry. Announced at once. */
export function ErrorState({ what, message, onRetry }: { what: string; message: string; onRetry?: () => void }) {
  return (
    <Notice tone="danger" title={`${what} could not be loaded`} live action={onRetry && <Button onClick={onRetry}>Retry</Button>}>{message}</Notice>
  )
}

/** The person's role does not allow this page or action; says whose decision it is. */
export function NoPermissionState({ what, who }: { what: string; who: string }) {
  return <Centered icon={Lock} title={`You cannot ${what}`}>This is limited to {who}. Ask one of them, or an administrator, if you need access.</Centered>
}

/** The page is shown but cannot be changed by this person. */
export function ReadOnlyNotice({ children }: { children?: ReactNode }) {
  return <Notice tone="info" title="Read only">{children ?? 'You can read this page but not change it.'}</Notice>
}

/** 409: someone else changed the row. Says what it is now and offers the two ways out; the person's draft is kept by the screen. */
export function ConflictNotice({ by, at, onUseTheirs, onOverwrite }: { by?: string; at?: string; onUseTheirs: () => void; onOverwrite?: () => void }) {
  return (
    <Notice tone="warning" title="Someone else changed this first" live
      action={<span className="os-state-buttons"><Button onClick={onUseTheirs}>Use theirs</Button>{onOverwrite && <Button variant="primary" onClick={onOverwrite}>Overwrite</Button>}</span>}>
      {by ? `${by} saved a newer version${at ? ` at ${at}` : ''}.` : 'A newer version was saved.'} Your changes are still here.
    </Notice>
  )
}

/** A button that cannot be used right now and says why, in text next to it, where a screen reader reads it with the button. */
export function DisabledAction({ reason, children, ...rest }: { reason: string; children: string } & Omit<Parameters<typeof Button>[0], 'children' | 'disabled' | 'aria-describedby'>) {
  const id = useId()
  return (
    <span className="os-disabled-action">
      <Button {...rest} disabled aria-describedby={id}>{children}</Button>
      <span id={id} className="os-disabled-reason">{reason}</span>
    </span>
  )
}

/** The data on screen may be old: the connection dropped or a refresh failed. Names the age and offers a refresh. */
export function StaleBanner({ offline, since, onRefresh }: { offline?: boolean; since?: string; onRefresh?: () => void }) {
  return (
    <Notice tone="warning" title={offline ? 'Live updates are offline' : 'This page may be out of date'} live action={onRefresh && <Button onClick={onRefresh}>Refresh</Button>}>
      {since ? `Last updated ${since}.` : 'It has not refreshed recently.'}{offline ? ' Changes made by others will not appear until the connection returns.' : ''}
    </Notice>
  )
}

/** The person's draft or session state could not be saved; it stays on screen. */
export function SaveFailureNotice({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return <Notice tone="warning" title="Your draft could not be saved" live action={onRetry && <Button onClick={onRetry}>Try again</Button>}>{message}</Notice>
}
