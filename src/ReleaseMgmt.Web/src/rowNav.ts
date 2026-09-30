import { useCallback, useEffect, useRef, useState } from 'react'

/** Keys typed into a field belong to the field: j and k are letters there. */
const typing = (t: EventTarget | null) => {
  const el = t as HTMLElement | null
  return !!el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName))
}

/** The row's primary control: the one marked `data-rownav`, else its first button or link. */
export const primaryOf = (row: Element | null | undefined) =>
  row?.querySelector<HTMLElement>('[data-rownav]') ?? row?.querySelector<HTMLElement>('button, a[href]') ?? null

/**
 * Grid keyboard (docs/UI.md, Accessibility; WCAG 2.1.4 and 4.1.2): j / k move the selected row and Enter opens it, but only while focus is inside
 * the container the returned props are spread on (a table, or a wrapper around several tables whose body rows form one list). Moving also moves DOM
 * focus to the row's primary control, so a screen reader follows; callers mark that control `data-rownav` and expose the selection with aria-current.
 * Enter is taken only on the primary control (or the row itself): any other button in the row keeps its own Enter. Ignored in fields and with modifiers.
 * `onMove` lets a screen that selects by id follow the keyboard.
 */
export function useRowNav<T extends HTMLElement = HTMLTableElement>(count: number, onOpen: (index: number) => void, onMove?: (index: number) => void) {
  const [index, setIndex] = useState<number | null>(null)
  const ref = useRef<T>(null)
  const cb = useRef({ onOpen, onMove }); cb.current = { onOpen, onMove }
  useEffect(() => { setIndex(i => (i !== null && i >= count ? (count ? count - 1 : null) : i)) }, [count])
  const onKeyDown = useCallback((e: React.KeyboardEvent<T>) => {
    if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || typing(e.target) || count === 0 || !ref.current) return
    const rows = Array.from(ref.current.querySelectorAll('tbody tr'))
    if (rows.length === 0) return
    const tr = (e.target as HTMLElement).closest('tr')
    const at = tr ? rows.indexOf(tr) : -1
    const cur = at >= 0 ? at : index
    if (e.key === 'j' || e.key === 'k') {
      e.preventDefault()
      const j = cur === null ? 0 : Math.min(Math.max(cur + (e.key === 'j' ? 1 : -1), 0), rows.length - 1)
      setIndex(j); cb.current.onMove?.(j)
      const target = primaryOf(rows[j])
      target?.focus(); target?.scrollIntoView?.({ block: 'nearest' })
    } else if (e.key === 'Enter' && at >= 0 && (e.target === tr || e.target === primaryOf(tr))) {
      e.preventDefault(); setIndex(at); cb.current.onOpen(at)
    }
  }, [count, index])
  return [index, setIndex, { ref, onKeyDown }] as const
}
