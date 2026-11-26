import type { Tone } from '../ui/tone.ts'

/**
 * The pure parts of the Webhooks screen (REOS-164, D61): the rows the API returns, the words for statuses and results, why an action is off, the events picker (a native
 * multi-select, because the rules forbid checkboxes and switches), the checks that keep the form honest, and what each confirmation says. Tested without a browser.
 */
export interface WebhookEvent { key: string; name: string; description: string }
export type SubscriptionStatus = 'Active' | 'Paused'
export interface Subscription {
  id: string; name: string; host: string; eventTypes: string[]; status: SubscriptionStatus; secretVersion: number; hasSecret: boolean; secretRotatedAt: string | null
  createdAt: string; createdBy: string | null; lastChangedAt: string | null; pending: number; deadLetters: number; version: number
}
export interface AllowList { mode: 'open' | 'restricted' | 'closed'; hosts: string[]; allowPrivateTargets: boolean }
export interface WebhookLimits { maxSubscriptions: number; maxAttempts: number; timeoutSeconds: number; retentionDays: number; replayWindowSeconds: number }
export interface WebhookOverview { events: WebhookEvent[]; allEvents: string; allowList: AllowList; limits: WebhookLimits; subscriptions: Subscription[] }
/** The answer to create and to rotate: the secret is in it once and never again. */
export interface SubscriptionWithSecret { subscription: Subscription; secret: string }

export type DeliveryStatus = 'Pending' | 'Delivered' | 'DeadLetter'
export interface Delivery {
  id: string; subscriptionId: string; subscriptionName: string; eventId: string; eventType: string; status: DeliveryStatus; attemptCount: number; nextAttemptAt: string | null
  lastAttemptAt: string | null; lastStatusCode: number | null; lastError: string | null; deliveredAt: string | null; finishedAt: string | null; replayOfDeliveryId: string | null; createdAt: string; version: number
}
export interface Attempt { attemptNo: number; attemptedAt: string; statusCode: number | null; durationMs: number; error: string | null }
export interface DeliveryDetail { delivery: Delivery; payloadJson: string; attempts: Attempt[] }

export const ALL_EVENTS = '*'
/** The server's limit on a name (1..100). */
export const MAX_NAME = 100

export const statusTone = (s: SubscriptionStatus): Tone => (s === 'Active' ? 'success' : 'warning')
export const deliveryTone = (s: DeliveryStatus): Tone => (s === 'Delivered' ? 'success' : s === 'DeadLetter' ? 'danger' : 'warning')
/** The words on a delivery's chip: a pending one that has already failed is "Retrying", so the word says what happens next. */
export const deliveryWord = (d: Pick<Delivery, 'status' | 'attemptCount'>): string => (d.status === 'DeadLetter' ? 'Dead letter' : d.status === 'Pending' && d.attemptCount > 0 ? 'Retrying' : d.status)

/** The events a subscription asked for, in words: "All events", or the names in catalogue order. */
export function eventsText(sub: Pick<Subscription, 'eventTypes'>, catalogue: WebhookEvent[]): string {
  if (sub.eventTypes.includes(ALL_EVENTS)) return 'All events'
  const names = sub.eventTypes.map(k => catalogue.find(e => e.key === k)?.name ?? k)
  return names.length > 2 ? `${names.slice(0, 2).join(', ')} and ${names.length - 2} more` : names.join(', ')
}

/** The picker's value as the API wants it: "All events" on its own wins over any other choice. */
export const normaliseChoice = (picked: string[]): string[] => (picked.includes(ALL_EVENTS) ? [ALL_EVENTS] : picked)

/** The reason Add is off, in words; null when the form is complete. The server checks the address against the allow-list; this only keeps an obvious mistake from being sent. */
export function formProblem(name: string, url: string, events: string[], needUrl = true): string | null {
  if (!name.trim()) return 'Enter a name.'
  if (name.trim().length > MAX_NAME) return `A name is at most ${MAX_NAME} characters.`
  if (needUrl && !url.trim()) return 'Enter the address to post to, starting with https://.'
  if (url.trim() && !/^https:\/\//i.test(url.trim())) return 'The address must start with https://.'
  if (events.length === 0) return 'Choose at least one event.'
  return null
}

/** Newest first (the server sorts the same way); a pending delivery that is still being tried is not pushed down. */
export const sortDeliveries = (rows: Delivery[]): Delivery[] => [...rows].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt) || b.id.localeCompare(a.id))
export const sortSubscriptions = (rows: Subscription[]): Subscription[] => [...rows].sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id))

/** What the last try said, in words. */
export function resultText(d: Pick<Delivery, 'status' | 'lastStatusCode' | 'lastError' | 'attemptCount'>): string {
  if (d.attemptCount === 0) return 'Not tried yet'
  if (d.status === 'Delivered') return `Delivered${d.lastStatusCode ? ` (HTTP ${d.lastStatusCode})` : ''}`
  return d.lastError ?? (d.lastStatusCode ? `HTTP ${d.lastStatusCode}` : 'No answer')
}
export const attemptText = (a: Pick<Attempt, 'statusCode' | 'error'>): string => (a.error ?? (a.statusCode ? `HTTP ${a.statusCode}` : 'No answer'))
export const attemptsText = (n: number): string => `${n} ${n === 1 ? 'attempt' : 'attempts'}`

/** "2 pending · 1 dead-lettered", or "Nothing waiting". */
export function queueText(s: Pick<Subscription, 'pending' | 'deadLetters'>): string {
  const parts = [s.pending ? `${s.pending} pending` : '', s.deadLetters ? `${s.deadLetters} dead-lettered` : ''].filter(Boolean)
  return parts.length ? parts.join(' · ') : 'Nothing waiting'
}
export const footerText = (rows: Subscription[]): string => `${rows.length} ${rows.length === 1 ? 'subscription' : 'subscriptions'} · ${rows.filter(r => r.status === 'Active').length} active`
export const deliveryFooter = (rows: Delivery[], shown: number): string =>
  `${rows.length} ${rows.length === 1 ? 'delivery' : 'deliveries'}${rows.length >= shown ? ` (the latest ${shown})` : ''} · ${rows.filter(r => r.status === 'DeadLetter').length} dead-lettered`

/** The allow-list notice's words. */
export function allowListNotice(a: AllowList): { tone: Tone; title: string; text: string } {
  if (a.mode === 'closed') return { tone: 'warning', title: 'No receiver host is allowed yet', text: 'Nothing can be posted until an administrator names the hosts that may receive events (the Webhooks:Out:AllowedHosts setting). Until then adding a webhook is refused.' }
  if (a.mode === 'open') return { tone: 'info', title: 'Any public host may receive events', text: 'No host list is set, so every public https address is allowed. Private and local addresses are always refused unless an administrator allows them.' }
  return { tone: 'info', title: 'Events go only to allowed hosts', text: `Allowed: ${a.hosts.join(', ')}. An address on any other host is refused when it is saved, and again when an event is sent.` }
}

/** Why Test and Replay are off for a paused subscription. */
export const PAUSED_REASON = 'Resume the webhook first.'
export const replayReason = (d: Pick<Delivery, 'status'>, sub: Pick<Subscription, 'status'> | undefined): string | null =>
  !sub ? 'The webhook was deleted.' : d.status === 'Pending' ? 'This delivery is still being tried.' : sub.status !== 'Active' ? PAUSED_REASON : null

export const rotateImpact = (name: string): string =>
  `The current secret of ${name} stops verifying at once: every request is signed with the new one from now on, so a receiver still holding the old secret refuses them until you give it the new one. The new secret is shown once.`
export const deleteImpact = (s: Pick<Subscription, 'name' | 'pending'>): string =>
  `${s.name} and its whole delivery log are removed, and its secret is destroyed. ${s.pending ? `${s.pending} pending ${s.pending === 1 ? 'delivery is' : 'deliveries are'} dropped and will not be sent. ` : ''}This cannot be undone; add the webhook again to start over.`

/** The text fallback when the clipboard cannot be written (not a secure context, permission refused). */
export const COPY_FALLBACK = 'Copying did not work here. Select the secret above and copy it yourself.'

/** A payload shown pretty (it is stored compact so the signature is over exact bytes; the screen only reformats what it shows). */
export function prettyPayload(json: string): string {
  try { return JSON.stringify(JSON.parse(json), null, 2) } catch { return json }
}
