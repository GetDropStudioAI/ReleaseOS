import type { Route } from '../route'

/**
 * The sidebar's destinations (REOS-94, Figma node 6:4719), in the design's order. A destination with no screen yet opens a placeholder (`placeholder`),
 * never a 404. Screens that exist but that the design does not place (My work, Analytics, Templates, ...) are kept reachable in two extra places
 * (QUESTIONS Q-094a): after the design's Management items, and in a "More" group. Sync health is the control-plane card in the sidebar footer; the
 * Inbox is the bell in the utility bar.
 */
export type View = Route['view']
export type IconName = 'layout-dashboard' | 'calendar-days' | 'layers' | 'workflow' | 'shield-check' | 'activity' | 'history'
export interface NavItem { view: View; label: string; icon?: IconName; placeholder?: string; need?: 'admin' | 'audit' }
export interface NavGroup { id: 'workspace' | 'management' | 'more'; label?: string; items: NavItem[] }

export const NAV: NavGroup[] = [
  {
    id: 'workspace', label: 'Workspace', items: [
      { view: 'overview', label: 'Overview', icon: 'layout-dashboard' },
      { view: 'calendar', label: 'Portfolio & calendar', icon: 'calendar-days' },
      { view: 'trains', label: 'Releases', icon: 'layers' },
      { view: 'orchestration', label: 'Orchestration', icon: 'workflow', placeholder: 'REOS-98' },
      { view: 'approvals', label: 'Approvals', icon: 'shield-check' },
      { view: 'deployments', label: 'Deployments', icon: 'activity' },
      { view: 'audit', label: 'Audit history', icon: 'history', need: 'audit' },
    ],
  },
  {
    id: 'management', items: [
      { view: 'services', label: 'Service catalog', placeholder: 'B8' },
      { view: 'environments', label: 'Environments', placeholder: 'B6' },
      { view: 'admin', label: 'Teams & settings', need: 'admin' },
      { view: 'templates', label: 'Templates' },
      { view: 'library', label: 'Comm library' },
      { view: 'connectors', label: 'Connectors', need: 'admin' },
    ],
  },
  { id: 'more', label: 'More', items: [{ view: 'work', label: 'My work' }, { view: 'analytics', label: 'Analytics' }, { view: 'importexport', label: 'Imports & exports' }] },
]

/** Pages reached outside the sidebar lists, with the names the breadcrumb, the document title and Jump to use. */
const OTHER: Partial<Record<View, string>> = { sync: 'Sync health', inbox: 'Inbox', gallery: 'Component gallery' }

export const canAdmin = (roles: string[]) => roles.includes('RTE') || roles.includes('ReleaseManager')
export const canAudit = (roles: string[]) => roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')

/** The groups as this person sees them: items needing a role they do not hold are left out (the pages refuse them anyway). */
export function navFor(roles: string[]): NavGroup[] {
  const allowed = (i: NavItem) => i.need === undefined || (i.need === 'admin' ? canAdmin(roles) : canAudit(roles))
  return NAV.map(g => ({ ...g, items: g.items.filter(allowed) }))
}

export const pathOf = (view: View) => (view === 'trains' ? '/' : `/${view}`)

/** The page's name: sidebar label, or the name of a page reached another way. */
export function pageName(view: View): string {
  for (const g of NAV) for (const i of g.items) if (i.view === view) return i.label
  return OTHER[view] ?? view
}

/** Destinations Jump to offers by name (every page this person can open). */
export function jumpTargets(roles: string[]): { name: string; view: View }[] {
  const fromNav = navFor(roles).flatMap(g => g.items.filter(i => i.view !== 'trains').map(i => ({ name: i.label, view: i.view })))
  return [...fromNav, { name: 'Sync health', view: 'sync' }, { name: 'Inbox', view: 'inbox' }]
}

export function placeholderOf(view: View): string | undefined {
  for (const g of NAV) for (const i of g.items) if (i.view === view) return i.placeholder
  return undefined
}
