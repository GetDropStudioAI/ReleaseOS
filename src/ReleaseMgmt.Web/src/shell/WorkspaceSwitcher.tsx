import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { Check, ChevronsUpDown } from 'lucide-react'
import type { Me } from '../api'
import { roleLabel } from '../workspaceApi'
import { switchTo } from '../workspace'
import type { WorkspaceInfo } from './Sidebar'
import { useWorkspaces } from './WorkspaceGate'

/**
 * The workspace in the sidebar (REOS-106, D45). A person with one workspace sees its name and region as plain text, without a menu. With several, the name
 * is a button that opens a menu of their workspaces (name, region, their role there); choosing one reloads the page in it (workspace.ts). Keyboard: Enter,
 * Space or Down opens on the current workspace; Up and Down move, Home and End jump, Enter or Space chooses, Escape closes and focus returns to the button.
 */
export function WorkspaceSwitcher({ me, workspace }: { me: Me; workspace: WorkspaceInfo | null }) {
  const { choices, current } = useWorkspaces()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const menuId = useId()
  const box = useRef<HTMLDivElement>(null)
  const button = useRef<HTMLButtonElement>(null)
  const items = useRef<(HTMLButtonElement | null)[]>([])
  const name = current?.name ?? workspace?.name ?? 'Loading workspace…'
  const region = current?.region ?? workspace?.region ?? null
  const detail = region ? `Workspace · ${region}` : 'Workspace'

  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    window.addEventListener('pointerdown', onDown)
    return () => window.removeEventListener('pointerdown', onDown)
  }, [open])
  useEffect(() => { if (open) items.current[active]?.focus() }, [open, active])

  if (choices.length < 2) {
    return (
      <div className="os-workspace">
        <p className="os-workspace-name">{name}</p>
        <p className="os-workspace-detail">{detail}</p>
      </div>
    )
  }

  const show = () => { setActive(Math.max(0, choices.findIndex(w => w.id === current?.id))); setOpen(true) }
  const close = () => { setOpen(false); button.current?.focus() }
  const choose = (slug: string) => { if (slug === current?.slug) { close(); return } switchTo(slug, me.id) }
  const onButtonKey = (e: KeyboardEvent) => { if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); show() } }
  const onMenuKey = (e: KeyboardEvent) => {
    const last = choices.length - 1
    const next = e.key === 'ArrowDown' ? (active + 1) % choices.length : e.key === 'ArrowUp' ? (active - 1 + choices.length) % choices.length
      : e.key === 'Home' ? 0 : e.key === 'End' ? last : null
    if (next !== null) { e.preventDefault(); setActive(next) }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close() }
    else if (e.key === 'Tab') setOpen(false)
  }

  return (
    <div className="os-workspace os-ws-switcher" ref={box}>
      <button ref={button} type="button" className="os-ws-button" aria-haspopup="menu" aria-expanded={open} aria-controls={open ? menuId : undefined}
        onClick={() => (open ? close() : show())} onKeyDown={onButtonKey}>
        <span className="os-ws-text">
          <span className="os-workspace-name">{name}</span>
          <span className="os-workspace-detail">{detail}</span>
        </span>
        <ChevronsUpDown className="os-ws-chevron" aria-hidden="true" size={16} strokeWidth={1.6} />
        <span className="sr-only">. Switch workspace</span>
      </button>
      {open && (
        <div id={menuId} className="os-ws-menu" role="menu" aria-label="Your workspaces" onKeyDown={onMenuKey}>
          {choices.map((w, i) => (
            <button key={w.id} ref={el => { items.current[i] = el }} type="button" role="menuitemradio" aria-checked={w.id === current?.id}
              tabIndex={i === active ? 0 : -1} className="os-ws-item" onClick={() => choose(w.slug)}>
              <span className="os-ws-item-text">
                <span className="os-ws-item-name">{w.name}</span>
                <span className="os-ws-item-detail">{[w.region, roleLabel(w.role)].filter(Boolean).join(' · ')}</span>
              </span>
              {w.id === current?.id && <Check aria-hidden="true" size={16} strokeWidth={1.8} />}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
