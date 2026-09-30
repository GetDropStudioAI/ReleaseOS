import { useEffect, useRef, useState } from 'react'

/** True when the key press belongs to something the user is typing into or pressing (an input, a button, a link): the grid must not swallow it. */
const typingOrActing = (t: EventTarget | null) => {
  const el = t as HTMLElement | null
  return !!el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON', 'A'].includes(el.tagName))
}

/**
 * Grid keyboard (docs/UI.md, Accessibility): j/k move the selected row, Enter opens it.
 * Ignored while focus is in an input, select, textarea, button or link, and with any modifier held.
 */
export function useRowNav(count: number, onOpen: (index: number) => void) {
  const [index, setIndex] = useState<number | null>(null)
  const open = useRef(onOpen); open.current = onOpen
  useEffect(() => { setIndex(i => (i !== null && i >= count ? (count ? count - 1 : null) : i)) }, [count])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || typingOrActing(e.target) || count === 0) return
      if (e.key === 'j') { e.preventDefault(); setIndex(i => (i === null ? 0 : Math.min(i + 1, count - 1))) }
      else if (e.key === 'k') { e.preventDefault(); setIndex(i => (i === null ? 0 : Math.max(i - 1, 0))) }
      else if (e.key === 'Enter' && index !== null) { e.preventDefault(); open.current(index) }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [count, index])
  return [index, setIndex] as const
}
