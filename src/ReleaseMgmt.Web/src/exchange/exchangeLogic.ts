import { stampOf } from '../deploy/deployLogic.ts'
import { browserZone } from '../shell/clock.ts'
import type { Tone } from '../ui'
import type { ImportJob, PlanRow } from '../exchangeApi.ts'

export const kb = (n: number) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`
export const shortSha = (s: string) => `${s.slice(0, 4)}…${s.slice(-4)}`

/** A time as the page writes it everywhere (D44): UTC labelled, and the person's own time beside it (null for a person who works in UTC). */
export const stamp = (iso: string | null | undefined, zone = browserZone()) => stampOf(iso ?? null, zone)

/** The diff marker of a preview row: a symbol and a word, plus a tone that only repeats them (the symbol alone never carries the meaning). */
export const MARK: Record<PlanRow['op'], { glyph: string; word: string; tone: Tone }> = {
  new: { glyph: '+', word: 'new', tone: 'success' },
  updated: { glyph: '~', word: 'updated', tone: 'warning' },
  unchanged: { glyph: '=', word: 'unchanged', tone: 'neutral' },
  error: { glyph: '✗', word: 'error', tone: 'danger' },
}

/** The result of a past import as a chip word and tone, with the detail that follows it. */
export function resultOf(job: ImportJob): { word: string; tone: Tone; detail: string } {
  if (job.status === 'Committed') return { word: 'Committed', tone: 'success', detail: job.counts ? `${job.counts.new} new, ${job.counts.updated} updated, ${job.counts.unchanged} unchanged` : '' }
  if (job.status === 'Rejected') return { word: 'Rejected', tone: 'danger', detail: job.errors[0]?.message ?? 'file refused' }
  if (job.status === 'Expired') return { word: 'Expired', tone: 'neutral', detail: '' }
  return job.errorCount > 0 ? { word: 'Errors', tone: 'danger', detail: `${job.errorCount} ${job.errorCount === 1 ? 'error' : 'errors'} · not committed` } : { word: 'Previewed', tone: 'info', detail: 'not committed' }
}
