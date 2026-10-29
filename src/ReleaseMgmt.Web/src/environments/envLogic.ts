import type { Tone } from '../ui'

/** The pure parts of the Environments screen (REOS-156): the rows the API returns, what a state or an SLO figure is called, and the reasons an action is off. Tested without a browser. */
export interface EnvironmentRow {
  id: string; name: string; region: string | null; kind: string; sortOrder: number; controlWebhook: { id: string; name: string; host: string } | null
  errorRateMaxPct: number | null; latencyP95MaxMs: number | null; availabilityMinPct: number | null
  sloAvailabilityPct: number | null; sloLatencyP95Ms: number | null; sloErrorRatePct: number | null
  archived: boolean; activeDeployments: number; version: number; thresholdForSeconds: number; canaryObservationSeconds: number; controlWebhookSigned: boolean
}
export interface SloMetric { objective: number | null; measured: number | null; meets: boolean | null }
export interface ErrorBudget { objectivePct: number; measuredPct: number; meets: boolean; budgetPct: number; consumedPct: number; remainingPct: number; exhausted: boolean }
export interface EnvironmentSlo {
  environmentId: string; environmentName: string; days: number; from: string; to: string; samples: number; rolledUpSamples: number
  availability: SloMetric; latencyP95: SloMetric; errorRate: SloMetric; errorBudget: ErrorBudget | null
}
export interface WebhookOption { id: string; name: string; kind: string; host: string }
/** The one response that carries the secret. It is held in component state only: never put in a draft, storage, the address bar or an announcement. */
export interface SigningKeyResult {
  secret: string; header: string; deliveryHeader: string; timestampHeader: string; scheme: string; toleranceSeconds: number; note: string
}

export const KINDS = ['Staging', 'Production']
export const SLO_DAYS = [7, 30, 90]
export const MAX_NAME = 100, MAX_REGION = 60

export const stateWord = (e: Pick<EnvironmentRow, 'archived'>) => (e.archived ? 'Archived' : 'Active')
export const stateTone = (e: Pick<EnvironmentRow, 'archived'>): Tone => (e.archived ? 'neutral' : 'success')

/** The table's order: active first, then the workspace's order, then name. */
export const sortEnvironments = (rows: EnvironmentRow[]): EnvironmentRow[] =>
  [...rows].sort((a, b) => Number(a.archived) - Number(b.archived) || a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))

/** Why Add stays off, in words; null when the form can be sent. The server has the last word (names are unique). */
export function addProblem(name: string, kind: string, region: string, existing: Pick<EnvironmentRow, 'name'>[]): string | null {
  const n = name.trim()
  if (n === '') return 'Enter a name for the environment.'
  if (n.length > MAX_NAME) return `A name is at most ${MAX_NAME} characters.`
  if (!KINDS.includes(kind)) return 'Choose Staging or Production.'
  if (region.trim().length > MAX_REGION) return `A region is at most ${MAX_REGION} characters.`
  if (existing.some(e => e.name.toLowerCase() === n.toLowerCase())) return `An environment called "${n}" exists.`
  return null
}

/** Why an environment cannot be archived yet, from what the row already says; null when it can. The server checks again (queued deployments count there). */
export const archiveBlock = (e: Pick<EnvironmentRow, 'activeDeployments' | 'archived' | 'name'>): string | null =>
  !e.archived && e.activeDeployments > 0 ? `${e.name} has ${e.activeDeployments} ${e.activeDeployments === 1 ? 'deployment' : 'deployments'} in progress. Archive it when they have finished.` : null

/** An SLO figure as a word: the chip beside it never relies on colour. */
export function sloWord(m: SloMetric): { word: string; tone: Tone } {
  if (m.objective === null) return { word: 'No target', tone: 'neutral' }
  if (m.meets === null) return { word: 'No data', tone: 'neutral' }
  return m.meets ? { word: 'Meets target', tone: 'success' } : { word: 'Misses target', tone: 'danger' }
}

const trim = (v: number, digits: number) => String(Number(v.toFixed(digits)))
export const pct = (v: number | null, digits = 3) => (v === null ? 'Not set' : `${trim(v, digits)}%`)
export const ms = (v: number | null) => (v === null ? 'Not set' : `${trim(v, 0)} ms`)

/** "99.95% of 99.9% target" style line for one SLO metric; the target is the direction in words so it is not read as a plain number. */
export function sloLine(m: SloMetric, fmt: (v: number | null) => string, better: 'higher' | 'lower'): string {
  if (m.objective === null) return `Measured ${m.measured === null ? 'no data' : fmt(m.measured)}; no target set`
  const target = `${fmt(m.objective)} ${better === 'higher' ? 'or more' : 'or less'}`
  return m.measured === null ? `Target ${target}; no samples in this range` : `Measured ${fmt(m.measured)}; target ${target}`
}

export function budgetLine(b: ErrorBudget | null): string {
  if (!b) return 'No error budget: it needs an availability target and measured availability.'
  if (b.exhausted) return `Exhausted: ${pct(b.consumedPct, 1)} of the ${pct(b.budgetPct, 3)} budget used.`
  return `${pct(b.remainingPct, 1)} of the ${pct(b.budgetPct, 3)} budget remains.`
}

/** What the signing-key panel says and offers for this row. `off` is the reason the button is disabled; null when it can be used. */
export function signingState(e: Pick<EnvironmentRow, 'controlWebhook' | 'controlWebhookSigned' | 'archived' | 'name'>, canEdit: boolean): { word: string; tone: Tone; action: string; off: string | null } {
  const has = e.controlWebhook !== null && e.controlWebhookSigned
  const base = has ? { word: 'Key set', tone: 'success' as Tone, action: 'Rotate signing key' } : { word: 'No key', tone: 'warning' as Tone, action: 'Create signing key' }
  if (!canEdit) return { ...base, off: 'Only an RTE or Release Manager can create signing keys.' }
  if (e.controlWebhook === null) return { word: 'No control webhook', tone: 'neutral', action: 'Create signing key', off: `Choose a control webhook for ${e.name} first.` }
  return { ...base, off: null }
}

/** The facts a receiver needs next to the one-time secret, as label/value pairs. */
export const keyFacts = (k: SigningKeyResult): { label: string; value: string }[] => [
  { label: 'Signature header', value: k.header }, { label: 'Delivery id header', value: k.deliveryHeader }, { label: 'Timestamp header', value: k.timestampHeader },
  { label: 'Signature', value: k.scheme }, { label: 'Timestamp tolerance', value: `${k.toleranceSeconds} seconds` },
]

/** The 409 message, same words on every management screen. */
export const conflictText = (what: string) => `Someone else changed ${what}. It has been reloaded; try again.`
