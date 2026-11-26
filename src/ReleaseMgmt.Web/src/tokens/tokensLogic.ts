import type { Tone } from '../ui/tone.ts'
import { timeText } from '../utcTime.ts'

/**
 * The pure parts of the API tokens screen (REOS-162): the token the API returns, the access presets (a native select, because the rules forbid checkboxes),
 * the expiry choices turned into a UTC instant from a clock, the state tone, why Make is off, and the sort. Tested without a browser.
 */
export type TokenState = 'Active' | 'Expired' | 'Revoked'
export interface ApiToken {
  id: string; name: string; tokenPrefix: string; scopes: string[]; expiresAt: string | null; state: TokenState
  ownerUserId: string; ownerName: string | null; createdAt: string; rotatedAt: string | null; lastUsedAt: string | null; revokedAt: string | null; revokedBy: string | null; version: number
}
export interface TokenList { scopes: string[]; tokens: ApiToken[] }
/** The answer to make and to rotate: the secret is in it once and never again. */
export interface TokenWithSecret { token: ApiToken; secret: string }

/** The server's limit on a name (1..100). */
export const MAX_NAME = 100

/** The scopes the server knows, in the order it lists them. GET /api-tokens returns the live list; this one is what the screen offers until it arrives. */
export const KNOWN_SCOPES = ['read', 'write', 'approvals', 'deployments'] as const

export interface AccessPreset { id: string; label: string; scopes: string[] }
const preset = (id: string, words: string, scopes: string[]): AccessPreset => ({ id, label: `${words} (${scopes.join(', ')})`, scopes })
export const ACCESS_PRESETS: AccessPreset[] = [
  preset('read', 'Read', ['read']),
  preset('read-write', 'Read and write', ['read', 'write']),
  preset('read-approvals', 'Read and approvals', ['read', 'approvals']),
  preset('read-deployments', 'Read and deployments', ['read', 'deployments']),
  preset('read-write-approvals', 'Read, write and approvals', ['read', 'write', 'approvals']),
  preset('read-write-deployments', 'Read, write and deployments', ['read', 'write', 'deployments']),
  preset('everything', 'Everything', ['read', 'write', 'approvals', 'deployments']),
]
export const DEFAULT_PRESET = 'read'

/** The scopes of a preset id (a copy); an unknown id gives none, so Make stays off. */
export const scopesOfPreset = (id: string): string[] => [...(ACCESS_PRESETS.find(p => p.id === id)?.scopes ?? [])]
/** The preset a scope list equals (any order), or null. */
export const presetOfScopes = (scopes: string[]): string | null => {
  const key = [...scopes].sort().join(',')
  return ACCESS_PRESETS.find(p => [...p.scopes].sort().join(',') === key)?.id ?? null
}
/** Scopes in words: "read, write". */
export const scopesText = (scopes: string[]): string => (scopes.length ? scopes.join(', ') : 'none')

/** Presets that name a scope the server does not list, so a stale screen cannot ask for one (the server would refuse it). */
export const presetsOutside = (known: readonly string[]): string[] => ACCESS_PRESETS.filter(p => p.scopes.some(s => !known.includes(s))).map(p => p.id)

/** The presets the server can honour: those whose every scope is in the list it returned. */
export const presetsFor = (known: readonly string[]): AccessPreset[] => ACCESS_PRESETS.filter(p => p.scopes.every(s => known.includes(s)))

export type ExpiryChoice = 'never' | '30d' | '90d' | '1y' | 'keep'
export const EXPIRY_OPTIONS: { value: ExpiryChoice; label: string }[] = [
  { value: 'never', label: 'Never' }, { value: '30d', label: 'In 30 days' }, { value: '90d', label: 'In 90 days' }, { value: '1y', label: 'In 1 year' },
]
/** Rotating cannot say "never" (no expiry in the body keeps the current one), so its choices are keep, or a new date. */
export const ROTATE_EXPIRY_OPTIONS: { value: ExpiryChoice; label: string }[] = [{ value: 'keep', label: 'Keep the current expiry' }, ...EXPIRY_OPTIONS.filter(o => o.value !== 'never')]
/** An expired token has no expiry worth keeping: the server refuses a rotation without a new one (ApiTokenExpired). */
export const rotateOptionsFor = (t: Pick<ApiToken, 'state'>) => (t.state === 'Expired' ? ROTATE_EXPIRY_OPTIONS.filter(o => o.value !== 'keep') : ROTATE_EXPIRY_OPTIONS)
export const defaultRotateExpiry = (t: Pick<ApiToken, 'state'>): ExpiryChoice => (t.state === 'Expired' ? '90d' : 'keep')

/** A choice as the UTC ISO instant the API wants, computed from `now` (the browser clock, passed in so a test can fix it). Never and keep are null (not sent). */
export function expiryIso(choice: ExpiryChoice, now: Date): string | null {
  if (choice === 'never' || choice === 'keep') return null
  const d = new Date(now.getTime())
  if (choice === '1y') d.setUTCFullYear(d.getUTCFullYear() + 1)
  else d.setUTCDate(d.getUTCDate() + (choice === '30d' ? 30 : 90))
  return d.toISOString()
}

/** The POST body of a make. */
export const makeBody = (name: string, presetId: string, expiry: ExpiryChoice, now: Date) => ({ name: name.trim(), scopes: scopesOfPreset(presetId), expiresAt: expiryIso(expiry, now) })
/** The POST body of a rotate: `{}` unless a new expiry was chosen. */
export const rotateBody = (expiry: ExpiryChoice, now: Date): { expiresAt?: string } => { const at = expiryIso(expiry, now); return at ? { expiresAt: at } : {} }

/** Active is success, Expired warns, Revoked is neutral: the label says it as well (chipTone in ui/tone.ts uses the same words). */
export const stateTone = (s: TokenState): Tone => (s === 'Active' ? 'success' : s === 'Expired' ? 'warning' : 'neutral')

/** The reason Make is off, in words; null when the form is complete. */
export function makeProblem(name: string, presetId: string): string | null {
  if (!name.trim()) return 'Enter a name for the token.'
  if (name.trim().length > MAX_NAME) return `A name is at most ${MAX_NAME} characters.`
  if (scopesOfPreset(presetId).length === 0) return 'Choose what the token may do.'
  return null
}

export const REVOKED_REASON = 'This token is revoked; make a new one.'

/** Live tokens first, newest first; revoked ones last (the server sorts the same way; re-sorted after a local change). */
export const sortTokens = (rows: ApiToken[]): ApiToken[] =>
  [...rows].sort((a, b) => Number(a.state === 'Revoked') - Number(b.state === 'Revoked') || Date.parse(b.createdAt) - Date.parse(a.createdAt) || a.name.localeCompare(b.name))

export const summary = (rows: ApiToken[]) => ({ active: rows.filter(r => r.state === 'Active').length, total: rows.length })
/** The footer: "3 tokens · 2 active". */
export const footerText = (rows: ApiToken[]): string => { const s = summary(rows); return `${s.total} ${s.total === 1 ? 'token' : 'tokens'} · ${s.active} active` }

/** What a rotation does, in words for its confirmation. */
export const rotateImpact = (t: Pick<ApiToken, 'name'>, me: string): string =>
  `The old secret of ${t.name} stops working at once, so anything still using it fails until it has the new one. The new secret is shown once. From now on the token acts as ${me}.`
/** What a revocation does, in words for its confirmation. */
export const revokeImpact = (t: Pick<ApiToken, 'name'>): string =>
  `${t.name} stops working at once: every call that uses it is refused from now on. Its record stays, and it cannot be brought back; make a new token instead.`

/** A time as UTC with the person's own time beside it, or the word for none. */
export const whenText = (iso: string | null, none: string): string => (iso ? timeText(iso, { date: true }) : none)

/** The text fallback when the clipboard cannot be written (not a secure context, permission refused): says to select and copy by hand. */
export const COPY_FALLBACK = 'Copying did not work here. Select the token above and copy it yourself.'
