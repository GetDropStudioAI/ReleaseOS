import type { NamedRef } from '../api.ts'

/**
 * The pure parts of add-on modules (REOS-174; D56, D57, D63): the row GET /modules returns, the words the settings page and its dialogs use, and which modules the navigation shows.
 * Modules are enabled with a button and a confirmation dialog, never a toggle switch (CLAUDE.md rule 9). Tested without a browser.
 */
export interface ModuleRow {
  key: string; name: string; summary: string; turnsOn: string[]
  enabled: boolean; entitled: boolean; entitlementReason: string | null; planSource: string
  enabledBy: NamedRef | null; enabledAt: string | null; lastChangedAt: string | null
  /** The If-Match value: 0 while the workspace has never enabled the module. */
  version: number
}

/** The keys of the modules that are on: the navigation shows an add-on's entries only for these. */
export const enabledKeys = (rows: ModuleRow[]): Set<string> => new Set(rows.filter(r => r.enabled).map(r => r.key))

export const stateLabel = (r: ModuleRow): string => (r.enabled ? 'Enabled' : 'Not enabled')
export const enableLabel = (r: ModuleRow): string => `Enable ${r.name}`
export const disableLabel = (r: ModuleRow): string => `Disable ${r.name}`

/** What the plan says, in words. */
export const planLabel = (r: ModuleRow): string => (!r.entitled ? 'Not in your plan' : r.planSource === 'Plan' ? 'Included in your plan' : 'Available')

/** Why Enable is off; null when it can be used. The plan's own reason is shown when it gave one. */
export function enableBlocker(r: ModuleRow): string | null {
  return r.entitled ? null : (r.entitlementReason ?? `Your plan does not include the ${r.name}.`)
}

/** The sentence a confirmation dialog opens with. */
export const enableIntro = (r: ModuleRow): string => `Enabling the ${r.name} turns on, for everyone in this workspace:`
export const disableIntro = (r: ModuleRow): string => `Disabling the ${r.name} turns these off again:`
export const DATA_KEPT = 'Nothing is deleted. The module’s data is kept, and it comes back as it was if you enable the module again.'

/** Enabled modules first in the table's order, then the others; the registry's own order within each. */
export const sortModules = (rows: ModuleRow[]): ModuleRow[] => [...rows]

/** The settings page's summary line. */
export function summary(rows: ModuleRow[]): string {
  const on = rows.filter(r => r.enabled).length
  return `${on} of ${rows.length} enabled`
}
