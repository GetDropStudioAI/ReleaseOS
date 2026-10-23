import type { MouseEvent } from 'react'
import { Activity, CalendarDays, History, Layers, Layers2, LayoutDashboard, ShieldCheck, Workflow, type LucideIcon } from 'lucide-react'
import type { Me } from '../api'
import { CountBadge } from '../ui'
import type { ControlPlane } from './health.ts'
import { navFor, pathOf, type IconName, type View } from './nav.ts'
import { UserMenu } from './UserMenu'
import { WorkspaceSwitcher } from './WorkspaceSwitcher'

const ICONS: Record<IconName, LucideIcon> = {
  'layout-dashboard': LayoutDashboard, 'calendar-days': CalendarDays, layers: Layers, workflow: Workflow, 'shield-check': ShieldCheck, activity: Activity, history: History,
}

export interface WorkspaceInfo { id: string; name: string; slug: string; region: string | null }

/**
 * The sidebar (REOS-94, Figma node 6:4719): brand, the workspace, the destinations in the design's order with the active one marked, the Approvals count
 * (hidden at zero), and the footer with the control-plane card (a link to Sync health) and the user card. Every destination is a real link, so it opens
 * in a new tab and the browser's history works.
 */
export function Sidebar({ me, view, workspace, approvals, plane, onGo, onSignedOut, onStopped }: {
  me: Me; view: View; workspace: WorkspaceInfo | null; approvals: number; plane: ControlPlane
  onGo: (v: View) => void; onSignedOut: () => void; onStopped: () => void
}) {
  const go = (v: View) => (e: MouseEvent<HTMLAnchorElement>) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return   // a new tab or window keeps the browser's own behaviour
    e.preventDefault(); onGo(v)
  }
  const region = workspace?.region?.toUpperCase()
  return (
    <aside className="os-sidebar" aria-label="Workspace">
      <div className="os-sidebar-top">
        <div className="os-brand">
          <span className="os-brand-mark" aria-hidden="true"><Layers2 size={18} strokeWidth={1.8} /></span>
          <span className="os-brand-name">ReleaseOS</span>
        </div>
        <WorkspaceSwitcher me={me} workspace={workspace} />
        <nav className="os-nav" aria-label="Primary">
          {navFor(me.roles).map(g => (
            <div key={g.id} className={`os-nav-group os-nav-group-${g.id}`}>
              {g.label && <p className="os-nav-label" id={`nav-${g.id}`}>{g.label}</p>}
              <ul aria-labelledby={g.label ? `nav-${g.id}` : undefined}>
                {g.items.map(i => {
                  const Icon = i.icon ? ICONS[i.icon] : null
                  return (
                    <li key={i.view}>
                      <a href={pathOf(i.view)} className="os-nav-item" aria-current={view === i.view ? 'page' : undefined} onClick={go(i.view)}>
                        {Icon && <Icon className="os-nav-icon" size={17} strokeWidth={1.6} aria-hidden="true" />}
                        <span className="os-nav-text">{i.label}</span>
                        {i.view === 'approvals' && <CountBadge count={approvals} label={approvals === 1 ? 'decision waiting for you' : 'decisions waiting for you'} />}
                      </a>
                    </li>
                  )
                })}
              </ul>
            </div>
          ))}
        </nav>
      </div>
      <div className="os-sidebar-foot">
        <a href="/sync" className={`os-plane ${plane.tone}`} aria-current={view === 'sync' ? 'page' : undefined} onClick={go('sync')}>
          <span className="os-plane-status"><span className="os-plane-dot" aria-hidden="true" />{plane.label}</span>
          {plane.detail && <span className="os-plane-detail">{plane.detail}</span>}
          <span className="os-plane-region">CONTROL PLANE{region ? ` / ${region}` : ''}</span>
          <span className="sr-only">. Open Sync health</span>
        </a>
        <UserMenu me={me} onSignedOut={onSignedOut} onStopped={onStopped} />
      </div>
    </aside>
  )
}
