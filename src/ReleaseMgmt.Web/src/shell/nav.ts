import type { Route } from '../route'

/**
 * The sidebar's destinations (REOS-94, Figma node 6:4719), in the design's order. A destination with no screen yet opens a placeholder (`placeholder`),
 * never a 404. Screens that exist but that the design does not place (My work, Analytics, Templates, ...) are kept reachable in two extra places
 * (QUESTIONS Q-094a): after the design's Management items, and in a "More" group. Sync health is the control-plane card in the sidebar footer; the
 * Inbox is the bell in the utility bar.
 */
export type View = Route['view']
export type IconName = 'layout-dashboard' | 'calendar-days' | 'layers' | 'workflow' | 'shield-check' | 'activity' | 'history'
/** `module`: an add-on module's entry (REOS-174, D56): present only while that module is enabled in the workspace. */
export interface NavItem { view: View; label: string; icon?: IconName; placeholder?: string; need?: 'admin' | 'audit'; module?: string }
export interface NavGroup { id: 'workspace' | 'management' | 'more'; label?: string; items: NavItem[] }

export const NAV: NavGroup[] = [
  {
    id: 'workspace', label: 'Workspace', items: [
      { view: 'overview', label: 'Overview', icon: 'layout-dashboard' },
      { view: 'calendar', label: 'Portfolio & calendar', icon: 'calendar-days' },
      { view: 'trains', label: 'Releases', icon: 'layers' },
      { view: 'orchestration', label: 'Orchestration', icon: 'workflow' },
      { view: 'approvals', label: 'Approvals', icon: 'shield-check' },
      { view: 'deployments', label: 'Deployments', icon: 'activity' },
      { view: 'audit', label: 'Audit history', icon: 'history', need: 'audit' },
    ],
  },
  {
    id: 'management', items: [
      { view: 'services', label: 'Service catalog' },
      { view: 'environments', label: 'Environments' },
      { view: 'admin', label: 'Teams & settings', need: 'admin' },
      { view: 'templates', label: 'Templates' },
      { view: 'library', label: 'Comm library' },
      { view: 'connectors', label: 'Connectors', need: 'admin' },
      { view: 'cab', label: 'Change advisory board', module: 'cab' },   // REOS-174 gates it, REOS-176 builds the change request screens (src/cab)
      { view: 'ai', label: 'AI assistant', placeholder: 'ai', module: 'ai' },
      { view: 'integrations', label: 'Integrations', need: 'admin' },   // REOS-165: API tokens (REOS-162) and webhooks (REOS-164) in one page; /api-tokens and /webhooks still open on their own
    ],
  },
  { id: 'more', label: 'More', items: [{ view: 'work', label: 'My work' }, { view: 'analytics', label: 'Analytics' }, { view: 'importexport', label: 'Imports & exports' }] },
]

/** Pages reached outside the sidebar lists, with the names the breadcrumb, the document title and Jump to use. */
const OTHER: Partial<Record<View, string>> = { 'api-tokens': 'API tokens', webhooks: 'Webhooks', sync: 'Sync health', inbox: 'Inbox', gallery: 'Component gallery' }

export const canAdmin = (roles: string[]) => roles.includes('RTE') || roles.includes('ReleaseManager')
export const canAudit = (roles: string[]) => roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')

/**
 * The groups as this person sees them: items needing a role they do not hold are left out (the pages refuse them anyway), and so are an add-on module's items unless the module is
 * enabled (REOS-174). `modules` is the set of enabled module keys; null while it is not known yet, when no module's entry is shown (the core never waits for it).
 */
export function navFor(roles: string[], modules: ReadonlySet<string> | null = null): NavGroup[] {
  const allowed = (i: NavItem) => (i.need === undefined || (i.need === 'admin' ? canAdmin(roles) : canAudit(roles))) && (i.module === undefined || (modules?.has(i.module) ?? false))
  return NAV.map(g => ({ ...g, items: g.items.filter(allowed) }))
}

/** The module a page belongs to, if it belongs to one: its page is Page not found while the module is off. */
export function moduleOfView(view: View): string | undefined {
  for (const g of NAV) for (const i of g.items) if (i.view === view) return i.module
  return undefined
}

export const pathOf = (view: View) => (view === 'trains' ? '/' : `/${view}`)

/** The page's name: sidebar label, or the name of a page reached another way. */
export function pageName(view: View): string {
  for (const g of NAV) for (const i of g.items) if (i.view === view) return i.label
  return OTHER[view] ?? view
}

/** Destinations Jump to offers by name (every page this person can open). */
export function jumpTargets(roles: string[], modules: ReadonlySet<string> | null = null): { name: string; view: View }[] {
  const fromNav = navFor(roles, modules).flatMap(g => g.items.filter(i => i.view !== 'trains').map(i => ({ name: i.label, view: i.view })))
  return [...fromNav, { name: 'Sync health', view: 'sync' }, { name: 'Inbox', view: 'inbox' }]
}

export function placeholderOf(view: View): string | undefined {
  for (const g of NAV) for (const i of g.items) if (i.view === view) return i.placeholder
  return undefined
}
