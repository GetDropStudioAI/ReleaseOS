import type { Tone } from '../ui/tone.ts'
import type { ApiToken } from '../tokens/tokensLogic.ts'
import type { Subscription } from '../webhooks/webhooksLogic.ts'

/**
 * The pure parts of the Integrations page (REOS-165): the health summary of API tokens and webhooks, the word and tone of each webhook's delivery health, and the quickstart text.
 * The page adds no endpoint; everything here is computed from what GET /api-tokens and GET /webhooks already return. Tested without a browser.
 */
export type TabId = 'overview' | 'tokens' | 'webhooks' | 'docs'
export const TABS: { id: TabId; label: string }[] = [
  { id: 'overview', label: 'Overview' }, { id: 'tokens', label: 'API tokens' }, { id: 'webhooks', label: 'Webhooks' }, { id: 'docs', label: 'Developer docs' },
]

/** A token that expires within this many days is called out on the overview. */
export const EXPIRING_DAYS = 14
const DAY = 86_400_000

export interface TokenSummary { active: number; expiringSoon: number; expired: number; revoked: number; neverUsed: number; nextExpiry: string | null }
export function tokenSummary(tokens: ApiToken[], now: Date): TokenSummary {
  const t = now.getTime()
  const live = tokens.filter(x => x.state === 'Active')
  const soon = live.filter(x => x.expiresAt !== null && Date.parse(x.expiresAt) - t <= EXPIRING_DAYS * DAY)
  const expiries = live.filter(x => x.expiresAt !== null).map(x => x.expiresAt as string).sort()
  return {
    active: live.length, expiringSoon: soon.length, expired: tokens.filter(x => x.state === 'Expired').length, revoked: tokens.filter(x => x.state === 'Revoked').length,
    neverUsed: live.filter(x => x.lastUsedAt === null).length, nextExpiry: expiries[0] ?? null,
  }
}

export interface WebhookSummary { total: number; active: number; paused: number; pending: number; deadLetters: number }
export function webhookSummary(subs: Subscription[]): WebhookSummary {
  return {
    total: subs.length, active: subs.filter(s => s.status === 'Active').length, paused: subs.filter(s => s.status !== 'Active').length,
    pending: subs.reduce((n, s) => n + s.pending, 0), deadLetters: subs.reduce((n, s) => n + s.deadLetters, 0),
  }
}

export interface Health { word: 'Healthy' | 'Queued' | 'Paused' | 'Failing'; tone: Tone }
/** One webhook's delivery health. Failing (dead letters since the last good delivery) outranks Paused, which outranks Queued; the word is always shown beside the dot. */
export function subscriptionHealth(s: Pick<Subscription, 'status' | 'pending' | 'deadLetters'>): Health {
  if (s.deadLetters > 0) return { word: 'Failing', tone: 'danger' }
  if (s.status !== 'Active') return { word: 'Paused', tone: 'warning' }
  if (s.pending > 0) return { word: 'Queued', tone: 'warning' }
  return { word: 'Healthy', tone: 'success' }
}
/** What a webhook's health means, for the table's second line. */
export function healthDetail(s: Pick<Subscription, 'status' | 'pending' | 'deadLetters'>): string {
  const parts: string[] = []
  if (s.deadLetters > 0) parts.push(`${s.deadLetters} dead-lettered`)
  if (s.pending > 0) parts.push(`${s.pending} waiting to be delivered`)
  if (s.status !== 'Active') parts.push('no new events are queued while paused')
  return parts.length ? parts.join(', ') : 'No failed or waiting deliveries'
}

/** Webhooks that need a person first: failing, then paused, then queued, then by name. */
export const byHealth = (subs: Subscription[]): Subscription[] => {
  const rank = { Failing: 0, Paused: 1, Queued: 2, Healthy: 3 } as const
  return [...subs].sort((a, b) => rank[subscriptionHealth(a).word] - rank[subscriptionHealth(b).word] || a.name.localeCompare(b.name))
}

/** The overall line for the metric card: the worst webhook, or a plain statement when there are none. */
export function overallHealth(subs: Subscription[]): { value: string; detail: string; tone: Tone } {
  if (subs.length === 0) return { value: 'None', detail: 'No webhooks yet', tone: 'neutral' }
  const w = webhookSummary(subs)
  if (w.deadLetters > 0) return { value: 'Failing', detail: `${w.deadLetters} ${w.deadLetters === 1 ? 'delivery' : 'deliveries'} dead-lettered`, tone: 'danger' }
  if (w.pending > 0) return { value: 'Queued', detail: `${w.pending} waiting to be delivered`, tone: 'warning' }
  return { value: 'Healthy', detail: 'Every delivery has been made', tone: 'success' }
}

/** The documents an integrator reads, with where they are. The OpenAPI document is served by the app itself, without a token; the rest are in the repository. */
export const DOCS: { title: string; path: string; text: string }[] = [
  { title: 'Developer guide', path: 'docs/DEVELOPERS.md', text: 'Quickstart, authentication with API tokens, conventions, the event catalogue, verifying a webhook, retries and the replay window.' },
  { title: 'Public API v1', path: 'docs/API_VERSIONING.md', text: 'What the API is, scopes, versioning, deprecation and the changelog.' },
  { title: 'Outbound webhooks', path: 'docs/WEBHOOKS.md', text: 'Headers, signing, delivery, dead letters and operator settings.' },
  { title: 'Receiver samples', path: 'docs/samples/webhooks/', text: 'C# (Verify.cs), JavaScript (verify.mjs) and Python (verify.py), each tested in CI against what the real sender produces.' },
]
export const OPENAPI_PATH = '/api/public/v1/openapi.json'

/** The first call a new integrator makes. `origin` is where this page is served from; the token is read from the environment, never typed into a command line. */
export const quickstart = (origin: string): string =>
  `export RELEASEOS_TOKEN=rt_...   # the token you made on the API tokens tab\ncurl -s -H "Authorization: Bearer $RELEASEOS_TOKEN" ${origin}/api/public/v1/me`
