import { useEffect, useRef } from 'react'

/** Focus the element when `when` becomes true: an inline form's first field, a confirm button, a drawer heading (WCAG 2.4.3). */
export function useFocusWhen<T extends HTMLElement>(when: boolean) {
  const ref = useRef<T>(null)
  useEffect(() => { if (when) ref.current?.focus() }, [when])
  return ref
}

/**
 * For a drawer or inline panel: on mount remember what had focus, on unmount give it back if focus fell to <body>
 * (the control that opened it was not unmounted, but focus inside the panel went with the panel).
 */
export function useRestoreFocus() {
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    return () => {
      const lost = !document.activeElement || document.activeElement === document.body
      if (lost && opener && opener !== document.body && opener.isConnected) requestAnimationFrame(() => opener.focus())
    }
  }, [])
}

/** Move focus without scrolling the page twice (the caller has already scrolled) and without animating when reduced motion is asked for. */
export function scrollAndFocus(el: HTMLElement | null) {
  if (!el) return
  const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' })
  if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1')
  el.focus({ preventScroll: true })
}
