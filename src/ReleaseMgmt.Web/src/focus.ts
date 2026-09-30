import { useEffect, useRef } from 'react'

/** Focus the element when `when` becomes true: an inline form's first field, a confirm button, a drawer heading (WCAG 2.4.3). */
export function useFocusWhen<T extends HTMLElement>(when: boolean) {
  const ref = useRef<T>(null)
  useEffect(() => { if (when) ref.current?.focus() }, [when])
  return ref
}

/**
 * For a drawer or inline panel: on mount remember what had focus, on unmount give it back if focus fell to <body>.
 * The opener may have been re-rendered while the panel was open (its screen reloaded), so `fallback` names the trigger by selector:
 * when the remembered element is gone, focus goes to whatever matches it now.
 */
export function useRestoreFocus(fallback?: string) {
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    return () => {
      // Decide on the next frame: at unmount time the browser may still report the removed heading as the active element.
      requestAnimationFrame(() => {
        const a = document.activeElement
        const lost = !a || a === document.body || !a.isConnected
        if (!lost) return
        const target = opener && opener !== document.body && opener.isConnected ? opener : fallback ? document.querySelector<HTMLElement>(fallback) : null
        target?.focus()
      })
    }
  }, [fallback])
}

/** Move focus without scrolling the page twice (the caller has already scrolled) and without animating when reduced motion is asked for. */
export function scrollAndFocus(el: HTMLElement | null) {
  if (!el) return
  const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' })
  if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1')
  el.focus({ preventScroll: true })
}
