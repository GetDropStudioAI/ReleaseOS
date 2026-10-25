import type { ChainBrief, ChainStep, NextOption, VersionView } from './versionsApi.ts'

/** The pure parts of versions (REOS-117, RT2, D48): how a chain reads, what "create the next release" asks for, and why it is off. Tested without a browser. */

const CLOSED = ['Complete', 'Aborted']
export const isOpen = (status: string) => !CLOSED.includes(status)

/** "Orion 5.2 · Beta 2, 2 of 4": the compact line the calendar and the overview put under a release's name. */
export const briefText = (b: ChainBrief) => `${b.versionName} · ${b.label}, ${b.position} of ${b.count}`

/** "Beta 1 → Beta 2 → CERT": the chain as one line of words, for a screen-reader name and a tooltip. */
export const chainWords = (steps: ChainStep[]) => steps.map(s => `${s.label} (${s.status.toLowerCase()}, ${s.target})`).join(', then ')

/** The warning against a release by train id, so a strip can mark the step it belongs to. */
export const warningsById = (v: Pick<VersionView, 'warnings'>): Record<string, string> => Object.fromEntries(v.warnings.map(w => [w.trainId, w.message]))

/** The option "Create the next release" starts on: the first place after this release. */
export const firstNext = (next: NextOption[]): NextOption | undefined => next[0]

/** What the new release is called unless the person types another title: the version and the label ("Orion 5.2 Beta 2"). */
export const defaultTitle = (versionName: string, option: NextOption | undefined) => option ? `${versionName} ${option.label}` : ''

/** Why Create next release is off, in words; null when it can go. The date is a calendar date, YYYY-MM-DD. */
export function nextProblem(option: NextOption | undefined, title: string, date: string): string | null {
  if (!option) return 'Choose which release comes next.'
  if (title.trim() === '') return 'Enter a title.'
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return 'Choose a target date.'
  return null
}

/** The reason a release cannot go on in its chain, from what the page already knows; null when it can. */
export function endOfChain(v: VersionView | null, next: NextOption[]): string | null {
  if (!v) return 'This release is not in a version.'
  if (v.archived) return `The version ${v.name} is archived: it takes no new release.`
  if (next.length === 0) return 'Every place after this release is taken: the chain has nowhere left to go.'
  return null
}

/** The version's name for the picker, with how many releases it already has. */
export const versionOption = (v: VersionView) => ({ value: v.id, label: `${v.name} (${v.chain.length} ${v.chain.length === 1 ? 'release' : 'releases'})` })

/** Steps for a strip from a version view (the release page and the settings tab have the whole version). */
export const stepsOf = (v: VersionView): ChainStep[] => v.chain.map(c => ({ trainId: c.trainId, label: c.label, status: c.status, target: c.target }))
