import { useEffect, useRef, type ReactNode } from 'react'
import { SidePanel } from './ui'

// Like App's page focus, only a selection the user made moves focus: a deep link or a restored session opening the panel must not.
let interacted = false
if (typeof window !== 'undefined') for (const t of ['pointerdown', 'keydown']) window.addEventListener(t, () => { interacted = true }, true)

/**
 * The frame of the right-hand Inspector (REOS-124): the shared SidePanel with "Close inspector". Focus (WCAG 2.4.3): when a selection the person made opens it,
 * the title takes focus once it has loaded (`loading` false) and the opener is remembered; when the frame goes away and focus fell to <body>, the opener gets it back.
 * `focusKey` names what is shown, so choosing another gate moves focus again.
 */
export function InspectorFrame({ focusKey, title, subtitle, loading, onClose, children }: { focusKey: string; title: ReactNode; subtitle?: ReactNode; loading?: boolean; onClose: () => void; children?: ReactNode }) {
  const head = useRef<HTMLHeadingElement>(null)
  const done = useRef<string | null>(null), opener = useRef<HTMLElement | null>(null)
  useEffect(() => {
    if (loading || done.current === focusKey) return
    done.current = focusKey
    if (!interacted) return
    if (!opener.current || !opener.current.isConnected) opener.current = document.activeElement as HTMLElement | null
    head.current?.focus()
  })
  useEffect(() => () => {
    const o = opener.current
    // decide on the next frame: at unmount the browser may still report the removed heading as the active element
    requestAnimationFrame(() => { const a = document.activeElement; if ((!a || a === document.body || !a.isConnected) && o?.isConnected) o.focus() })
  }, [])
  return <SidePanel title={title} subtitle={subtitle} titleRef={head} closeLabel="Close inspector" onClose={onClose}>{children}</SidePanel>
}
